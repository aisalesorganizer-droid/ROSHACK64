using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using ROS64Hack.Core;
using ROS64Hack.Engine;
using ROS64Hack.Features;

namespace ROS64Hack.Overlay
{
    /// <summary>
    /// Full-screen transparent overlay window.
    ///
    ///   • Positioned and sized to match the game window (FindWindow → GetClientRect)
    ///   • WS_EX_LAYERED + WS_EX_TRANSPARENT → fully click-through
    ///   • TransparencyKey = Color.Black → black pixels are transparent
    ///   • Redraws at ~30 fps via System.Threading.Timer
    ///   • Drives the full game loop: camera update → entity enumeration → ESP draw
    ///
    /// Hotkeys (checked in the draw-loop thread):
    ///   INSERT = toggle menu
    ///   DELETE = exit
    ///
    /// THREADING MODEL:
    ///   ReadLoop  → camera refresh + entity enumeration → publishes one snapshot
    ///   DrawLoop  → consumes the snapshot for aimbot/noclip + schedules repaint
    ///   OnPaint   → consumes the same snapshot and performs zero RPM reads
    ///
    /// The read/draw callbacks have overlap guards so a slow callback cannot pile up
    /// concurrent timer work. Camera selection stays on the read thread.
    /// </summary>
    public class HackOverlay : Form
    {
        // ── Win32 ──────────────────────────────────────────────────────────────

        [DllImport("user32.dll")]
        private static extern IntPtr FindWindow(string cls, string title);

        [DllImport("user32.dll")]
        private static extern bool GetClientRect(IntPtr hWnd, out RECT rc);

        [DllImport("user32.dll")]
        private static extern bool ClientToScreen(IntPtr hWnd, ref POINT pt);

        [DllImport("user32.dll")]
        private static extern int  SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
        [DllImport("user32.dll")]
        private static extern int  GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        private static extern bool SetLayeredWindowAttributes(
            IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        private const int GWL_EXSTYLE          = -20;
        private const int VK_RBUTTON           = 0x02;
        private const int WS_EX_LAYERED        = 0x00080000;
        private const int WS_EX_TRANSPARENT    = 0x00000020;
        private const int WS_EX_TOPMOST        = 0x00000008;
        private const uint LWA_COLORKEY        = 0x00000001;

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X, Y; }

        // ── State ──────────────────────────────────────────────────────────────

        private readonly Mem          _mem;
        private readonly Camera       _camera;
        private readonly PlayerReader _player;
        private readonly EntityLoop   _entities;
        private readonly EntityModelCorrelationDiagnostics _entityModelDiag;
        private          MenuForm     _menu;

        private List<EntityData> _snapshot = new(64);
        private CameraData       _snapshotCamera;
        private EntityData       _localPlayer;
        private object           _snapshotLock = new();

        private float _screenW = Offsets.DEFAULT_SCREEN_W;
        private float _screenH = Offsets.DEFAULT_SCREEN_H;

        private System.Threading.Timer _drawTimer;
        private System.Threading.Timer _readTimer;

        private bool _menuVisible = false;
        private bool _insertWas   = false;
        private bool _deleteWas   = false;
        private bool _running     = true;

        private int _readBusy;
        private int _drawBusy;
        private int _paintPending;

        private static readonly string[] GAME_WINDOW_TITLES =
            { "ROS Legacy", "Rules of Survival", "ROS", "ros64" };

        // ── Constructor ────────────────────────────────────────────────────────

        public HackOverlay(Mem mem)
        {
            _mem      = mem;
            _camera   = new Camera(mem);
            _player   = new PlayerReader(mem);
            _entities = new EntityLoop(mem, _player);
            _entityModelDiag = new EntityModelCorrelationDiagnostics(mem);

            ConfigureForm();
        }

        // ── Form setup ─────────────────────────────────────────────────────────

        private void ConfigureForm()
        {
            Text            = "";
            FormBorderStyle = FormBorderStyle.None;
            BackColor       = Color.Black;
            TransparencyKey = Color.Black;
            TopMost         = true;
            ShowInTaskbar   = false;
            DoubleBuffered  = true;

            // Cover the full primary screen by default; will snap to game window
            Bounds = Screen.PrimaryScreen.Bounds;
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOPMOST;
                return cp;
            }
        }

        // ── Load ───────────────────────────────────────────────────────────────

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);

            SetWindowLong(Handle, GWL_EXSTYLE,
                GetWindowLong(Handle, GWL_EXSTYLE)
                | WS_EX_LAYERED | WS_EX_TRANSPARENT);
            SetLayeredWindowAttributes(Handle, 0x000000, 0, LWA_COLORKEY);

            _camera.Acquire();
            _entities.Calibrate();
            SnapToGameWindow();

            // Memory read loop — 30 Hz
            _readTimer = new System.Threading.Timer(ReadLoop, null, 0, 33);

            // Targeted Entity -> ModelSkeletal -> SpaceNode evidence capture.
            // It runs on its own slow timer and uses quiet RPM calls, so the normal
            // camera/W2S/ESP/aim loop and its RPM telemetry remain unchanged.
            _entityModelDiag.Start();

            // Draw loop — 30 fps (enough for ESP, much lower CPU than 60fps)
            _drawTimer = new System.Threading.Timer(DrawLoop, null, 0, 33);
        }

        private int  _snapFrameCounter = 0;
        private long _scopeProbePtr = 0;
        private float _scopeProbeLastSX = 0f;
        private float _scopeProbeLastSY = 0f;
        private float _scopeProbeLastX = 0f;
        private float _scopeProbeLastY = 0f;
        private float _scopeProbeLastZ = 0f;
        private bool _scopeProbeHasLast = false;
        private long _scopeProbeLastTicks = 0;
        private int _scopeProbeCount = 0;

        // ── Read loop (background thread) ────────────────────────────────────

        private void ReadLoop(object _)
        {
            if (!_running) return;
            if (Interlocked.Exchange(ref _readBusy, 1) != 0)
            {
                RuntimeDiagnostics.ReadLoopSkipped();
                return;
            }

            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                long emPtr = _mem.Read<long>(_mem.BaseAddress + Offsets.EM_STATIC_PTR, "OVERLAY.EM");
                if (emPtr == 0) return;

                var local = _player.ReadPlayer(emPtr);

                float px = 0f, py = 0f, pz = 0f;
                if (local != null)
                {
                    px = local.X;
                    py = local.Y;
                    pz = local.Z;
                }

                // Sample ADS/right-click on the same read tick used to capture the
                // camera/entity snapshot. This is diagnostic-only and does not alter
                // camera selection or recovery behavior.
                bool adsDown = (GetAsyncKeyState(VK_RBUTTON) & 0x8000) != 0;

                // Camera selection/refresh is read-thread only. Update() now reads
                // the selected camera rather than scanning the entire camera pool.
                if (!_camera.Update(px, py, pz, adsDown))
                    return;
                CameraData cam = _camera.Data;

                var list = _entities.Enumerate();

                // Diagnostic-only coherent snapshot hook. No feature reads from or
                // mutates the diagnostic's result.
                _entityModelDiag.ObserveSnapshot(local, list, emPtr);

                ScopeProjectionProbe(list, cam, adsDown);

                // Publish one coherent camera+entity snapshot. Paint never reads game
                // memory; it consumes only this snapshot.
                lock (_snapshotLock)
                {
                    _localPlayer   = local;
                    _snapshot      = list;
                    _snapshotCamera = cam;
                }

                _snapFrameCounter++;
                if (_snapFrameCounter >= 60)
                {
                    _snapFrameCounter = 0;
                    SnapToGameWindow();
                }

            }
            catch (Exception ex)
            {
                Console.WriteLine($"  [DIAG][READ-LOOP-EXCEPTION] {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                RuntimeDiagnostics.ReadLoopCompleted(
                    (System.Diagnostics.Stopwatch.GetTimestamp() - started) * 1000.0 /
                    System.Diagnostics.Stopwatch.Frequency);
                Volatile.Write(ref _readBusy, 0);
            }
        }

        // Focused 8x/scope projection probe. It observes exactly one stable enemy
        // target while ADS is held. It does not alter ESP or aimbot behavior.
        private void ScopeProjectionProbe(List<EntityData> entities, in CameraData cam, bool adsDown)
        {
            if (!adsDown || !cam.IsValid) return;

            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (_scopeProbeLastTicks != 0 &&
                (now - _scopeProbeLastTicks) * 1000.0 / System.Diagnostics.Stopwatch.Frequency < 500.0)
                return;
            _scopeProbeLastTicks = now;
            _scopeProbeCount++;

            EntityData target = null;
            float best = float.MaxValue;
            float cx = _screenW * 0.5f;
            float cy = _screenH * 0.5f;

            // Keep one enemy pointer across samples whenever possible. This makes
            // dWorld/dScreen a meaningful continuity measurement instead of silently
            // comparing two different enemies.
            if (_scopeProbePtr != 0)
            {
                foreach (var e in entities)
                {
                    if (e.IsEnemy && e.Ptr == _scopeProbePtr)
                    {
                        if (WorldToScreen.Project(
                                e.X, e.Y, e.Z, cam, _screenW, _screenH,
                                out _, out _, e.Ptr))
                        {
                            target = e;
                        }
                        break;
                    }
                }
            }

            if (target == null)
            {
                foreach (var e in entities)
                {
                    if (!e.IsEnemy) continue;

                    if (!WorldToScreen.Project(
                            e.X, e.Y, e.Z, cam, _screenW, _screenH,
                            out float sx, out float sy, e.Ptr))
                        continue;

                    float dx = sx - cx;
                    float dy = sy - cy;
                    float d2 = dx*dx + dy*dy;
                    if (d2 < best)
                    {
                        best = d2;
                        target = e;
                    }
                }

                if (target != null)
                    _scopeProbeHasLast = false;
            }

            float aspect = _screenW / _screenH;
            float ar = (cam.XFocal > 0.3f && cam.XFocal < 10f)
                ? cam.YFocal / cam.XFocal
                : cam.YFocal / aspect;

            if (target == null)
            {
                Console.WriteLine(
                    $"  [W2S][ADS-PROBE] n={_scopeProbeCount} target=NONE " +
                    $"YF={cam.YFocal:F5} XF={cam.XFocal:F5} ar={ar:F5}");
                _scopeProbePtr = 0;
                _scopeProbeHasLast = false;
                return;
            }

            WorldToScreen.Project(target.X, target.Y, target.Z,
                cam, _screenW, _screenH,
                out float sx0, out float sy0, target.Ptr);

            const float HeadHeight = 21.5f;
            bool headOk = WorldToScreen.Project(target.X, target.Y + HeadHeight, target.Z,
                cam, _screenW, _screenH,
                out float hsx, out float hsy, target.Ptr);

            float dxw = 0f, dyw = 0f, dzw = 0f, dsx = 0f, dsy = 0f;
            if (_scopeProbeHasLast && _scopeProbePtr == target.Ptr)
            {
                dxw = target.X - _scopeProbeLastX;
                dyw = target.Y - _scopeProbeLastY;
                dzw = target.Z - _scopeProbeLastZ;
                dsx = sx0 - _scopeProbeLastSX;
                dsy = sy0 - _scopeProbeLastSY;
            }

            float vx = (target.X - cam.PosX) * cam.RightX +
                       (target.Y - cam.PosY) * cam.RightY +
                       (target.Z - cam.PosZ) * cam.RightZ;
            float vy = (target.X - cam.PosX) * cam.UpX +
                       (target.Y - cam.PosY) * cam.UpY +
                       (target.Z - cam.PosZ) * cam.UpZ;
            float vz = (target.X - cam.PosX) * cam.FwdX +
                       (target.Y - cam.PosY) * cam.FwdY +
                       (target.Z - cam.PosZ) * cam.FwdZ;

            Console.WriteLine(
                $"  [W2S][ADS-PROBE] n={_scopeProbeCount} ptr=0x{target.Ptr:X} " +
                $"world=({target.X:F2},{target.Y:F2},{target.Z:F2}) " +
                $"view=({vx:F2},{vy:F2},{vz:F2}) " +
                $"foot=({sx0:F2},{sy0:F2}) headOk={(headOk ? 1 : 0)} head=({hsx:F2},{hsy:F2}) " +
                $"YF={cam.YFocal:F5} XF={cam.XFocal:F5} ar={ar:F5} " +
                $"dWorld=({dxw:F2},{dyw:F2},{dzw:F2}) dScreen=({dsx:F2},{dsy:F2})");

            _scopeProbePtr = target.Ptr;
            _scopeProbeLastX = target.X;
            _scopeProbeLastY = target.Y;
            _scopeProbeLastZ = target.Z;
            _scopeProbeLastSX = sx0;
            _scopeProbeLastSY = sy0;
            _scopeProbeHasLast = true;
        }

        // ── Draw loop (background thread → Invoke to UI) ──────────────────────

        private void DrawLoop(object _)
        {
            if (!_running) return;
            if (Interlocked.Exchange(ref _drawBusy, 1) != 0)
            {
                RuntimeDiagnostics.DrawLoopSkipped();
                return;
            }

            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                bool insertNow = (GetAsyncKeyState((int)Keys.Insert) & 0x8000) != 0;
                if (insertNow && !_insertWas && !IsDisposed)
                    BeginInvoke((Action)ToggleMenu);
                _insertWas = insertNow;

                bool deleteNow = (GetAsyncKeyState((int)Keys.Delete) & 0x8000) != 0;
                if (deleteNow && !_deleteWas)
                {
                    _running = false;
                    if (!IsDisposed) BeginInvoke((Action)ExitHack);
                }
                _deleteWas = deleteNow;

                lock (_snapshotLock)
                {
                    var cam = _snapshotCamera;
                    Aimbot.Tick(_snapshot, cam, _screenW, _screenH);
                    if (_localPlayer != null)
                        NoClip.Tick(_mem, _localPlayer, cam);
                    RuntimeDiagnostics.Frame(cam, _snapshot);
                }

                if (!IsDisposed && Interlocked.Exchange(ref _paintPending, 1) == 0)
                    BeginInvoke((Action)Invalidate);

            }
            finally
            {
                RuntimeDiagnostics.DrawLoopCompleted(
                    (System.Diagnostics.Stopwatch.GetTimestamp() - started) * 1000.0 /
                    System.Diagnostics.Stopwatch.Frequency);
                Volatile.Write(ref _drawBusy, 0);
            }
        }

        // ── Paint ─────────────────────────────────────────────────────────────

        protected override void OnPaint(PaintEventArgs e)
        {
            Volatile.Write(ref _paintPending, 0);

            var g = e.Graphics;
            g.SmoothingMode     = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.Clear(Color.Black);

            // Paint is deliberately RPM-free. Use the same camera+entity snapshot
            // published by ReadLoop so both datasets describe the same read cycle.
            lock (_snapshotLock)
            {
                ESP.Draw(g, _snapshot, _snapshotCamera, _screenW, _screenH);
            }
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private void SnapToGameWindow()
        {
            IntPtr hwnd = IntPtr.Zero;
            foreach (var title in GAME_WINDOW_TITLES)
            {
                hwnd = FindWindow(null, title);
                if (hwnd != IntPtr.Zero) break;
            }
            if (hwnd == IntPtr.Zero) return;

            if (!GetClientRect(hwnd, out RECT rc)) return;
            var origin = new POINT();
            ClientToScreen(hwnd, ref origin);

            int w = rc.Right  - rc.Left;
            int h = rc.Bottom - rc.Top;
            if (w < 100 || h < 100) return;

            _screenW = w;
            _screenH = h;

            if (!IsDisposed)
                BeginInvoke((Action)(() =>
                {
                    SetBounds(origin.X, origin.Y, w, h);
                }));
        }

        private void ToggleMenu()
        {
            if (!_menuVisible || _menu == null || _menu.IsDisposed)
            {
                _menu = new MenuForm();
                _menu.Show();
                _menuVisible = true;
            }
            else
            {
                _menu.Hide();
                _menuVisible = false;
            }
        }

        private void ExitHack()
        {
            _running = false;
            _drawTimer?.Dispose();
            _readTimer?.Dispose();
            _entityModelDiag?.Dispose();
            _mem?.Dispose();
            BeginInvoke((Action)Application.Exit);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            _running = false;
            _drawTimer?.Dispose();
            _readTimer?.Dispose();
            _entityModelDiag?.Dispose();
            base.OnFormClosing(e);
        }
    }
}
