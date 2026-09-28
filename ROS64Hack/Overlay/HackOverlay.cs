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
    ///   • Redraws at ~60 fps via a high-resolution System.Threading.Timer
    ///   • Drives the full game loop: camera update → entity enumeration → ESP draw
    ///
    /// Hotkeys (checked in the draw-loop thread):
    ///   INSERT = toggle menu
    ///   DELETE = exit
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
        private          MenuForm     _menu;

        private List<EntityData> _snapshot = new(64);
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

        private static readonly string[] GAME_WINDOW_TITLES =
            { "ROS Legacy", "Rules of Survival", "ROS", "ros64" };

        // ── Constructor ────────────────────────────────────────────────────────

        public HackOverlay(Mem mem)
        {
            _mem      = mem;
            _camera   = new Camera(mem);
            _player   = new PlayerReader(mem);
            _entities = new EntityLoop(mem, _player);

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

            // Draw loop — 30 fps (enough for ESP, much lower CPU than 60fps)
            _drawTimer = new System.Threading.Timer(DrawLoop, null, 0, 33);
        }

        private bool _paintPending = false;
        private int  _snapFrameCounter = 0;

        // ── Read loop (background thread) ────────────────────────────────────

        private void ReadLoop(object _)
        {
            if (!_running) return;
            try
            {
                _camera.Update();

                long emPtr = _mem.Read<long>(_mem.BaseAddress + Offsets.EM_STATIC_PTR, "OVERLAY.EM");
                if (emPtr == 0) return;

                var local  = _player.ReadPlayer(emPtr);
                var list   = _entities.Enumerate();

                lock (_snapshotLock)
                {
                    _localPlayer = local;
                    _snapshot    = list;
                }

                // Snap overlay to game window every ~2 seconds (60 reads = 2s at 33ms)
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
        }

        // ── Draw loop (background thread → Invoke to UI) ──────────────────────

        private void DrawLoop(object _)
        {
            if (!_running) return;

            // Hotkeys — MUST dispatch to UI thread (WinForms requirement)
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

            // Aimbot + NoClip are thread-safe (no UI touch)
            lock (_snapshotLock)
            {
                var cam = _camera.Data;
                Aimbot.Tick(_snapshot, cam, _screenW, _screenH);
                if (_localPlayer != null)
                    NoClip.Tick(_mem, _localPlayer, cam);
                RuntimeDiagnostics.Frame(cam, _snapshot);
            }

            // Trigger repaint — only if previous paint already completed
            if (!_paintPending && !IsDisposed)
            {
                _paintPending = true;
                BeginInvoke((Action)Invalidate);
            }
        }

        // ── Paint ─────────────────────────────────────────────────────────────

        protected override void OnPaint(PaintEventArgs e)
        {
            _paintPending = false;   // allow next frame to queue

            var g = e.Graphics;
            g.SmoothingMode     = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.Clear(Color.Black);

            var cam = _camera.Data;
            List<EntityData> snap;
            lock (_snapshotLock) { snap = _snapshot; }

            ESP.Draw(g, snap, cam, _screenW, _screenH);
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
            _mem?.Dispose();
            BeginInvoke((Action)Application.Exit);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            _running = false;
            _drawTimer?.Dispose();
            _readTimer?.Dispose();
            base.OnFormClosing(e);
        }
    }
}
