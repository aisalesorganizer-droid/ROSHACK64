namespace ROS64Hack.Core
{
    /// <summary>
    /// ALL OFFSETS ARE IMAGE-RELATIVE (add Mem.BaseAddress to get the live VA).
    /// Source: ROS64 RE session — 2026-09-28. Image base: 0x140000000.
    /// Formula: OFFSET = ABSOLUTE_VA - 0x140000000
    /// </summary>
    public static class Offsets
    {
        // ── EntityManager ──────────────────────────────────────────────────────
        // Static TLS anchor: [base + EM_STATIC_PTR] → live heap EM ptr
        // Absolute VA was 0x1431F53D8 → offset = 0x031F53D8
        public const long EM_STATIC_PTR         = 0x031F53D8;

        // EM vtable — for heap scan fallback; add base before comparing
        // Absolute VA was 0x141BEF730 → offset = 0x01BEF730
        public const long EM_VTABLE_OFFSET      = 0x01BEF730;

        // EM internal offsets (relative to live EM ptr, NOT the image base)
        public const long EM_ALL_ENTITIES       = 0x10E0;   // RB-tree head for ALL entities
        public const long EM_CONTROLLED_ENT     = 0x10F8;   // sentinel → player PBGA (fast path)

        // ── Exact native model/scene type anchors used by the evidence diagnostic ──
        // ModelSkeletal vtable RVA  = 0x1C1ACE0  (abs 0x141C1ACE0)
        // SpaceNode vtable RVA      = 0x1C137C8  (abs 0x141C137C8)
        // Native Model vtable RVA   = 0x1C173E0  (abs 0x141C173E0)
        public const long MODEL_SKELETAL_VTABLE_RVA = 0x001C1ACE0;
        public const long SPACENODE_VTABLE_RVA      = 0x001C137C8;
        public const long MODEL_VTABLE_RVA           = 0x001C173E0;

        // Python/BGA structural fingerprint (image-relative).
        public const long PY_CUSTOM_GETATTR      = 0x00214870; // proven tp_getattro implementation

        // ── Camera ─────────────────────────────────────────────────────────────
        // Static chain: [base + CAM_ANCHOR] → ptr1 → ptr2 → live cam ptr
        // Absolute VA was 0x143151EF0 → offset = 0x03151EF0
        public const long CAM_ANCHOR_OFFSET     = 0x03151EF0;

        // Camera vtable offset — for heap scan fallback; add base before comparing
        // Absolute VA was 0x141D84DC8 → offset = 0x01D84DC8
        public const long CAM_VTABLE_OFFSET     = 0x01D84DC8;

        // Camera struct layout (relative to live cam ptr)
        // BigWorld 4x3 matrix layout (vtable at +0x00, 8 bytes):
        //   Row 0 (+0x08): RIGHT vector (local +X)
        //   Row 1 (+0x18): UP    vector (local +Y)
        //   Row 2 (+0x28): FWD   vector (local +Z / "At" direction)
        //   Row 3 (+0x38): POS
        // NOTE: Previously mislabeled Row 0 as FWD and Row 2 as RIGHT — corrected 2026-09-29.
        public const long CAM_RIGHT_X           = 0x08;
        public const long CAM_RIGHT_Y           = 0x0C;
        public const long CAM_RIGHT_Z           = 0x10;
        public const long CAM_UP_X              = 0x18;
        public const long CAM_UP_Y              = 0x1C;
        public const long CAM_UP_Z              = 0x20;
        public const long CAM_FWD_X             = 0x28;
        public const long CAM_FWD_Y             = 0x2C;
        public const long CAM_FWD_Z             = 0x30;
        public const long CAM_POS_X             = 0x38;
        public const long CAM_POS_Y             = 0x3C;
        public const long CAM_POS_Z             = 0x40;
        public const long CAM_YFOCAL            = 0x9C;    // 2.4142 — cot(22.5°), 90° VFOV
        public const long CAM_ORTHO_FLAG        = 0x348;   // byte; 0 = perspective
        public const long CAM_XFOCAL            = 0x360;   // 1.8156 — aspect-variant focal

        // ── RB-Tree node layout ────────────────────────────────────────────────
        // Offsets relative to a tree node ptr
        public const long NODE_LEFT             = 0x00;    // left child ptr
        public const long NODE_PARENT           = 0x08;    // parent ptr
        public const long NODE_RIGHT            = 0x10;    // right child ptr
        public const long NODE_SENTINEL_BYTE    = 0x18;    // byte; 1 = this is the nil sentinel; +0x19 is color
        public const long NODE_ENTITY_ID        = 0x20;    // historical key/value area; do not use for object enumeration
        public const long NODE_ENTITY_PTR       = 0x28;    // one historically observed value slot; EntityLoop resolves +0x20/+0x28 from live objects

        // ── Entity (BGA / PBGA) layout ─────────────────────────────────────────
        // Offsets relative to the resolved Python entity object address
        public const long ENT_OB_TYPE           = 0x08;    // QWORD → Python type object ptr
        public const long ENT_ENTITY_ID         = 0x18;    // DWORD entity ID (numeric)
        public const long ENT_X                 = 0x1C;    // float32 world X
        public const long ENT_Y                 = 0x20;    // float32 world Y (vertical in BigWorld)
        public const long ENT_Z                 = 0x24;    // float32 world Z
        public const long ENT_UNKNOWN_FLOAT_4C  = 0x4C;    // possibly yaw or partial health (unconfirmed)

        // Python 2.7 layout proven in the RE transcript.
        public const long PY_TYPE_NAME             = 0x18;
        public const long PY_INSTANCE_DICT         = 0x1B0;   // player object +0x1B0 = instance dict (verified live)
        public const long PY_DICT_USED             = 0x18;
        public const long PY_DICT_MASK             = 0x20;
        public const long PY_DICT_TABLE            = 0x28;
        public const long PY_DICT_ENTRY_KEY        = 0x08;
        public const long PY_DICT_ENTRY_VALUE      = 0x10;
        public const long PY_DICT_ENTRY_STRIDE     = 0x18;
        public const long PY_STRING_VALUE           = 0x20;

        // controlledEntities is a special RB-tree layout: node +0x20 is the
        // Python PlayerBattleGroundAvatar value. This must not be confused with
        // the all-entities tree's numeric key at the same offset.
        public const long CONTROLLED_NODE_VALUE    = 0x20;

        // ── ob_type constants ──────────────────────────────────────────────────
        // Historical dump-only type addresses. Main ESP path resolves the current BGA
        // PyTypeObject from a live avatarInAoI value; these are intentionally not used
        // as current-session addresses.
        public const ulong OB_TYPE_BGA_FALLBACK  = 0xAA9068B8;   // BGA  = enemy
        public const ulong OB_TYPE_PBGA_FALLBACK = 0xAA90E9C8;   // PBGA = local player

        // ── Python interpreter ─────────────────────────────────────────────────
        // Not needed for direct-read path but kept for reference
        // PyEval_EvalFrameEx offset = 0x122E840 (abs 0x14122E840)
        public const long PY_EVAL_FRAME_OFFSET  = 0x0122E840;

        // ── NoClip (write target) ──────────────────────────────────────────────
        // NoClip writes directly to the local player's XYZ in memory.
        // Use ENT_X / ENT_Y / ENT_Z on the player entity ptr.
        // Additional NoClip speed and height offsets TBD.

        // ── Screen defaults ────────────────────────────────────────────────────
        // Discovered from render dimension chain during RE.
        // Override at runtime by reading the game window client rect.
        public const float DEFAULT_SCREEN_W     = 1280f;
        public const float DEFAULT_SCREEN_H     = 705f;
    }
}
