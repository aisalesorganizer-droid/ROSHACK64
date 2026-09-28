using System.Drawing;

namespace ROS64Hack.Core
{
    /// <summary>
    /// World-to-screen projection.
    ///
    /// Replicates the game's vtable[27] function at 0x140F9B060.
    ///
    /// PROVEN FORMULA (verified against 3 sampled models during RE session):
    ///
    ///   Step 1 — camera-relative delta:
    ///     dx = wx - cam.PosX
    ///     dy = wy - cam.PosY
    ///     dz = wz - cam.PosZ
    ///
    ///   Step 2 — view-space (dot products with camera basis vectors):
    ///     vx = dot(delta, cam.Right)
    ///     vy = dot(delta, cam.Up)
    ///     vz = dot(delta, cam.Fwd)
    ///
    ///   Step 3 — perspective project:
    ///     ar    = YFocal / XFocal        (≈ 2.4142 / 1.8156 ≈ 1.3297)
    ///     sx    = W/2 * (1 + ar * vx/vz)
    ///     sy    = H/2 * (1 - YFocal * vy/vz)   ← MULTIPLY, not divide
    ///
    /// IMPORTANT: sy uses cam.YFocal as a MULTIPLIER (not divisor).
    /// The previous code had this inverted, causing Y-projection to be 5x too small.
    ///
    /// XFocal (cam+0x360, confirmed ≈ 1.8156) is used for the aspect ratio.
    /// If XFocal reads as 0/invalid, we fall back to W/H screen aspect, which
    /// gives a slightly wrong X projection but correct Y — lines will still appear.
    /// </summary>
    public static class WorldToScreen
    {
        public static bool Project(
            float wx, float wy, float wz,
            in CameraData cam,
            float screenW, float screenH,
            out float sx, out float sy,
            long entityPtr = 0)
        {
            sx = 0; sy = 0;

            // Step 1: camera-relative delta
            float dx = wx - cam.PosX;
            float dy = wy - cam.PosY;
            float dz = wz - cam.PosZ;

            // Step 2: view-space via dot products with camera basis vectors
            float vx = dx * cam.RightX + dy * cam.RightY + dz * cam.RightZ;
            float vy = dx * cam.UpX    + dy * cam.UpY    + dz * cam.UpZ;
            float vz = dx * cam.FwdX   + dy * cam.FwdY   + dz * cam.FwdZ;

            // Behind-camera cull
            if (vz <= 0.01f)
            {
                RuntimeDiagnostics.W2S(entityPtr, wx, wy, wz, vx, vy, vz, sx, sy, "BEHIND");
                return false;
            }

            // Step 3: perspective project
            // ar = YFocal / XFocal (BigWorld-specific ratio, confirmed from RE)
            // Fallback: if XFocal is not available, use screen aspect ratio
            float ar;
            if (cam.XFocal > 0.3f && cam.XFocal < 10f)
                ar = cam.YFocal / cam.XFocal;  // proven formula — use real ratio
            else
                ar = screenW / screenH;          // fallback if +0x360 reads garbage

            // ── These are the CORRECT formulas from the RE session ──
            sx = screenW * 0.5f * (1.0f + ar * (vx / vz));
            sy = screenH * 0.5f * (1.0f - cam.YFocal * (vy / vz));  // MULTIPLY by YFocal

            // Loose bounds — allow slightly off-screen so lines clip the edge cleanly
            float margin = 300f;
            if (sx < -margin || sx > screenW + margin ||
                sy < -margin || sy > screenH + margin)
            {
                RuntimeDiagnostics.W2S(entityPtr, wx, wy, wz, vx, vy, vz, sx, sy, "BOUNDS");
                return false;
            }

            RuntimeDiagnostics.W2S(entityPtr, wx, wy, wz, vx, vy, vz, sx, sy, "PROJECTED");
            return true;
        }

        public static float Distance(float wx, float wy, float wz, in CameraData cam)
        {
            float dx = wx - cam.PosX;
            float dy = wy - cam.PosY;
            float dz = wz - cam.PosZ;
            // BigWorld position/distance units are decimeters on this build.
            // The legacy working implementation converted the raw world distance
            // with Divide=10 before displaying it as metres.
            return MathF.Sqrt(dx * dx + dy * dy + dz * dz) / 10.0f;
        }

        public static PointF ToPoint(float sx, float sy) => new PointF(sx, sy);
    }

    public struct CameraData
    {
        public float FwdX, FwdY, FwdZ;
        public float UpX,  UpY,  UpZ;
        public float RightX, RightY, RightZ;
        public float PosX, PosY, PosZ;
        public float YFocal;   // cam+0x9C — confirmed ≈ 2.4142 (90° VFOV, cot 22.5°)
        public float XFocal;   // cam+0x360 — confirmed ≈ 1.8156 (used in ar = YFocal/XFocal)
        public byte  Ortho;

        private static bool Finite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);

        private static bool SaneVector(float x, float y, float z)
        {
            if (!Finite(x) || !Finite(y) || !Finite(z)) return false;
            float mag = MathF.Sqrt(x * x + y * y + z * z);
            return mag >= 0.90f && mag <= 1.10f;
        }

        // The RE-proven camera discriminator is the vtable plus a valid YFocal
        // and unit forward vector. XFocal is useful for the exact X projection,
        // but is not required to identify/accept the live camera because Project()
        // has a screen-aspect fallback when XFocal is unavailable.
        public bool IsValid =>
            Finite(PosX) && Finite(PosY) && Finite(PosZ) &&
            MathF.Abs(PosX) < 50000f && MathF.Abs(PosY) < 50000f && MathF.Abs(PosZ) < 50000f &&
            Finite(YFocal) && YFocal > 0.3f && YFocal < 10f &&
            SaneVector(FwdX, FwdY, FwdZ) &&
            SaneVector(UpX, UpY, UpZ) &&
            SaneVector(RightX, RightY, RightZ);
    }
}
