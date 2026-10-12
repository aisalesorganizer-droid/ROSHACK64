using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using ROS64Hack.Core;
using ROS64Hack.Engine;

namespace ROS64Hack.Features
{
    /// <summary>
    /// All ESP drawing routines.
    ///
    /// Call ESP.Draw(g, entities, cam, screenW, screenH) each frame.
    /// The method projects world positions and draws all enabled overlays.
    ///
    /// Target look (from reference images — Ashesh UltraHack):
    ///   - Green lines from screen centre to each enemy
    ///   - Red name label above enemy
    ///   - Cyan distance below name
    ///   - Green HP bar on left side of box (placeholder until health RE done)
    ///   - White 2D bounding box
    /// </summary>
    public static class ESP
    {
        private static readonly Pen    PenBox      = new Pen(Color.White,         1f);
        private static readonly Pen    PenLine     = new Pen(Color.Lime,          1.2f);
        private static readonly Pen    PenFOV      = new Pen(Color.FromArgb(80, 0, 255, 0), 1f);
        private static readonly Pen    PenHealthBg = new Pen(Color.DarkRed,       3f);
        private static readonly Pen    PenHealthFg = new Pen(Color.Lime,          3f);
        private static readonly Brush  BrushName   = new SolidBrush(Color.Red);
        private static readonly Brush  BrushDist   = new SolidBrush(Color.Cyan);
        private static readonly Brush  BrushShadow = new SolidBrush(Color.FromArgb(160, Color.Black));
        private static readonly Font   FontLabel   = new Font("Arial", 7.5f, FontStyle.Bold);
        private static readonly Font   FontSmall   = new Font("Arial", 6.5f, FontStyle.Regular);
        private static readonly Font   FontDebug   = new Font("Consolas", 8f);
        private static readonly Brush  BrushDebug  = new SolidBrush(Color.Yellow);
        private static readonly StringFormat SFCenter = new StringFormat
            { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Far };

        public static void Draw(
            Graphics g,
            List<EntityData> entities,
            in CameraData cam,
            float screenW, float screenH)
        {
            if (!cam.IsValid)
            {
                RuntimeDiagnostics.EspCameraRejected();
                return;
            }

            float cx = screenW * 0.5f;
            float cy = screenH * 0.5f;

            foreach (var e in entities)
            {
                if (e.IsLocalPlayer) continue;
                if (!e.IsEnemy)      continue;

                e.Distance = WorldToScreen.Distance(e.X, e.Y, e.Z, cam);
                bool rangeRejected = !float.IsFinite(e.Distance) || e.Distance > Settings.MaxESPRange;
                RuntimeDiagnostics.EspRangeSample(
                    e.Ptr, e.X, e.Y, e.Z, cam, e.Distance, Settings.MaxESPRange, rangeRejected);
                if (rangeRejected)
                {
                    e.OnScreen = false;
                    RuntimeDiagnostics.EspRangeRejected();
                    continue;
                }

                bool feetOk = WorldToScreen.Project(
                    e.X, e.Y, e.Z,
                    cam, screenW, screenH,
                    out float fx, out float fy, e.Ptr);

                // Verified legacy height for this BigWorld build.
                // The old working implementation used 21.5 raw world units,
                // not 1.8 raw units. 1.8 was a metre value incorrectly applied
                // directly to the game's coordinate space and collapsed the box.
                const float EntityWorldHeight = 21.5f;
                bool headOk = WorldToScreen.Project(
                    e.X, e.Y + EntityWorldHeight, e.Z,
                    cam, screenW, screenH,
                    out float hx, out float hy, e.Ptr);

                e.FootSX = fx; e.FootSY = fy;
                e.HeadSX = hx; e.HeadSY = hy;
                e.HeadProjected = headOk;
                e.OnScreen = feetOk;
            }

            if (Settings.EspLines)
                foreach (var e in entities)
                    if (e.IsEnemy && e.OnScreen)
                        DrawLine(g, cx, 0f, e.FootSX, e.FootSY);  // origin = top-centre

            if (Settings.EspPlayer)
                foreach (var e in entities)
                    if (e.IsEnemy && e.OnScreen)
                        DrawEntity(g, e, screenW, screenH);

            if (Settings.ShowAimbotFOV && Settings.AimbotEnabled)
                DrawFOVCircle(g, cx, cy, Settings.AimbotFOV);

            if (Settings.ShowDebugInfo)
                DrawDebugInfo(g, cam, entities);
        }

        private static void DrawEntity(Graphics g, EntityData e, float sw, float sh)
        {
            // A box requires both endpoints. Keep tracer visibility independent
            // from the box when the head projection is outside the projection gate.
            if (!e.HeadProjected) return;

            float bx = e.FootSX;
            float by = e.FootSY;
            float tx = e.HeadSX;
            float ty = e.HeadSY;

            float boxH = MathF.Abs(by - ty);
            float boxW = boxH * 0.35f;

            float left  = bx - boxW * 0.5f;
            float top   = ty - 4f;
            float right = bx + boxW * 0.5f;
            float bot   = by;

            if (Settings.Esp2DBox)
            {
                var rect = new RectangleF(left, top, right - left, bot - top);
                g.DrawRectangle(PenBox, rect.X, rect.Y, rect.Width, rect.Height);
            }

            if (Settings.EspHealth)
                DrawHealthBar(g, left - 5f, top, bot - top, e.Health, e.MaxHealth);

            if (Settings.EspPlayer)
            {
                g.DrawString(e.Name, FontLabel, BrushShadow,
                    bx + 1f, top - 12f, SFCenter);
                g.DrawString(e.Name, FontLabel, BrushName,
                    bx, top - 13f, SFCenter);
            }

            if (Settings.EspDistance)
            {
                string distStr = $"[ {e.Distance:F0} Meter ]";
                g.DrawString(distStr, FontSmall, BrushShadow, bx + 1f, bot + 1f, SFCenter);
                g.DrawString(distStr, FontSmall, BrushDist, bx, bot, SFCenter);
            }
        }

        private static void DrawHealthBar(
            Graphics g, float x, float top, float height,
            float hp, float maxHp)
        {
            if (height < 5f) return;

            g.DrawLine(PenHealthBg, x, top, x, top + height);

            float ratio  = (hp >= 0 && maxHp > 0) ? hp / maxHp : 1.0f;
            float fillH  = height * ratio;
            using var pen = new Pen(HpColor(ratio), 3f);
            g.DrawLine(pen, x, top + height, x, top + height - fillH);
        }

        private static Color HpColor(float ratio)
        {
            if (ratio > 0.6f)  return Color.Lime;
            if (ratio > 0.3f)  return Color.Yellow;
            return Color.OrangeRed;
        }

        private static void DrawLine(
            Graphics g, float cx, float cy,
            float tx, float ty)
        {
            g.DrawLine(PenLine, cx, cy, tx, ty);
        }

        private static void DrawFOVCircle(
            Graphics g, float cx, float cy, float radius)
        {
            g.DrawEllipse(PenFOV,
                cx - radius, cy - radius,
                radius * 2f, radius * 2f);
        }

        private static int _frameCount;
        private static long _lastFpsTick = System.Diagnostics.Stopwatch.GetTimestamp();
        private static int _fps;

        private static void DrawDebugInfo(
            Graphics g, in CameraData cam,
            List<EntityData> entities)
        {
            _frameCount++;
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            long freq = System.Diagnostics.Stopwatch.Frequency;
            if (now - _lastFpsTick >= freq)
            {
                _fps = _frameCount;
                _frameCount = 0;
                _lastFpsTick = now;
            }

            int enemyCount = 0;
            foreach (var e in entities) if (e.IsEnemy && e.OnScreen) enemyCount++;
            int totalEnt = entities.Count;

            bool camOk = cam.IsValid;

            var lines = new[]
            {
                $"Range: {Settings.MaxESPRange:F0} m",
                $"AMBOT: {(Settings.AimbotEnabled ? 1 : 0)}",
                $"FOV: {Settings.AimbotFOV:F0}",
                $"NOCLIP: {(Settings.NoClipEnabled ? 1 : 0)}",
                $"SmartAim: {(Settings.AimbotSmoothing < 0.5f ? 1 : 0)}",
                $"Height: {Settings.NoClipHeight:F0}",
                $"NoClip Speed: {Settings.NoClipSpeed:F0}",
                $"FPS: {_fps}",
                $"Visible: {enemyCount}",
                $"Entities: {totalEnt}",
                $"CAM: {(camOk ? "OK" : "SEARCHING...")}",
                camOk ? $"POS: {cam.PosX:F0} {cam.PosY:F0} {cam.PosZ:F0}" : "POS: --",
                camOk ? $"FOC: {cam.YFocal:F3}/{cam.XFocal:F3}" : "FOC: --",
            };

            float y = 8f;
            foreach (var line in lines)
            {
                g.DrawString(line, FontDebug, BrushShadow,  9f, y + 1f);
                g.DrawString(line, FontDebug, BrushDebug,   8f, y);
                y += 14f;
            }
        }
    }
}
