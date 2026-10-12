namespace ROS64Hack.Engine
{
    /// <summary>
    /// Snapshot of one entity read from game memory this frame.
    /// </summary>
    public class EntityData
    {
        // ── Memory address ─────────────────────────────────────────────────────
        public long   Ptr;          // Python entity object address
        public ulong  ObType;       // Python ob_type discriminator

        // ── Classification ─────────────────────────────────────────────────────
        public bool   IsLocalPlayer;   // ob_type == PBGA
        public bool   IsEnemy;         // ob_type == BGA

        // ── World-space ────────────────────────────────────────────────────────
        public float  X, Y, Z;         // feet/root position (BigWorld axes)
        public float  Distance;        // metres from camera

        // ── Screen-space (updated per-frame by ESP) ───────────────────────────
        public float  FootSX, FootSY;  // projected feet
        public float  HeadSX, HeadSY;  // projected head (+21.5 BigWorld units)
        public bool   HeadProjected;
        public bool   OnScreen;

        // ── Entity identity ────────────────────────────────────────────────────
        // Numeric gameplay/entity ID from the same +0x18 field used by the
        // correlation diagnostic. Keeping it in the snapshot avoids an extra
        // 30 Hz RPM read by the diagnostic observer.
        public uint Id;

        // Health offset not yet found in RE. Displayed as "?" until discovered.
        public float  Health   = -1f;  // <0 = unknown
        public float  MaxHealth = 100f;
        public string Name     = "Player";

        // ── Convenience ────────────────────────────────────────────────────────
        /// <summary>Screen-space bounding box derived from foot/head projection.</summary>
        public float BoxWidth  => MathF.Abs(FootSX - HeadSX) * 0.5f + 15f;
        public float BoxHeight => MathF.Abs(FootSY - HeadSY);
        public float BoxLeft   => FootSX - BoxWidth;
        public float BoxTop    => HeadSY;
    }
}
