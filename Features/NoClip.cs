using System.Runtime.InteropServices;
using System.Windows.Forms;
using ROS64Hack.Core;
using ROS64Hack.Engine;

namespace ROS64Hack.Features
{
    /// <summary>
    /// NoClip — writes XYZ directly to the local player entity in game memory.
    ///
    /// Uses WASD + SPACE/CTRL for movement.
    /// Requires WriteProcessMemory which needs PROCESS_VM_WRITE | PROCESS_VM_OPERATION.
    /// Both are included in Mem.Attach().
    ///
    /// NOTE: External WPM-based noclip is detectable by the game's position validator.
    /// Use at own risk. Implement with care: only write when keys are held.
    /// </summary>
    public static class NoClip
    {
        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);
        private static bool IsDown(Keys k) => (GetAsyncKeyState((int)k) & 0x8000) != 0;

        private static float _accumY = 0f; // accumulated height offset

        /// <summary>
        /// Called every tick. Reads key state and writes updated XYZ to player entity.
        /// </summary>
        public static void Tick(Mem mem, EntityData player, in CameraData cam)
        {
            if (!Settings.NoClipEnabled || player == null) return;

            float speed  = Settings.NoClipSpeed * 0.05f; // scale for per-tick
            float dx = 0, dy = 0, dz = 0;

            // Movement aligned to camera forward/right vectors
            bool any = false;

            if (IsDown(Keys.W)) { dx += cam.FwdX * speed; dz += cam.FwdZ * speed; any = true; }
            if (IsDown(Keys.S)) { dx -= cam.FwdX * speed; dz -= cam.FwdZ * speed; any = true; }
            if (IsDown(Keys.A)) { dx -= cam.RightX * speed; dz -= cam.RightZ * speed; any = true; }
            if (IsDown(Keys.D)) { dx += cam.RightX * speed; dz += cam.RightZ * speed; any = true; }

            // Vertical — BigWorld world Y is vertical
            if (IsDown(Keys.Space))       { dy =  speed; any = true; }
            if (IsDown(Keys.ControlKey))  { dy = -speed; any = true; }

            if (!any) return;

            float nx = player.X + dx;
            float ny = player.Y + dy;
            float nz = player.Z + dz;

            mem.WriteFloat(player.Ptr + Offsets.ENT_X, nx);
            mem.WriteFloat(player.Ptr + Offsets.ENT_Y, ny);
            mem.WriteFloat(player.Ptr + Offsets.ENT_Z, nz);

            // Update local snapshot so ESP uses updated position
            player.X = nx;
            player.Y = ny;
            player.Z = nz;

            Settings.NoClipHeight = ny;
        }
    }
}
