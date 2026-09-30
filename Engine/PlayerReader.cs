using System;
using ROS64Hack.Core;

namespace ROS64Hack.Engine
{
    /// <summary>
    /// Resolves the local PlayerBattleGroundAvatar from EntityManager.controlledEntities.
    ///
    /// Proven RE layout:
    ///   EM + 0x10F8 -> controlledEntities container
    ///   container + 0x08 -> root node
    ///   root + 0x20 -> PlayerBattleGroundAvatar Python object
    ///
    /// IMPORTANT: container + 0x19 is NOT an empty-tree discriminator. The RE snapshot
    /// had a one-entry container whose +0x18 qword was 0x101, so the previous +0x19 test
    /// incorrectly rejected a valid player.
    /// </summary>
    public class PlayerReader
    {
        private const string PBGA_TYPE_NAME = "PlayerBattleGroundAvatar";
        private readonly Mem _mem;

        public PlayerReader(Mem mem) { _mem = mem; }

        public long GetPlayerPtr(long emPtr)
        {
            if (emPtr == 0) return 0;

            long container = _mem.ReadLong(emPtr + Offsets.EM_CONTROLLED_ENT);
            if (!IsPlausiblePtr(container)) return 0;

            long root = _mem.ReadLong(container + Offsets.NODE_PARENT);
            if (!IsPlausiblePtr(root) || root == container)
            {
                return 0;
            }

            // The controlledEntities node stores the Python PBGA at +0x20.
            long playerPtr = _mem.ReadLong(root + Offsets.CONTROLLED_NODE_VALUE);
            if (!IsPlausiblePtr(playerPtr)) return 0;

            ulong obType = (ulong)_mem.ReadLong(playerPtr + Offsets.ENT_OB_TYPE);
            if (!IsPlausiblePtr(unchecked((long)obType))) return 0;

            long typeNamePtr = _mem.ReadLong(unchecked((long)obType) + Offsets.PY_TYPE_NAME);
            if (!IsPlausiblePtr(typeNamePtr)) return 0;

            string typeName = _mem.ReadString(typeNamePtr, 96);
            if (!string.Equals(typeName, PBGA_TYPE_NAME, StringComparison.Ordinal))
                return 0;

            return playerPtr;
        }

        public EntityData ReadPlayer(long emPtr)
        {
            long ptr = GetPlayerPtr(emPtr);
            if (ptr == 0) return null;

            float x = _mem.ReadFloat(ptr + Offsets.ENT_X);
            float y = _mem.ReadFloat(ptr + Offsets.ENT_Y);
            float z = _mem.ReadFloat(ptr + Offsets.ENT_Z);

            if (!IsValidCoord(x) || !IsValidCoord(y) || !IsValidCoord(z))
                return null;

            ulong obType = (ulong)_mem.ReadLong(ptr + Offsets.ENT_OB_TYPE);
            uint id = unchecked((uint)_mem.ReadInt(ptr + Offsets.ENT_ENTITY_ID));

            return new EntityData
            {
                Ptr = ptr,
                ObType = obType,
                IsLocalPlayer = true,
                IsEnemy = false,
                Id = id,
                X = x,
                Y = y,
                Z = z,
                Name = "Local",
            };
        }

        private static bool IsPlausiblePtr(long ptr) =>
            ptr >= 0x10000 && ptr < 0x0000800000000000;

        private static bool IsValidCoord(float v) =>
            !float.IsNaN(v) && !float.IsInfinity(v) && v > -50000f && v < 50000f;
    }
}
