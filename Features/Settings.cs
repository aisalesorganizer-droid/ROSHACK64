namespace ROS64Hack.Features
{
    /// <summary>
    /// Global feature settings. Shared between overlay, menu, and feature classes.
    /// All fields are plain statics — no lock needed (read/write are atomic on x64 for bool/int).
    /// </summary>
    public static class Settings
    {
        // ── VISUAL tab ────────────────────────────────────────────────────────

        /// <summary>Show enemy player ESP (boxes + info).</summary>
        public static bool EspPlayer    = true;

        /// <summary>Draw lines from screen centre to each enemy.</summary>
        public static bool EspLines     = true;

        /// <summary>Display HP value over enemy (shows "?" until health offset confirmed).</summary>
        public static bool EspHealth    = true;

        /// <summary>Display distance (metres) under enemy name.</summary>
        public static bool EspDistance  = true;

        /// <summary>Draw 2D bounding box around each enemy.</summary>
        public static bool Esp2DBox     = true;

        /// <summary>Show items on the ground (loot ESP — future feature).</summary>
        public static bool EspItem      = true;
        public static bool EspItemDist  = true;
        public static bool EspGoldItem  = false;

        /// <summary>Show vehicles.</summary>
        public static bool EspVehicle   = true;
        public static bool EspVehicleDist = true;

        /// <summary>Show supply box (airdrop) location.</summary>
        public static bool EspSupplyBox = true;

        /// <summary>Show plane path.</summary>
        public static bool EspPlane     = false;

        // ── AIMBOT tab ────────────────────────────────────────────────────────

        /// <summary>Aimbot enabled. Hold AimbotKey to activate.</summary>
        public static bool AimbotEnabled    = false;

        /// <summary>AimbotKey = Shift by default.</summary>
        public static System.Windows.Forms.Keys AimbotKey = System.Windows.Forms.Keys.ShiftKey;

        /// <summary>
        /// Smoothing factor 0.0–1.0.
        /// 1.0 = instant snap. 0.85 = tight head tracking. Lower values are smoother/slower.
        /// </summary>
        public static float AimbotSmoothing = 0.85f;

        /// <summary>FOV radius in pixels. Enemies outside this radius are ignored.</summary>
        public static float AimbotFOV = 150f;

        /// <summary>Draw FOV circle on overlay.</summary>
        public static bool ShowAimbotFOV = true;

        /// <summary>Target bone: HEAD = verified +21.5 raw-world-unit head point, BODY = root.</summary>
        public static AimbotBone TargetBone = AimbotBone.Head;

        // ── NOCLIP tab ────────────────────────────────────────────────────────

        /// <summary>NoClip mode — writes XYZ directly to player entity in memory.</summary>
        public static bool NoClipEnabled   = false;

        /// <summary>NoClip movement speed (metres per tick).</summary>
        public static float NoClipSpeed    = 2.0f;

        /// <summary>NoClip height adjustment (metres per UP key press).</summary>
        public static float NoClipHeight   = 0f;

        // ── MISC tab ──────────────────────────────────────────────────────────

        /// <summary>Maximum ESP range in metres. Enemies beyond this are hidden.</summary>
        public static float MaxESPRange    = 100000f;

        /// <summary>Draw a debug info overlay (range, flags, FPS).</summary>
        public static bool ShowDebugInfo   = true;

        /// <summary>Toggle key for showing/hiding the menu form.</summary>
        public static System.Windows.Forms.Keys MenuKey = System.Windows.Forms.Keys.Insert;

        // ── Internal ──────────────────────────────────────────────────────────

        /// <summary>Approx camera FOV in degrees (used for debug display).</summary>
        public const float VFOV_DEGREES = 90f;
    }

    public enum AimbotBone { Head, Body }
}
