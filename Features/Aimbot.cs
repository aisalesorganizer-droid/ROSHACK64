using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using ROS64Hack.Core;
using ROS64Hack.Engine;

namespace ROS64Hack.Features
{
    /// <summary>
    /// Aimbot — selects the closest-to-crosshair enemy within the FOV radius
    /// and applies smooth mouse movement toward it.
    ///
    /// Activation: hold Settings.AimbotKey (default: Shift).
    /// Targeting:  picks the entity whose screen position is closest to screen centre.
    /// Response:   partial move each frame by Settings.AimbotSmoothing factor.
    /// Bone:       HEAD = entity XYZ + (0, +21.5 raw units, 0), matching ESP.
    ///             BODY = entity XYZ root.
    /// </summary>
    public static class Aimbot
    {
        // ── Win32 mouse movement ──────────────────────────────────────────────

        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT
        {
            public uint type;
            public MOUSEINPUT mi;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MOUSEINPUT
        {
            public int dx, dy;
            public uint mouseData, dwFlags, time;
            public IntPtr dwExtraInfo;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        private const uint INPUT_MOUSE         = 0;
        private const uint MOUSEEVENTF_MOVE    = 0x0001;

        // ── Tracking state ─────────────────────────────────────────────────────

        // The pointer is the entity identity exposed by the existing EntityData
        // snapshot. Keep one target across frames instead of reselecting by
        // crosshair distance on every tick.
        private static long _lockedTargetPtr;
        private static int _switchCandidateFrames;

        // Time-based target filtering and aim response. This removes frame-rate
        // dependence from the old "delta * 0.85" per-frame response.
        private static long _lastTickTicks;
        private static bool _filterInitialized;
        private static float _filteredAimX;
        private static float _filteredAimY;
        private static long _lastValidTargetTicks;

        // Target retention: a challenger must be meaningfully better for several
        // consecutive ticks before the lock is allowed to change.
        private const float TargetSwitchMarginPx = 24.0f;
        private const int TargetSwitchConfirmFrames = 3;
        private const float TargetRetainFovMultiplier = 1.75f;
        private const double TargetLostGraceSeconds = 0.10;

        // Approximate time constant for the screen-space target filter. Lower is
        // faster/tighter; higher is steadier but adds more lag.
        private const float TargetFilterTauSeconds = 0.025f;
        private const float AimDeadzonePx = 0.85f;

        // ── Public API ────────────────────────────────────────────────────────

        /// <summary>
        /// Call every frame. Checks if AimbotKey is held, maintains a stable target,
        /// filters the target head position, and moves the mouse toward it.
        /// </summary>
        public static void Tick(
            List<EntityData> entities,
            in CameraData cam,
            float screenW, float screenH)
        {
            bool shiftDown = IsShiftDown();
            bool activationDown = shiftDown || IsAimbotKeyDown();
            RuntimeDiagnostics.Aimbot(Settings.AimbotEnabled, Settings.AimbotKey, activationDown, shiftDown, entities.Count);

            // Shift is the explicit activation key requested for this build.
            // It is allowed to activate the aimbot even when the menu toggle is off,
            // so the key itself is not blocked by a stale UI setting.
            if (!Settings.AimbotEnabled && !shiftDown)
            {
                ResetTracking();
                return;
            }
            if (!activationDown || !cam.IsValid)
            {
                ResetTracking();
                return;
            }

            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            double dt = _lastTickTicks == 0
                ? (1.0 / 31.0)
                : (now - _lastTickTicks) / (double)System.Diagnostics.Stopwatch.Frequency;
            _lastTickTicks = now;

            // Clamp abnormal scheduler stalls so a single delayed tick cannot create
            // a giant mouse step. Normal frame-rate changes still affect the filter
            // continuously through dt.
            dt = Math.Clamp(dt, 1.0 / 120.0, 0.050);

            float cx = screenW * 0.5f;
            float cy = screenH * 0.5f;

            EntityData best = null;
            float bestDist = float.MaxValue;

            EntityData locked = null;
            float lockedDist = float.MaxValue;
            float lockedX = 0f;
            float lockedY = 0f;

            foreach (var e in entities)
            {
                if (e == null || !e.IsEnemy) continue;

                // Project directly from the current entity snapshot. Do not depend on
                // ESP's separate paint pass to populate screen coordinates.
                bool feetOk = WorldToScreen.Project(
                    e.X, e.Y, e.Z, cam, screenW, screenH,
                    out float fx, out float fy, e.Ptr);

                const float EntityWorldHeight = 21.5f;
                bool headOk = WorldToScreen.Project(
                    e.X, e.Y + EntityWorldHeight, e.Z, cam, screenW, screenH,
                    out float hx, out float hy, e.Ptr);

                if (Settings.TargetBone == AimbotBone.Head)
                {
                    if (!headOk) continue;
                }
                else if (!feetOk && !headOk)
                {
                    continue;
                }

                e.FootSX = fx;
                e.FootSY = fy;
                e.HeadSX = hx;
                e.HeadSY = hy;
                e.OnScreen = feetOk || headOk;

                float tx;
                float ty;
                if (Settings.TargetBone == AimbotBone.Head)
                {
                    tx = hx;
                    ty = hy;
                }
                else
                {
                    tx = fx;
                    ty = (fy + hy) * 0.5f;
                }

                float dx = tx - cx;
                float dy = ty - cy;
                float screenDist = MathF.Sqrt(dx * dx + dy * dy);

                if (screenDist > Settings.AimbotFOV)
                    continue;

                if (e.Ptr == _lockedTargetPtr)
                {
                    locked = e;
                    lockedDist = screenDist;
                    lockedX = tx;
                    lockedY = ty;
                }

                if (screenDist < bestDist)
                {
                    bestDist = screenDist;
                    best = e;
                }
            }

            // Decide whether to keep the current target or change it.
            bool switched = false;
            bool lockUsable = locked != null &&
                              lockedDist <= Settings.AimbotFOV * TargetRetainFovMultiplier;

            float targetX;
            float targetY;

            if (lockUsable)
            {
                // Keep the current target by default. A different target must beat it
                // by a real margin, and must do so for several consecutive frames.
                if (best != null && best.Ptr != _lockedTargetPtr &&
                    bestDist + TargetSwitchMarginPx < lockedDist)
                {
                    _switchCandidateFrames++;

                    if (_switchCandidateFrames >= TargetSwitchConfirmFrames)
                    {
                        _lockedTargetPtr = best.Ptr;
                        _switchCandidateFrames = 0;
                        switched = true;
                        targetX = Settings.TargetBone == AimbotBone.Head ? best.HeadSX : best.FootSX;
                        targetY = Settings.TargetBone == AimbotBone.Head
                            ? best.HeadSY
                            : (best.FootSY + best.HeadSY) * 0.5f;
                    }
                    else
                    {
                        targetX = lockedX;
                        targetY = lockedY;
                    }
                }
                else
                {
                    _switchCandidateFrames = 0;
                    targetX = lockedX;
                    targetY = lockedY;
                }

                _lastValidTargetTicks = now;
            }
            else if (_lockedTargetPtr != 0 &&
                     _filterInitialized &&
                     (now - _lastValidTargetTicks) / (double)System.Diagnostics.Stopwatch.Frequency <= TargetLostGraceSeconds)
            {
                // Brief projection loss during a jump/lean does not immediately hand
                // the lock to another enemy. Hold the last filtered target for a short
                // grace period instead of causing a visible target-switch flicker.
                targetX = _filteredAimX;
                targetY = _filteredAimY;
            }
            else if (best != null)
            {
                _lockedTargetPtr = best.Ptr;
                _switchCandidateFrames = 0;
                switched = true;
                targetX = Settings.TargetBone == AimbotBone.Head ? best.HeadSX : best.FootSX;
                targetY = Settings.TargetBone == AimbotBone.Head
                    ? best.HeadSY
                    : (best.FootSY + best.HeadSY) * 0.5f;
                _lastValidTargetTicks = now;
            }
            else
            {
                ResetTracking();
                return;
            }

            // Filter the target point in screen space. This removes single-frame raw
            // coordinate noise without changing the verified head offset or W2S math.
            if (switched || !_filterInitialized)
            {
                _filteredAimX = targetX;
                _filteredAimY = targetY;
                _filterInitialized = true;
            }
            else
            {
                float filterAlpha = 1.0f - MathF.Exp(-((float)dt / TargetFilterTauSeconds));
                _filteredAimX += (targetX - _filteredAimX) * filterAlpha;
                _filteredAimY += (targetY - _filteredAimY) * filterAlpha;
            }

            // Convert the filtered screen error into a frame-rate-independent mouse
            // response. Settings.AimbotSmoothing remains the user-facing response
            // control, but it is now a time-normalized gain rather than a fixed
            // per-frame percentage.
            float response = Math.Clamp(Settings.AimbotSmoothing, 0.01f, 1.0f);
            float moveAlpha = 1.0f - MathF.Exp(-response * 25.0f * (float)dt);

            float deltaX = _filteredAimX - cx;
            float deltaY = _filteredAimY - cy;

            // Do not force a 1-pixel move near the centre. That old anti-stall rule
            // can oscillate around the head when the target moves by fractions of a
            // pixel between samples.
            int movX = MathF.Abs(deltaX) < AimDeadzonePx
                ? 0
                : (int)MathF.Round(deltaX * moveAlpha);
            int movY = MathF.Abs(deltaY) < AimDeadzonePx
                ? 0
                : (int)MathF.Round(deltaY * moveAlpha);

            if (movX == 0 && movY == 0) return;

            MoveMouse(movX, movY);
        }

        private static void ResetTracking()
        {
            _lockedTargetPtr = 0;
            _switchCandidateFrames = 0;
            _lastTickTicks = 0;
            _filterInitialized = false;
            _filteredAimX = 0f;
            _filteredAimY = 0f;
            _lastValidTargetTicks = 0;
        }

        // ── Input helpers ─────────────────────────────────────────────────────

        private static void MoveMouse(int dx, int dy)
        {
            var input = new INPUT
            {
                type = INPUT_MOUSE,
                mi   = new MOUSEINPUT
                {
                    dx      = dx,
                    dy      = dy,
                    dwFlags = MOUSEEVENTF_MOVE,
                }
            };
            SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
        }

        private static bool IsAimbotKeyDown() => IsKeyDown(Settings.AimbotKey);

        private static bool IsShiftDown() =>
            IsKeyDown(Keys.ShiftKey) ||
            IsKeyDown(Keys.LShiftKey) ||
            IsKeyDown(Keys.RShiftKey);

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        private static bool IsKeyDown(Keys key) =>
            (GetAsyncKeyState((int)key) & 0x8000) != 0;
    }
}
