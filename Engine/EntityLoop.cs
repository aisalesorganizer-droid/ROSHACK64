using System;
using System.Collections.Generic;
using ROS64Hack.Core;

namespace ROS64Hack.Engine
{
    /// <summary>
    /// Enumerates live enemy BattleGroundAvatar objects through the RE-proven
    /// runtime path:
    ///
    ///   controlledEntities
    ///      -> PlayerBattleGroundAvatar
    ///      -> player instance dict (+0x1B0)
    ///      -> key "avatarInAoI"
    ///      -> AOI dictionary
    ///      -> dictionary values = BattleGroundAvatar objects
    ///
    /// The EM+0x10E0 RB-tree is intentionally NOT used for the ESP entity pool.
    /// The live evidence capture demonstrated that node+0x28 is not a BGA Python
    /// object for this target build, while avatarInAoI contains actual BGA objects
    /// with valid ob_type, entity ID, and XYZ.
    ///
    /// The hot path uses bounded dictionary reads only; there is no process-wide
    /// heap scan and no per-frame type-name probing once the BGA PyTypeObject has
    /// been learned from a real AOI value.
    /// </summary>
    public sealed class EntityLoop
    {
        private const string BGA_TYPE_NAME = "BattleGroundAvatar";
        private const string AOI_KEY_NAME = "avatarInAoI";

        private const int MAX_DICT_SLOTS = 0x10000;
        private const int MAX_KEY_STRING = 96;
        // Live RE capture repeatedly found avatarInAoI at player-dict slot 770
        // while mask=0x7FF. Use the verified slot directly; do not make enemy
        // discovery depend on decoding arbitrary player-dict keys.
        private const int VERIFIED_AOI_SLOT = 770;

        private readonly Mem _mem;
        private readonly PlayerReader _player;

        private long _lastPlayerPtr;
        private long _lastPlayerDictPtr;
        private long _lastPlayerDictTable;
        private int _lastPlayerDictMask = -1;
        private long _aoiDictPtr;

        private int _avatarKeySlot = -1;
        private long _avatarKeyObj;
        private long _nextAvatarScanMs;
        private int _calibrationLogCount;
        private int _dictReadFailureLogCount;

        public ulong TypePbga { get; private set; }
        public ulong TypeBga { get; private set; }

        public EntityLoop(Mem mem, PlayerReader player)
        {
            _mem = mem;
            _player = player;
            Console.WriteLine("  [ENT] Enumerating enemies from PBGA.avatarInAoI (verified live path)");
        }

        private static bool IsPlausiblePtr(long ptr) =>
            ptr >= 0x10000 && ptr < 0x0000800000000000L;

        private static bool IsValidCoord(float v) =>
            float.IsFinite(v) && v > -50000f && v < 50000f;

        private bool TryReadPlayerDict(long playerPtr, out long dictPtr)
        {
            dictPtr = 0;
            if (!IsPlausiblePtr(playerPtr)) return false;

            // The captured live object layout resolves the instance dict at +0x1B0.
            dictPtr = _mem.ReadLong(playerPtr + Offsets.PY_INSTANCE_DICT, "ENT.player_dict");
            return IsPlausiblePtr(dictPtr);
        }

        private bool TryReadDictHeader(long dictPtr, out int used, out int mask, out long table)
        {
            used = 0;
            mask = 0;
            table = 0;

            if (!IsPlausiblePtr(dictPtr)) return false;

            long usedRaw = _mem.ReadLong(dictPtr + Offsets.PY_DICT_USED, "ENT.dict_used");
            long maskRaw = _mem.ReadLong(dictPtr + Offsets.PY_DICT_MASK, "ENT.dict_mask");
            table = _mem.ReadLong(dictPtr + Offsets.PY_DICT_TABLE, "ENT.dict_table");

            if (usedRaw < 0 || usedRaw > MAX_DICT_SLOTS) return false;
            if (maskRaw < 0 || maskRaw > MAX_DICT_SLOTS - 1) return false;
            if (!IsPlausiblePtr(table)) return false;

            used = (int)usedRaw;
            mask = (int)maskRaw;

            int slots = mask + 1;
            if (slots <= 0 || slots > MAX_DICT_SLOTS) return false;
            if (used > slots) return false;
            return true;
        }

        private static long ReadQword(byte[] buffer, int offset)
        {
            return BitConverter.ToInt64(buffer, offset);
        }

        private bool TryReadDictTable(long table, int mask, out byte[] bytes)
        {
            bytes = null;
            int slots = mask + 1;
            long totalLong = (long)slots * Offsets.PY_DICT_ENTRY_STRIDE;
            if (totalLong <= 0 || totalLong > int.MaxValue) return false;

            int total = (int)totalLong;
            var data = new byte[total];
            bool anyChunkRead = false;
            int failedChunks = 0;

            // Do not ask ReadProcessMemory for the entire dict table in one call.
            // The live target can place the table across page boundaries where a
            // monolithic read is rejected even though individual pages are readable.
            // Page-bounded reads also make the failure location explicit.
            int cursor = 0;
            while (cursor < total)
            {
                long address = table + cursor;
                int pageOffset = (int)(address & 0xFFF);
                int chunk = Math.Min(0x1000 - pageOffset, total - cursor);

                if (_mem.ReadBytes(address, data, cursor, chunk))
                {
                    anyChunkRead = true;
                }
                else
                {
                    failedChunks++;
                    Array.Clear(data, cursor, chunk);
                    if (_dictReadFailureLogCount < 8)
                    {
                        _dictReadFailureLogCount++;
                        Console.WriteLine(
                            $"  [ENT][DICT-READ] chunk failed addr=0x{address:X} size=0x{chunk:X}; continuing with readable pages");
                    }
                }

                cursor += chunk;
            }

            if (!anyChunkRead) return false;
            if (failedChunks > 0 && _dictReadFailureLogCount < 8)
            {
                _dictReadFailureLogCount++;
                Console.WriteLine(
                    $"  [ENT][DICT-READ] partial table read: failedChunks={failedChunks} total=0x{total:X}");
            }

            bytes = data;
            return true;
        }

        private string ReadPythonString(long keyObj)
        {
            if (!IsPlausiblePtr(keyObj)) return string.Empty;

            long chars = _mem.ReadLong(keyObj + Offsets.PY_STRING_VALUE, "ENT.key_chars");
            if (!IsPlausiblePtr(chars)) return string.Empty;

            return (_mem.ReadString(chars, MAX_KEY_STRING) ?? string.Empty).TrimEnd('\0');
        }

        private bool TryResolveAvatarKey(long playerPtr, long playerDictPtr)
        {
            bool sameDict = _lastPlayerPtr == playerPtr && _lastPlayerDictPtr == playerDictPtr;
            if (sameDict && _avatarKeySlot >= 0 && _aoiDictPtr != 0)
                return true;

            long nowMs = Environment.TickCount64;
            if (sameDict && nowMs < _nextAvatarScanMs)
                return false;

            if (!sameDict)
            {
                _avatarKeySlot = -1;
                _avatarKeyObj = 0;
                _aoiDictPtr = 0;
                _lastPlayerPtr = playerPtr;
                _lastPlayerDictPtr = playerDictPtr;
                _lastPlayerDictTable = 0;
                _lastPlayerDictMask = -1;
            }

            if (!TryReadDictHeader(playerDictPtr, out int used, out int mask, out long table))
            {
                _nextAvatarScanMs = nowMs + 500;
                return false;
            }

            if (_calibrationLogCount < 6)
            {
                Console.WriteLine(
                    $"  [ENT][DICT] player=0x{playerPtr:X} dict=0x{playerDictPtr:X} " +
                    $"used={used} mask=0x{mask:X} table=0x{table:X}");
            }

            _lastPlayerDictTable = table;
            _lastPlayerDictMask = mask;

            // Verified live evidence repeatedly located avatarInAoI at slot 770
            // with mask=0x7FF. Read that one entry directly, avoiding a scan of
            // hundreds of unrelated Python keys and avoiding dependence on string
            // decoding for target acquisition.
            if (mask == 0x7FF)
            {
                long entry = table + (long)VERIFIED_AOI_SLOT * Offsets.PY_DICT_ENTRY_STRIDE;
                long rawHash = _mem.ReadLong(entry + 0x00, "ENT.avatar_slot770_hash");
                long keyObj = _mem.ReadLong(entry + Offsets.PY_DICT_ENTRY_KEY, "ENT.avatar_slot770_key");
                long value = _mem.ReadLong(entry + Offsets.PY_DICT_ENTRY_VALUE, "ENT.avatar_slot770_value");

                if (_calibrationLogCount < 6)
                {
                    Console.WriteLine(
                        $"  [ENT][SLOT770] entry=0x{entry:X} hash=0x{rawHash:X} " +
                        $"key=0x{keyObj:X} value=0x{value:X}");
                }

                if (IsPlausiblePtr(value) &&
                    TryReadDictHeader(value, out int aoiUsed, out int aoiMask, out long aoiTable) &&
                    aoiMask == 0x7F)
                {
                    _avatarKeySlot = VERIFIED_AOI_SLOT;
                    _avatarKeyObj = keyObj;
                    _aoiDictPtr = value;

                    Console.WriteLine(
                        $"  [ENT][AOI] avatarInAoI resolved by verified slot 770: " +
                        $"player=0x{playerPtr:X} playerDict=0x{playerDictPtr:X} " +
                        $"aoiDict=0x{value:X} aoiUsed={aoiUsed} aoiMask=0x{aoiMask:X} " +
                        $"aoiTable=0x{aoiTable:X}");
                    return true;
                }
            }

            // Fallback: use the complete table only to recover if the current
            // Python dictionary has been rebuilt with a different slot assignment.
            if (!TryReadDictTable(table, mask, out byte[] data))
            {
                _nextAvatarScanMs = nowMs + 500;
                return false;
            }

            int slots = mask + 1;
            for (int slot = 0; slot < slots; slot++)
            {
                int off = checked((int)((long)slot * Offsets.PY_DICT_ENTRY_STRIDE));
                long keyObj = ReadQword(data, off + (int)Offsets.PY_DICT_ENTRY_KEY);
                long value = ReadQword(data, off + (int)Offsets.PY_DICT_ENTRY_VALUE);
                if (!IsPlausiblePtr(value)) continue;

                string key = ReadPythonString(keyObj);
                if (!string.Equals(key, AOI_KEY_NAME, StringComparison.Ordinal)) continue;

                _avatarKeySlot = slot;
                _avatarKeyObj = keyObj;
                _aoiDictPtr = value;

                Console.WriteLine(
                    $"  [ENT][AOI] avatarInAoI resolved by key scan: player=0x{playerPtr:X} " +
                    $"playerDict=0x{playerDictPtr:X} slot={slot} aoiDict=0x{value:X} used={used}");
                return true;
            }

            _nextAvatarScanMs = nowMs + 500;
            return false;
        }

        private bool TryRefreshAoiDict(long playerDictPtr)
        {
            if (_aoiDictPtr == 0 || _lastPlayerDictPtr != playerDictPtr || _avatarKeySlot < 0)
            {
                return false;
            }

            if (!TryReadDictHeader(playerDictPtr, out _, out int mask, out long table))
                return false;

            if (mask != _lastPlayerDictMask || table != _lastPlayerDictTable)
            {
                _avatarKeySlot = -1;
                return false;
            }

            long entry = table + (long)_avatarKeySlot * Offsets.PY_DICT_ENTRY_STRIDE;
            long value = _mem.ReadLong(entry + Offsets.PY_DICT_ENTRY_VALUE, "ENT.avatar_value_fast");

            if (IsPlausiblePtr(value))
            {
                _aoiDictPtr = value;
                return true;
            }

            _avatarKeySlot = -1;
            _aoiDictPtr = 0;
            return false;
        }

        private bool TryReadBgaType(long obj, out ulong typePtr)
        {
            typePtr = 0;
            if (!IsPlausiblePtr(obj)) return false;

            long rawType = _mem.ReadLong(obj + Offsets.ENT_OB_TYPE, "ENT.bga_ob_type");
            if (!IsPlausiblePtr(rawType)) return false;

            typePtr = unchecked((ulong)rawType);
            if (TypeBga == typePtr) return true;

            long typeNamePtr = _mem.ReadLong(rawType + Offsets.PY_TYPE_NAME, "ENT.bga_type_name");
            if (!IsPlausiblePtr(typeNamePtr)) return false;

            string typeName = (_mem.ReadString(typeNamePtr, 96) ?? string.Empty).TrimEnd('\0');
            if (!string.Equals(typeName, BGA_TYPE_NAME, StringComparison.Ordinal)) return false;

            TypeBga = typePtr;
            return true;
        }

        private bool TryReadBga(long obj, long localPlayerPtr, out EntityData entity)
        {
            entity = null;
            if (!IsPlausiblePtr(obj) || obj == localPlayerPtr) return false;

            ulong type = unchecked((ulong)_mem.ReadLong(obj + Offsets.ENT_OB_TYPE, "ENT.bga_ob_type_fast"));
            if (!IsPlausiblePtr(unchecked((long)type))) return false;

            if (TypeBga == 0)
            {
                if (!TryReadBgaType(obj, out type)) return false;
            }
            else if (type != TypeBga)
            {
                // Type pointers are normally stable for the life of the Python
                // runtime, but re-resolve by name if a scene/runtime transition
                // presents a different live PyTypeObject.
                if (!TryReadBgaType(obj, out ulong liveType) || liveType != type)
                    return false;
                type = liveType;
            }

            float x = _mem.ReadFloat(obj + Offsets.ENT_X, "ENT.bga_x");
            float y = _mem.ReadFloat(obj + Offsets.ENT_Y, "ENT.bga_y");
            float z = _mem.ReadFloat(obj + Offsets.ENT_Z, "ENT.bga_z");
            if (!IsValidCoord(x) || !IsValidCoord(y) || !IsValidCoord(z)) return false;

            uint id = unchecked((uint)_mem.ReadInt(obj + Offsets.ENT_ENTITY_ID, "ENT.bga_id"));
            entity = new EntityData
            {
                Ptr = obj,
                ObType = type,
                IsLocalPlayer = false,
                IsEnemy = true,
                X = x,
                Y = y,
                Z = z,
                Id = id,
                Name = $"Enemy#{id}",
            };
            return true;
        }

        private int EnumerateAoi(long aoiDictPtr, long localPlayerPtr, List<EntityData> output)
        {
            if (!TryReadDictHeader(aoiDictPtr, out _, out int mask, out long table))
                return 0;

            if (!TryReadDictTable(table, mask, out byte[] data))
                return 0;

            int slots = mask + 1;
            int count = 0;
            var seen = new HashSet<long>();

            for (int slot = 0; slot < slots; slot++)
            {
                int off = checked((int)((long)slot * Offsets.PY_DICT_ENTRY_STRIDE));

                // AOI keys are not needed for target enumeration. The live evidence
                // proves the values themselves are BattleGroundAvatar objects, while
                // some key objects/key strings are not reliably readable externally.
                long valueObj = ReadQword(data, off + (int)Offsets.PY_DICT_ENTRY_VALUE);
                if (!IsPlausiblePtr(valueObj) || !seen.Add(valueObj)) continue;

                if (!TryReadBga(valueObj, localPlayerPtr, out EntityData entity)) continue;

                output.Add(entity);
                count++;
                RuntimeDiagnostics.EntityNode(
                    table + off,
                    entity.Ptr,
                    entity.ObType,
                    entity.X,
                    entity.Y,
                    entity.Z);
                RuntimeDiagnostics.EntityAccepted();
            }

            return count;
        }

        public bool Calibrate()
        {
            var snapshot = Enumerate();
            if (snapshot.Count > 0)
            {
                Console.WriteLine(
                    $"  [ENT][CAL] avatarInAoI active; BGA type=0x{TypeBga:X} enemies={snapshot.Count}");
                return true;
            }

            if (_calibrationLogCount++ < 3)
            {
                if (_aoiDictPtr != 0)
                    Console.WriteLine("  [ENT][CAL] AOI dictionary present, but no accepted live BGA values are currently present");
                else
                    Console.WriteLine("  [ENT][CAL] avatarInAoI has not yet been resolved");
            }
            return TypeBga != 0;
        }

        public List<EntityData> Enumerate()
        {
            var result = new List<EntityData>(64);

            long emPtr = _mem.ReadLong(
                _mem.BaseAddress + Offsets.EM_STATIC_PTR,
                "ENT.EM");
            if (!IsPlausiblePtr(emPtr)) return result;

            long playerPtr = _player.GetPlayerPtr(emPtr);
            if (!IsPlausiblePtr(playerPtr)) return result;

            if (!TryReadPlayerDict(playerPtr, out long playerDictPtr))
                return result;

            if (!TryRefreshAoiDict(playerDictPtr))
            {
                if (!TryResolveAvatarKey(playerPtr, playerDictPtr))
                    return result;
            }

            int enemies = EnumerateAoi(_aoiDictPtr, playerPtr, result);

            // The AOI object can be replaced during zone/death transitions. If the
            // cached value suddenly stops looking like a populated dict, force a
            // fresh key lookup on the next frame.
            if (enemies == 0 && _aoiDictPtr != 0)
            {
                if (!TryReadDictHeader(_aoiDictPtr, out int used, out _, out _ ) || used == 0)
                {
                    _avatarKeySlot = -1;
                }
            }

            if (_calibrationLogCount < 3 || (enemies > 0 && _calibrationLogCount == 3))
            {
                Console.WriteLine(
                    $"  [ENT][AOI] aoiDict=0x{_aoiDictPtr:X} enemies={enemies} bgaType=" +
                    (TypeBga == 0 ? "?" : $"0x{TypeBga:X}"));
                _calibrationLogCount++;
            }

            return result;
        }

        public void Invalidate()
        {
            _lastPlayerPtr = 0;
            _lastPlayerDictPtr = 0;
            _lastPlayerDictTable = 0;
            _lastPlayerDictMask = -1;
            _aoiDictPtr = 0;
            _avatarKeySlot = -1;
            _nextAvatarScanMs = 0;
            _avatarKeyObj = 0;
            TypePbga = 0;
            TypeBga = 0;
            _calibrationLogCount = 0;
        }
    }
}
