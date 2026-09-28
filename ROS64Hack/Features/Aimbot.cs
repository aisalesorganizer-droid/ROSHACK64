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
    /// Smoothing:  partial move each frame by Settings.AimbotSmoothing factor.
    /// Bone:       HEAD = entity XYZ + (0, +1.7, 0) in world space.
    ///             BODY = entity XYZ root (default).
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

        // ── Public API ────────────────────────────────────────────────────────

        /// <summary>
        /// Call every frame. Checks if AimbotKey is held, finds best target,
        /// and moves mouse toward it.
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
            if (!Settings.AimbotEnabled && !shiftDown) return;
            if (!activationDown) return;
            if (!cam.IsValid) return;

            float cx = screenW * 0.5f;
            float cy = screenH * 0.5f;

            EntityData best    = null;
            float      bestDist = float.MaxValue;

            foreach (var e in entities)
            {
                if (!e.IsEnemy) continue;

                // Do the projection here instead of relying on ESP.Draw() to mutate
                // EntityData first.  The read loop and paint loop run independently,
                // so using e.OnScreen/e.HeadSX/e.HeadSY here creates a real frame-order
                // race in which a freshly enumerated entity has no screen coordinates.
                bool feetOk = WorldToScreen.Project(
                    e.X, e.Y, e.Z, cam, screenW, screenH,
                    out float fx, out float fy, e.Ptr);

                bool headOk = WorldToScreen.Project(
                    e.X, e.Y + 1.8f, e.Z, cam, screenW, screenH,
                    out float hx, out float hy, e.Ptr);

                if (!feetOk && !headOk) continue;

                e.FootSX = fx;
                e.FootSY = fy;
                e.HeadSX = hx;
                e.HeadSY = hy;
                e.OnScreen = feetOk || headOk;

                // Pick aim coordinate based on target bone setting.
                float tx, ty;
                if (Settings.TargetBone == AimbotBone.Head)
                {
                    tx = hx;
                    ty = hy + (fy - hy) * 0.1f;
                }
                else
                {
                    tx = fx;
                    ty = (fy + hy) * 0.5f;
                }

                // Distance from crosshair to target screen position.
                float dx = tx - cx;
                float dy = ty - cy;
                float screenDist = MathF.Sqrt(dx * dx + dy * dy);

                // Must be inside FOV circle.
                if (screenDist > Settings.AimbotFOV) continue;

                if (screenDist < bestDist)
                {
                    bestDist = screenDist;
                    best = e;
                }
            }

            if (best == null) return;

            // Compute aim point
            float aimX, aimY;
            if (Settings.TargetBone == AimbotBone.Head)
            {
                aimX = best.HeadSX;
                aimY = best.HeadSY + (best.FootSY - best.HeadSY) * 0.1f;
            }
            else
            {
                aimX = best.FootSX;
                aimY = (best.FootSY + best.HeadSY) * 0.5f;
            }

            // Delta from current crosshair (screen centre)
            int movX = (int)((aimX - cx) * Settings.AimbotSmoothing);
            int movY = (int)((aimY - cy) * Settings.AimbotSmoothing);

            if (movX == 0 && movY == 0) return;

            MoveMouse(movX, movY);
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
