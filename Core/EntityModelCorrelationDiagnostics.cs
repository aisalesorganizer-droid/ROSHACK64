using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using ROS64Hack.Engine;

namespace ROS64Hack.Core
{
    /// <summary>
    /// One-shot + short continuity diagnostic for the exact unresolved bridge:
    ///
    ///   authoritative/native Entity
    ///          |
    ///          +0xD8  Entity.model backing reference (wrapper/provider)
    ///          |
    ///          +0xE0/+0xE8  native model pointer range
    ///          |
    ///          v
    ///      ModelSkeletal
    ///          +0x38
    ///          |
    ///          v
    ///       SpaceNode
    ///          +0x40 == ModelSkeletal
    ///          +0x48..+0x87 = transform block
    ///
    /// This diagnostic intentionally does NOT walk BGA.+0xB8 or treat arbitrary
    /// pointer runs as bones.  It starts from the already-proven ModelSkeletal
    /// and SpaceNode RTTI/vtable anchors, finds live objects of that exact class,
    /// then reverse-correlates references back to the native Entity layout.
    ///
    /// All scanning is read-only.  The main camera/W2S/ESP/aim path is not used
    /// for the proof and is not modified by the diagnostic.
    /// </summary>
    public sealed class EntityModelCorrelationDiagnostics : IDisposable
    {
        // ── Proven image-relative RTTI/vtable anchors ──────────────────────────
        private const long MODEL_SKELETAL_VTABLE_RVA = 0x001C1ACE0;
        private const long SPACE_NODE_VTABLE_RVA     = 0x001C137C8;
        private const long MODEL_VTABLE_RVA           = 0x001C173E0;

        // ── Proven native object fields ────────────────────────────────────────
        private const int ENTITY_ID                   = 0x18;
        private const int ENTITY_X                    = 0x1C;
        private const int ENTITY_Y                    = 0x20;
        private const int ENTITY_Z                    = 0x24;
        private const int ENTITY_DIRECTION_X          = 0x44;
        private const int ENTITY_DIRECTION_Y          = 0x48;
        private const int ENTITY_DIRECTION_Z          = 0x4C;
        private const int ENTITY_SPACE_ID             = 0x54;
        private const int ENTITY_VEHICLE_ID            = 0x58;
        private const int ENTITY_SELF_CONTROLLED       = 0x5E;
        private const int ENTITY_MODEL                = 0xD8;
        private const int ENTITY_MODELS_BEGIN         = 0xE0;
        private const int ENTITY_MODELS_END           = 0xE8;

        private const int MODEL_SPACE_NODE             = 0x38;
        private const int SPACE_MODEL                  = 0x40;
        private const int SPACE_TRANSFORM              = 0x48;
        private const int SPACE_TRANSFORM_BYTES        = 0x40;

        // ── Python object fields used only for semantic correlation ─────────────
        private const int PY_OB_TYPE                   = 0x08;
        private const int PY_TYPE_NAME                 = 0x18;
        private const int PY_DICT                     = 0x1B0;
        private const int PY_DICT_USED                = 0x18;
        private const int PY_DICT_MASK                = 0x20;
        private const int PY_DICT_TABLE               = 0x28;
        private const int PY_DICT_KEY                 = 0x08;
        private const int PY_DICT_VALUE               = 0x10;
        private const int PY_DICT_STRIDE              = 0x18;
        private const int PY_STRING_VALUE              = 0x20;

        // ── Scan policy ────────────────────────────────────────────────────────
        // A full private-heap scan is deliberately bounded by a generous byte
        // budget.  The diagnostic records the exact scanned bytes/regions so a
        // negative result is never presented as an absolute absence proof.
        private const long MAX_PRIVATE_SCAN_BYTES      = 1536L * 1024 * 1024;
        private const int  SCAN_CHUNK_BYTES             = 0x100000;
        private const int  MAX_MODEL_OBJECTS            = 512;
        private const int  MAX_CORRELATIONS             = 128;
        private const int  MAX_ENTITY_ARRAY_LENGTH      = 64;
        private const int  MAX_ENTITY_ARRAY_INDEX_TRIES = 64;
        private const int  MAX_PY_DICT_SLOTS            = 0x2000;
        private const int  MAX_BGA_TARGETS              = 4;
        private const int  CONTINUITY_SAMPLES           = 12;
        private const int  CONTINUITY_INTERVAL_MS       = 1000;
        private const int  INITIAL_DELAY_MS              = 2500;

        private const float MAX_WORLD_COORD = 50000f;
        private const float ENTITY_NODE_MATCH_DISTANCE = 8.0f;

        private readonly Mem _mem;
        private readonly object _sync = new();
        private readonly HashSet<long> _knownModelPointers = new();
        private readonly Dictionary<long, ModelRecord> _models = new();
        private readonly List<CorrelationRecord> _correlations = new();

        private System.Threading.Timer _timer;
        private int _started;
        private int _disposed;
        private int _sampleIndex;
        private bool _initialScanComplete;
        private long _lastEmPtr;
        private long _modelScanBytes;
        private int _modelScanRegions;
        private long _reverseScanBytes;
        private int _reverseScanRegions;
        private bool _modelScanTruncated;
        private bool _reverseScanTruncated;

        private readonly Dictionary<uint, EntityData> _latestBgaById = new();
        private EntityData _latestLocal;

        private sealed class ModelRecord
        {
            public long Ptr;
            public long Vtable;
            public long SpaceNode;
            public long SpaceVtable;
            public float NodeX;
            public float NodeY;
            public float NodeZ;
            public bool Reciprocal;
        }

        private sealed class CorrelationRecord
        {
            public long EntityPtr;
            public uint EntityId;
            public float EntityX;
            public float EntityY;
            public float EntityZ;
            public bool SelfControlled;
            public long ModelPtr;
            public int ModelArrayIndex;
            public string ModelSource;
            public long SpaceNodePtr;
            public float NodeX;
            public float NodeY;
            public float NodeZ;
            public uint MatchedBgaId;
            public long MatchedBgaPtr;
            public float MatchDistance;
            public string MatchClass;
        }

        private struct MemoryRegion
        {
            public long Base;
            public long Size;
            public uint Protect;
            public uint State;
            public uint Type;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MEMORY_BASIC_INFORMATION64
        {
            public IntPtr BaseAddress;
            public IntPtr AllocationBase;
            public uint AllocationProtect;
            public uint __alignment1;
            public UIntPtr RegionSize;
            public uint State;
            public uint Protect;
            public uint Type;
            public uint __alignment2;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern UIntPtr VirtualQueryEx(
            IntPtr hProcess,
            IntPtr lpAddress,
            out MEMORY_BASIC_INFORMATION64 lpBuffer,
            UIntPtr dwLength);

        public EntityModelCorrelationDiagnostics(Mem mem)
        {
            _mem = mem ?? throw new ArgumentNullException(nameof(mem));
        }

        public void Start()
        {
            if (Interlocked.Exchange(ref _started, 1) != 0)
                return;

            Console.WriteLine();
            Console.WriteLine("============================================================");
            Console.WriteLine("[ENTITY-MODEL-DIAG] TARGETED ENTITY -> MODEL -> SPACENODE");
            Console.WriteLine("============================================================");
            Console.WriteLine("  Scope: native Entity layout -> models[] -> exact ModelSkeletal -> SpaceNode");
            Console.WriteLine("  No BGA.+0xB8 bone guessing; no writes; no camera/W2S/ESP/aim changes");
            Console.WriteLine($"  ModelSkeletal vtable RVA = 0x{MODEL_SKELETAL_VTABLE_RVA:X}");
            Console.WriteLine($"  SpaceNode vtable RVA     = 0x{SPACE_NODE_VTABLE_RVA:X}");
            Console.WriteLine($"  Entity +0xD8/+0xE0/+0xE8  = model backing + model array begin/end");
            Console.WriteLine($"  Continuity samples        = {CONTINUITY_SAMPLES} x {CONTINUITY_INTERVAL_MS} ms");
            Console.WriteLine("============================================================");

            _timer = new System.Threading.Timer(TimerTick, null, INITIAL_DELAY_MS, Timeout.Infinite);
        }

        private void TimerTick(object state)
        {
            if (Volatile.Read(ref _disposed) != 0)
                return;

            try
            {
                // The timer owns the expensive scan.  It never runs inside the
                // 30 Hz overlay read loop, so it cannot directly block camera/W2S.
                if (!_initialScanComplete)
                {
                    RunInitialEvidenceCapture();
                    _initialScanComplete = true;
                    _timer?.Change(CONTINUITY_INTERVAL_MS, CONTINUITY_INTERVAL_MS);
                    return;
                }

                RunContinuitySample();
                if (_sampleIndex >= CONTINUITY_SAMPLES)
                {
                    EmitFinalSummary();
                    _timer?.Change(Timeout.Infinite, Timeout.Infinite);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    $"  [ENTITY-MODEL-DIAG][EXCEPTION] {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// Called from the normal read loop after a coherent local/entity snapshot
        /// is already available.  This is intentionally cheap: it only refreshes
        /// the target identities used by the one-shot/correlated evidence pass.
        /// </summary>
        public void ObserveSnapshot(EntityData local, List<EntityData> enemies, long emPtr)
        {
            if (Volatile.Read(ref _disposed) != 0 || Volatile.Read(ref _started) == 0)
                return;

            if (local != null)
                _latestLocal = CloneEntity(local);

            _latestBgaById.Clear();
            if (enemies != null)
            {
                foreach (EntityData e in enemies)
                {
                    if (e == null || e.Ptr == 0) continue;
                    uint id = e.Id;
                    if (!_latestBgaById.ContainsKey(id))
                        _latestBgaById[id] = CloneEntity(e);
                }
            }

            _lastEmPtr = emPtr;
        }

        private static EntityData CloneEntity(EntityData e)
        {
            return new EntityData
            {
                Ptr = e.Ptr,
                ObType = e.ObType,
                Id = e.Id,
                IsLocalPlayer = e.IsLocalPlayer,
                IsEnemy = e.IsEnemy,
                X = e.X,
                Y = e.Y,
                Z = e.Z,
                Distance = e.Distance,
                Name = e.Name,
                Health = e.Health,
                MaxHealth = e.MaxHealth,
            };
        }

        private void RunInitialEvidenceCapture()
        {
            long started = Stopwatch.GetTimestamp();
            Console.WriteLine();
            Console.WriteLine("[ENTITY-MODEL-DIAG][PHASE 1] LIVE SNAPSHOT / ROOTS");
            Console.WriteLine($"  base=0x{_mem.BaseAddress:X} em=0x{_lastEmPtr:X}");

            LogLocalAndBgaTargets();
            LogEntityManagerRoots();
            LogPythonSemanticBridges();

            Console.WriteLine();
            Console.WriteLine("[ENTITY-MODEL-DIAG][PHASE 2] FIND CURRENT MODELSKELETAL OBJECTS");
            long modelVtable = _mem.BaseAddress + MODEL_SKELETAL_VTABLE_RVA;
            List<long> modelObjects = ScanForExactVtable(modelVtable, MAX_MODEL_OBJECTS);

            Console.WriteLine(
                $"  [MODELSKELETAL-SCAN] vtable=0x{modelVtable:X} objects={modelObjects.Count} " +
                $"privateBytes={_modelScanBytes:N0} regions={_modelScanRegions} truncated={_modelScanTruncated}");

            foreach (long modelPtr in modelObjects)
                InspectModel(modelPtr);

            Console.WriteLine();
            Console.WriteLine("[ENTITY-MODEL-DIAG][PHASE 3] REVERSE-CORRELATE MODEL POINTERS TO ENTITY LAYOUT");

            if (_models.Count == 0)
            {
                Console.WriteLine("  [REVERSE] No exact ModelSkeletal objects passed the object-level fingerprint.");
            }
            else
            {
                ReverseCorrelateModelPointers();
            }

            PrintCorrelationTable();

            double elapsedMs = (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;
            Console.WriteLine($"[ENTITY-MODEL-DIAG][INITIAL-DONE] elapsedMs={elapsedMs:F1}");
        }

        private void LogLocalAndBgaTargets()
        {
            Console.WriteLine();
            Console.WriteLine("[ENTITY-MODEL-DIAG][TARGETS]");
            if (_latestLocal != null)
            {
                uint id = _latestLocal.Id;
                Console.WriteLine(
                    $"  [LOCAL-PY] bga=0x{_latestLocal.Ptr:X} id=0x{id:X8} " +
                    $"xyz=({_latestLocal.X:F3},{_latestLocal.Y:F3},{_latestLocal.Z:F3}) " +
                    $"type=0x{_latestLocal.ObType:X}");
            }
            else
            {
                Console.WriteLine("  [LOCAL-PY] unavailable at capture start");
            }

            foreach (EntityData bga in _latestBgaById.Values.Take(MAX_BGA_TARGETS))
            {
                uint id = bga.Id;
                Console.WriteLine(
                    $"  [BGA-PY] bga=0x{bga.Ptr:X} id=0x{id:X8} " +
                    $"xyz=({bga.X:F3},{bga.Y:F3},{bga.Z:F3}) " +
                    $"type=0x{bga.ObType:X}");
            }
        }

        private void LogEntityManagerRoots()
        {
            long em = _lastEmPtr;
            if (!IsPlausiblePtr(em))
            {
                Console.WriteLine("  [EM] unavailable");
                return;
            }

            Console.WriteLine();
            Console.WriteLine("[ENTITY-MODEL-DIAG][EM-ROOTS]");
            long controlled = ReadQuiet(em + Offsets.EM_CONTROLLED_ENT, "controlled");
            long allEntities = ReadQuiet(em + Offsets.EM_ALL_ENTITIES, "allEntities");

            Console.WriteLine($"  [EM] ptr=0x{em:X} vtable=0x{ReadQuiet(em, "em-vtable"):X}");
            Console.WriteLine($"  [EM+0x10F8] controlledRoot=0x{controlled:X}");
            Console.WriteLine($"  [EM+0x10E0] candidateRoot=0x{allEntities:X}");

            if (IsPlausiblePtr(controlled))
            {
                DumpQwords("CONTROLLED-CONTAINER", controlled, 8);
                long root = ReadQuiet(controlled + Offsets.NODE_PARENT, "controlled-root");
                Console.WriteLine($"  [CONTROLLED] root=0x{root:X}");
                if (IsPlausiblePtr(root)) DumpQwords("CONTROLLED-NODE", root, 8);
            }

            if (IsPlausiblePtr(allEntities))
            {
                DumpQwords("ALL-ENTITIES-ROOT", allEntities, 16);
                long childL = ReadQuiet(allEntities + Offsets.NODE_LEFT, "all-left");
                long childR = ReadQuiet(allEntities + Offsets.NODE_RIGHT, "all-right");
                long value20 = ReadQuiet(allEntities + 0x20, "all-value20");
                long value28 = ReadQuiet(allEntities + 0x28, "all-value28");
                Console.WriteLine(
                    $"  [ALL-ENTITIES-FINGERPRINT] left=0x{childL:X} right=0x{childR:X} " +
                    $"+0x20=0x{value20:X} +0x28=0x{value28:X}");

                // Do not assume +0x20 or +0x28 is an entity. Test both using the
                // native Entity fingerprint and report why a candidate is rejected.
                TestEntityCandidate(value20, "EM+0x10E0+0x20");
                TestEntityCandidate(value28, "EM+0x10E0+0x28");
            }
        }

        private void LogPythonSemanticBridges()
        {
            Console.WriteLine();
            Console.WriteLine("[ENTITY-MODEL-DIAG][PYTHON-SEMANTIC-BRIDGES]");
            LogPythonDictKeys("LOCAL", _latestLocal?.Ptr ?? 0);
            foreach (EntityData bga in _latestBgaById.Values.Take(MAX_BGA_TARGETS))
                LogPythonDictKeys($"BGA-0x{bga.Id:X8}", bga.Ptr);
        }

        private void LogPythonDictKeys(string label, long obj)
        {
            if (!IsPlausiblePtr(obj))
            {
                Console.WriteLine($"  [PY-DICT] {label}: object unavailable");
                return;
            }

            long dict = ReadQuiet(obj + PY_DICT, "py-dict");
            if (!IsPlausiblePtr(dict))
            {
                Console.WriteLine($"  [PY-DICT] {label}: dict=0x{dict:X} unavailable");
                return;
            }

            long usedRaw = ReadQuiet(dict + PY_DICT_USED, "py-used");
            long maskRaw = ReadQuiet(dict + PY_DICT_MASK, "py-mask");
            long table = ReadQuiet(dict + PY_DICT_TABLE, "py-table");
            if (usedRaw < 0 || usedRaw > MAX_PY_DICT_SLOTS ||
                maskRaw < 0 || maskRaw >= MAX_PY_DICT_SLOTS || !IsPlausiblePtr(table))
            {
                Console.WriteLine($"  [PY-DICT] {label}: invalid header dict=0x{dict:X}");
                return;
            }

            int slots = (int)maskRaw + 1;
            int hits = 0;
            string[] interesting = { "entity", "model", "_model", "gameObject", "transform", "spaceID" };

            for (int i = 0; i < slots && i < MAX_PY_DICT_SLOTS; i++)
            {
                long entry = table + (long)i * PY_DICT_STRIDE;
                long key = ReadQuiet(entry + PY_DICT_KEY, "py-key");
                long value = ReadQuiet(entry + PY_DICT_VALUE, "py-value");
                if (!IsPlausiblePtr(key) || !IsPlausiblePtr(value)) continue;

                string text = ReadPythonStringQuiet(key);
                if (string.IsNullOrEmpty(text)) continue;
                if (!interesting.Any(x => string.Equals(x, text, StringComparison.Ordinal))) continue;

                hits++;
                Console.WriteLine(
                    $"  [PY-DICT-HIT] {label} slot={i} key='{text}' value=0x{value:X} " +
                    $"valueType={ReadPythonTypeName(value)}");
            }

            Console.WriteLine(
                $"  [PY-DICT] {label}: dict=0x{dict:X} used={usedRaw} mask=0x{maskRaw:X} " +
                $"slots={slots} semanticHits={hits}");
        }

        private List<long> ScanForExactVtable(long vtable, int maxObjects)
        {
            var objects = new List<long>();
            var seenObjects = new HashSet<long>();

            foreach (MemoryRegion region in EnumerateReadablePrivateRegions())
            {
                if (objects.Count >= maxObjects) break;
                if (_modelScanBytes >= MAX_PRIVATE_SCAN_BYTES) break;

                long regionBytes = Math.Min(region.Size, MAX_PRIVATE_SCAN_BYTES - _modelScanBytes);
                if (regionBytes <= 0) break;

                long cursor = 0;
                while (cursor < regionBytes && objects.Count < maxObjects)
                {
                    int chunk = (int)Math.Min(SCAN_CHUNK_BYTES, regionBytes - cursor);
                    var buffer = new byte[chunk];
                    long address = region.Base + cursor;
                    if (!_mem.ReadBytesQuiet(address, buffer, 0, chunk))
                    {
                        cursor += chunk;
                        _modelScanBytes += chunk;
                        continue;
                    }

                    for (int off = 0; off + 8 <= buffer.Length; off += 8)
                    {
                        long candidateVtable = BitConverter.ToInt64(buffer, off);
                        if (candidateVtable != vtable) continue;

                        long ptr = address + off;
                        if (!seenObjects.Add(ptr)) continue;

                        if (!ValidateModelObject(ptr, out ModelRecord record))
                            continue;

                        objects.Add(ptr);
                        _models[ptr] = record;
                    }

                    cursor += chunk;
                    _modelScanBytes += chunk;
                }

                _modelScanRegions++;
            }

            if (_modelScanBytes >= MAX_PRIVATE_SCAN_BYTES)
                _modelScanTruncated = true;

            return objects;
        }

        private bool ValidateModelObject(long ptr, out ModelRecord record)
        {
            record = null;
            if (!IsPlausiblePtr(ptr)) return false;

            long modelVtable = ReadQuiet(ptr, "model-vtable");
            if (modelVtable != _mem.BaseAddress + MODEL_SKELETAL_VTABLE_RVA)
                return false;

            long node = ReadQuiet(ptr + MODEL_SPACE_NODE, "model-space");
            if (!IsPlausiblePtr(node)) return false;

            long spaceVtable = ReadQuiet(node, "space-vtable");
            if (spaceVtable != _mem.BaseAddress + SPACE_NODE_VTABLE_RVA)
                return false;

            long reciprocal = ReadQuiet(node + SPACE_MODEL, "space-model");
            bool reciprocalOk = reciprocal == ptr;

            float x = ReadFloatQuiet(node + SPACE_TRANSFORM + 0x00);
            float y = ReadFloatQuiet(node + SPACE_TRANSFORM + 0x04);
            float z = ReadFloatQuiet(node + SPACE_TRANSFORM + 0x08);
            if (!IsValidCoordinate(x) || !IsValidCoordinate(y) || !IsValidCoordinate(z))
                return false;

            record = new ModelRecord
            {
                Ptr = ptr,
                Vtable = modelVtable,
                SpaceNode = node,
                SpaceVtable = spaceVtable,
                NodeX = x,
                NodeY = y,
                NodeZ = z,
                Reciprocal = reciprocalOk,
            };

            return true;
        }

        private void InspectModel(long modelPtr)
        {
            if (!_models.TryGetValue(modelPtr, out ModelRecord model))
                return;

            Console.WriteLine();
            Console.WriteLine(
                $"  [MODEL] ptr=0x{model.Ptr:X} vtable=0x{model.Vtable:X} " +
                $"space=0x{model.SpaceNode:X} spaceVtable=0x{model.SpaceVtable:X} " +
                $"reciprocal={model.Reciprocal} nodeXYZ=({model.NodeX:F3},{model.NodeY:F3},{model.NodeZ:F3})");

            DumpQwords("MODEL+0x00", modelPtr, 18);
            DumpQwords("SPACENODE+0x40", model.SpaceNode + 0x40, 10);
            DumpTransform(model.SpaceNode);

            foreach (EntityData bga in TargetEntities())
            {
                float d = Distance(model.NodeX, model.NodeY, model.NodeZ, bga.X, bga.Y, bga.Z);
                Console.WriteLine(
                    $"  [MODEL↔BGA-DIST] bgaId=0x{bga.Id:X8} " +
                    $"dist={d:F3} bgaXYZ=({bga.X:F3},{bga.Y:F3},{bga.Z:F3})");
            }
        }

        private void ReverseCorrelateModelPointers()
        {
            var modelMap = new Dictionary<long, ModelRecord>(_models);
            if (modelMap.Count == 0) return;

            long started = Stopwatch.GetTimestamp();
            long bytes = 0;
            int regions = 0;
            int refs = 0;
            var seenCorrelation = new HashSet<string>(StringComparer.Ordinal);

            foreach (MemoryRegion region in EnumerateReadablePrivateRegions())
            {
                if (bytes >= MAX_PRIVATE_SCAN_BYTES)
                {
                    _reverseScanTruncated = true;
                    break;
                }

                long regionBytes = Math.Min(region.Size, MAX_PRIVATE_SCAN_BYTES - bytes);
                if (regionBytes <= 0) break;

                long cursor = 0;
                while (cursor < regionBytes)
                {
                    int chunk = (int)Math.Min(SCAN_CHUNK_BYTES, regionBytes - cursor);
                    var buffer = new byte[chunk];
                    long address = region.Base + cursor;

                    if (_mem.ReadBytesQuiet(address, buffer, 0, chunk))
                    {
                        for (int off = 0; off + 8 <= buffer.Length; off += 8)
                        {
                            long q = BitConverter.ToInt64(buffer, off);
                            if (!modelMap.ContainsKey(q)) continue;

                            long refAddress = address + off;
                            refs++;
                            ProbeModelReference(refAddress, q, seenCorrelation);
                        }
                    }

                    cursor += chunk;
                    bytes += chunk;
                }

                regions++;
                _reverseScanRegions = regions;
                if (_correlations.Count >= MAX_CORRELATIONS)
                    break;
            }

            _reverseScanBytes = bytes;
            _reverseScanRegions = regions;
            if (bytes >= MAX_PRIVATE_SCAN_BYTES)
                _reverseScanTruncated = true;

            double ms = (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;
            Console.WriteLine(
                $"  [REVERSE-SCAN] exactModelRefs={refs} correlations={_correlations.Count} " +
                $"bytes={bytes:N0} regions={regions} truncated={_reverseScanTruncated} elapsedMs={ms:F1}");
        }

        private void ProbeModelReference(long refAddress, long modelPtr, HashSet<string> seen)
        {
            if (_correlations.Count >= MAX_CORRELATIONS) return;
            if (!_models.ContainsKey(modelPtr)) return;

            // Path A: Entity.model backing field at +0xD8.
            long directEntity = refAddress - ENTITY_MODEL;
            TestEntityOwner(directEntity, modelPtr, -1, "+0xD8", refAddress, seen);

            // Path B: models.begin at +0xE0 with a bounded array index.  This is the
            // strong static candidate for native Entity -> Model entries.
            for (int index = 0; index < MAX_ENTITY_ARRAY_INDEX_TRIES; index++)
            {
                long owner = refAddress - ENTITY_MODELS_BEGIN - index * 8L;
                if (!IsPlausiblePtr(owner)) continue;
                TestEntityOwner(owner, modelPtr, index, $"+0xE0[{index}]", refAddress, seen);
                if (_correlations.Count >= MAX_CORRELATIONS) return;
            }
        }

        private void TestEntityOwner(
            long entityPtr,
            long modelPtr,
            int modelIndex,
            string source,
            long refAddress,
            HashSet<string> seen)
        {
            if (!IsPlausiblePtr(entityPtr)) return;
            if (!LooksLikeNativeEntity(entityPtr, out uint id, out float x, out float y, out float z,
                out bool selfControlled, out long modelField, out long begin, out long end))
                return;

            long count = 0;
            if (IsPlausiblePtr(begin) && IsPlausiblePtr(end) && end >= begin)
                count = (end - begin) / 8L;

            if (modelIndex >= 0 && (modelIndex >= MAX_ENTITY_ARRAY_LENGTH ||
                modelIndex >= count || !IsPlausiblePtr(begin) || begin + modelIndex * 8L != refAddress))
                return;

            if (source == "+0xD8" && modelField != modelPtr)
                return;

            string key = $"{entityPtr:X}|{modelPtr:X}|{modelIndex}|{source}";
            if (!seen.Add(key)) return;

            _correlations.Add(new CorrelationRecord
            {
                EntityPtr = entityPtr,
                EntityId = id,
                EntityX = x,
                EntityY = y,
                EntityZ = z,
                SelfControlled = selfControlled,
                ModelPtr = modelPtr,
                ModelArrayIndex = modelIndex,
                ModelSource = source,
                SpaceNodePtr = _models[modelPtr].SpaceNode,
                NodeX = _models[modelPtr].NodeX,
                NodeY = _models[modelPtr].NodeY,
                NodeZ = _models[modelPtr].NodeZ,
                MatchedBgaId = 0,
                MatchedBgaPtr = 0,
                MatchDistance = float.NaN,
                MatchClass = "NO_BGA_MATCH",
            });

            CorrelationRecord added = _correlations[^1];
            CorrelateWithBga(added);

            Console.WriteLine();
            Console.WriteLine(
                $"  [NATIVE-ENTITY-CANDIDATE] entity=0x{entityPtr:X} id=0x{id:X8} " +
                $"selfControlled={(selfControlled ? 1 : 0)} xyz=({x:F3},{y:F3},{z:F3})");
            Console.WriteLine(
                $"    source={source} refAddress=0x{refAddress:X} model=0x{modelPtr:X} " +
                $"modelField(+D8)=0x{modelField:X} models=[0x{begin:X},0x{end:X}) count={count}");
            Console.WriteLine(
                $"    modelSpace=0x{_models[modelPtr].SpaceNode:X} " +
                $"nodeXYZ=({_models[modelPtr].NodeX:F3},{_models[modelPtr].NodeY:F3},{_models[modelPtr].NodeZ:F3}) " +
                $"entityNodeDist={Distance(x,y,z,_models[modelPtr].NodeX,_models[modelPtr].NodeY,_models[modelPtr].NodeZ):F3}");

            DumpQwords("NATIVE-ENTITY", entityPtr, 32);
            DumpModelArray(begin, end, modelPtr);

            if (IsPlausiblePtr(modelField) && modelField != modelPtr)
                DumpQwords("ENTITY+0xD8-OBJECT", modelField, 24);
        }

        private void DumpModelArray(long begin, long end, long matchedModel)
        {
            if (!IsPlausiblePtr(begin) || !IsPlausiblePtr(end) || end < begin)
                return;

            long count = (end - begin) / 8L;
            if (count <= 0 || count > MAX_ENTITY_ARRAY_LENGTH)
                return;

            Console.WriteLine(
                $"    [ENTITY-MODELS] begin=0x{begin:X} end=0x{end:X} count={count}");

            for (int i = 0; i < count; i++)
            {
                long ptr = ReadQuiet(begin + i * 8L, "entity-model-array");
                string marker = ptr == matchedModel ? "  <== MATCHED MODELSKELETAL" : string.Empty;
                Console.WriteLine($"      [{i:D2}] 0x{ptr:X}{marker}");
            }
        }

        private bool LooksLikeNativeEntity(
            long ptr,
            out uint id,
            out float x,
            out float y,
            out float z,
            out bool selfControlled,
            out long modelField,
            out long begin,
            out long end)
        {
            id = 0;
            x = y = z = 0f;
            selfControlled = false;
            modelField = begin = end = 0;

            if (!IsPlausiblePtr(ptr)) return false;

            id = unchecked((uint)ReadQuiet(ptr + ENTITY_ID, "entity-id"));
            x = ReadFloatQuiet(ptr + ENTITY_X);
            y = ReadFloatQuiet(ptr + ENTITY_Y);
            z = ReadFloatQuiet(ptr + ENTITY_Z);
            modelField = ReadQuiet(ptr + ENTITY_MODEL, "entity-model");
            begin = ReadQuiet(ptr + ENTITY_MODELS_BEGIN, "entity-model-begin");
            end = ReadQuiet(ptr + ENTITY_MODELS_END, "entity-model-end");
            selfControlled = ReadByteQuiet(ptr + ENTITY_SELF_CONTROLLED) != 0;

            if (!IsValidCoordinate(x) || !IsValidCoordinate(y) || !IsValidCoordinate(z))
                return false;

            bool idPlausible = id > 0 && id < 0x7FFFFFFF;
            bool modelPlausible = IsPlausiblePtr(modelField);
            bool arrayPlausible = IsPlausiblePtr(begin) && IsPlausiblePtr(end) && end >= begin &&
                                  end - begin <= MAX_ENTITY_ARRAY_LENGTH * 8L &&
                                  ((end - begin) % 8L == 0);

            // Require more than just XYZ.  The purpose is specifically to reject
            // arbitrary heap objects whose first 0x30 bytes happen to be floats.
            return (idPlausible && (modelPlausible || arrayPlausible));
        }

        private void CorrelateWithBga(CorrelationRecord record)
        {
            EntityData best = null;
            float bestDistance = float.MaxValue;

            // Correlation uses every AOI BGA captured this read cycle. The
            // four-entity limit applies only to verbose target dumps, not to
            // identity matching.
            foreach (EntityData bga in _latestBgaById.Values)
            {
                uint id = bga.Id;
                float d = Distance(record.EntityX, record.EntityY, record.EntityZ, bga.X, bga.Y, bga.Z);
                if (id == record.EntityId)
                {
                    record.MatchedBgaId = id;
                    record.MatchedBgaPtr = bga.Ptr;
                    record.MatchDistance = d;
                    record.MatchClass = d <= ENTITY_NODE_MATCH_DISTANCE ? "ID+XYZ_MATCH" : "ID_MATCH";
                    return;
                }

                if (d < bestDistance)
                {
                    bestDistance = d;
                    best = bga;
                }
            }

            if (best != null && bestDistance <= ENTITY_NODE_MATCH_DISTANCE)
            {
                record.MatchedBgaId = best.Id;
                record.MatchedBgaPtr = best.Ptr;
                record.MatchDistance = bestDistance;
                record.MatchClass = "XYZ_NEAREST_MATCH";
            }
        }

        private void PrintCorrelationTable()
        {
            Console.WriteLine();
            Console.WriteLine("[ENTITY-MODEL-DIAG][CORRELATION-TABLE]");
            if (_correlations.Count == 0)
            {
                Console.WriteLine("  NO_NATIVE_ENTITY_MODEL_CORRELATION_FOUND_IN_SCAN_SCOPE");
                Console.WriteLine(
                    $"  modelScanBytes={_modelScanBytes:N0} modelScanRegions={_modelScanRegions} modelScanTruncated={_modelScanTruncated} " +
                    $"reverseScanBytes={_reverseScanBytes:N0} reverseScanRegions={_reverseScanRegions} reverseScanTruncated={_reverseScanTruncated}");
                return;
            }

            foreach (CorrelationRecord c in _correlations)
            {
                float entityNodeDist = Distance(c.EntityX, c.EntityY, c.EntityZ, c.NodeX, c.NodeY, c.NodeZ);
                Console.WriteLine(
                    $"  entity=0x{c.EntityPtr:X} id=0x{c.EntityId:X8} self={Convert.ToInt32(c.SelfControlled)} " +
                    $"model=0x{c.ModelPtr:X} src={c.ModelSource} idx={c.ModelArrayIndex} " +
                    $"node=0x{c.SpaceNodePtr:X} " +
                    $"entityXYZ=({c.EntityX:F2},{c.EntityY:F2},{c.EntityZ:F2}) " +
                    $"nodeXYZ=({c.NodeX:F2},{c.NodeY:F2},{c.NodeZ:F2}) " +
                    $"dEntityNode={entityNodeDist:F3} " +
                    $"bga=0x{c.MatchedBgaPtr:X} match={c.MatchClass} dBga={c.MatchDistance:F3}");
            }
        }

        private void RunContinuitySample()
        {
            _sampleIndex++;
            Console.WriteLine();
            Console.WriteLine(
                $"[ENTITY-MODEL-DIAG][CONTINUITY { _sampleIndex }/{CONTINUITY_SAMPLES }]");

            if (_correlations.Count == 0)
            {
                Console.WriteLine("  no established native Entity↔Model correlation; recording target BGA motion only");
                LogLocalAndBgaTargets();
                return;
            }

            foreach (CorrelationRecord c in _correlations.Take(24))
            {
                uint currentId = unchecked((uint)ReadQuiet(c.EntityPtr + ENTITY_ID, "cont-id"));
                float ex = ReadFloatQuiet(c.EntityPtr + ENTITY_X);
                float ey = ReadFloatQuiet(c.EntityPtr + ENTITY_Y);
                float ez = ReadFloatQuiet(c.EntityPtr + ENTITY_Z);
                long modelField = ReadQuiet(c.EntityPtr + ENTITY_MODEL, "cont-model-field");
                long begin = ReadQuiet(c.EntityPtr + ENTITY_MODELS_BEGIN, "cont-begin");
                long end = ReadQuiet(c.EntityPtr + ENTITY_MODELS_END, "cont-end");
                long node = ReadQuiet(c.ModelPtr + MODEL_SPACE_NODE, "cont-space");
                float nx = ReadFloatQuiet(node + SPACE_TRANSFORM + 0x00);
                float ny = ReadFloatQuiet(node + SPACE_TRANSFORM + 0x04);
                float nz = ReadFloatQuiet(node + SPACE_TRANSFORM + 0x08);

                uint bgaId = 0;
                float bgaDist = float.NaN;
                if (_latestBgaById.TryGetValue(currentId, out EntityData bga))
                {
                    bgaId = currentId;
                    bgaDist = Distance(ex, ey, ez, bga.X, bga.Y, bga.Z);
                }

                Console.WriteLine(
                    $"  [LINK] ent=0x{c.EntityPtr:X} id=0x{currentId:X8} idStable={(currentId == c.EntityId ? 1 : 0)} " +
                    $"xyz=({ex:F2},{ey:F2},{ez:F2}) " +
                    $"model=0x{c.ModelPtr:X} modelD8={(modelField == c.ModelPtr ? 1 : 0)} " +
                    $"modelsSpan=0x{begin:X}->0x{end:X} " +
                    $"node=0x{node:X} nodeXYZ=({nx:F2},{ny:F2},{nz:F2}) " +
                    $"dEntNode={Distance(ex,ey,ez,nx,ny,nz):F3} " +
                    $"bgaId=0x{bgaId:X8} dEntBga={(float.IsNaN(bgaDist) ? float.NaN : bgaDist):F3}");
            }
        }

        private void EmitFinalSummary()
        {
            Console.WriteLine();
            Console.WriteLine("============================================================");
            Console.WriteLine("[ENTITY-MODEL-DIAG][FINAL EVIDENCE]");
            Console.WriteLine("============================================================");
            Console.WriteLine(
                $"  modelskeletals={_models.Count} correlations={_correlations.Count} " +
                $"modelScanBytes={_modelScanBytes:N0} reverseScanBytes={_reverseScanBytes:N0} " +
                $"modelRegions={_modelScanRegions} reverseRegions={_reverseScanRegions} " +
                $"modelTruncated={_modelScanTruncated} reverseTruncated={_reverseScanTruncated}");
            Console.WriteLine(
                "  Required proof chain: native Entity -> models/_model -> exact ModelSkeletal -> exact SpaceNode -> transform");

            bool hasIdMatch = _correlations.Any(c => c.MatchClass == "ID+XYZ_MATCH");
            bool hasNodeMatch = _correlations.Any(c =>
                Distance(c.EntityX, c.EntityY, c.EntityZ, c.NodeX, c.NodeY, c.NodeZ) <= ENTITY_NODE_MATCH_DISTANCE);
            bool hasLocal = _correlations.Any(c => c.SelfControlled);

            Console.WriteLine($"  [PROOF] nativeEntityFound={(_correlations.Count > 0 ? 1 : 0)}");
            Console.WriteLine($"  [PROOF] entityIdXYZMatchesBga={(hasIdMatch ? 1 : 0)}");
            Console.WriteLine($"  [PROOF] entityXYZMatchesSpaceNode={(hasNodeMatch ? 1 : 0)}");
            Console.WriteLine($"  [PROOF] selfControlledEntityFound={(hasLocal ? 1 : 0)}");

            if (hasIdMatch && hasNodeMatch)
            {
                Console.WriteLine("  [PROOF-RESULT] TARGET CHAIN CLOSED FOR AT LEAST ONE LIVE ENTITY");
                Console.WriteLine("  [PROOF-RESULT] This is sufficient evidence to implement the Entity->Model->SpaceNode path without BGA.+0xB8 guessing.");
            }
            else
            {
                Console.WriteLine("  [PROOF-RESULT] Chain NOT fully closed in this scan scope. See raw candidates/rejection reasons above.");
            }

            Console.WriteLine("============================================================");
        }

        private IEnumerable<EntityData> TargetEntities()
        {
            if (_latestLocal != null)
                yield return _latestLocal;

            foreach (EntityData e in _latestBgaById.Values.Take(MAX_BGA_TARGETS))
                yield return e;
        }

        private uint ReadEntityId(long bgaPtr)
        {
            if (!IsPlausiblePtr(bgaPtr)) return 0;
            return unchecked((uint)ReadQuiet(bgaPtr + ENTITY_ID, "bga-id"));
        }

        private void DumpTransform(long nodePtr)
        {
            byte[] bytes = ReadQuietBytes(nodePtr + SPACE_TRANSFORM, SPACE_TRANSFORM_BYTES);
            if (bytes == null || bytes.Length != SPACE_TRANSFORM_BYTES)
                return;

            var values = new float[SPACE_TRANSFORM_BYTES / 4];
            for (int i = 0; i < values.Length; i++)
                values[i] = BitConverter.ToSingle(bytes, i * 4);

            Console.WriteLine("  [SPACENODE-MATRIX] +0x48..+0x87 float32:");
            Console.WriteLine(
                $"    M0=({values[0]:F5},{values[1]:F5},{values[2]:F5},{values[3]:F5})");
            Console.WriteLine(
                $"    M1=({values[4]:F5},{values[5]:F5},{values[6]:F5},{values[7]:F5})");
            Console.WriteLine(
                $"    M2=({values[8]:F5},{values[9]:F5},{values[10]:F5},{values[11]:F5})");
            Console.WriteLine(
                $"    M3=({values[12]:F5},{values[13]:F5},{values[14]:F5},{values[15]:F5})");
        }

        private void DumpQwords(string label, long ptr, int count)
        {
            if (!IsPlausiblePtr(ptr) || count <= 0) return;
            Console.WriteLine($"  [{label}] ptr=0x{ptr:X} qwords={count}");
            byte[] bytes = ReadQuietBytes(ptr, checked(count * 8));
            if (bytes == null) return;

            for (int i = 0; i < count; i++)
            {
                long value = BitConverter.ToInt64(bytes, i * 8);
                Console.WriteLine($"    +0x{i * 8:X3}=0x{value:X16}");
            }
        }

        private string ReadPythonStringQuiet(long keyObj)
        {
            if (!IsPlausiblePtr(keyObj)) return string.Empty;
            long chars = ReadQuiet(keyObj + PY_STRING_VALUE, "py-string");
            if (!IsPlausiblePtr(chars)) return string.Empty;
            byte[] bytes = ReadQuietBytes(chars, 96);
            if (bytes == null) return string.Empty;
            int end = Array.IndexOf(bytes, (byte)0);
            if (end < 0) end = bytes.Length;
            return Encoding.ASCII.GetString(bytes, 0, end);
        }

        private string ReadPythonTypeName(long obj)
        {
            if (!IsPlausiblePtr(obj)) return "<invalid>";
            long type = ReadQuiet(obj + PY_OB_TYPE, "py-value-type");
            if (!IsPlausiblePtr(type)) return "<no-type>";
            long namePtr = ReadQuiet(type + PY_TYPE_NAME, "py-type-name");
            if (!IsPlausiblePtr(namePtr)) return $"<type=0x{type:X}>";
            byte[] bytes = ReadQuietBytes(namePtr, 96);
            if (bytes == null) return $"<type=0x{type:X}>";
            int end = Array.IndexOf(bytes, (byte)0);
            if (end < 0) end = bytes.Length;
            return Encoding.ASCII.GetString(bytes, 0, end);
        }

        private long ReadQuiet(long address, string tag)
        {
            byte[] bytes = ReadQuietBytes(address, 8);
            return bytes == null ? 0 : BitConverter.ToInt64(bytes, 0);
        }

        private float ReadFloatQuiet(long address)
        {
            byte[] bytes = ReadQuietBytes(address, 4);
            return bytes == null ? float.NaN : BitConverter.ToSingle(bytes, 0);
        }

        private byte ReadByteQuiet(long address)
        {
            byte[] bytes = ReadQuietBytes(address, 1);
            return bytes == null ? (byte)0 : bytes[0];
        }

        private byte[] ReadQuietBytes(long address, int size)
        {
            if (!IsPlausiblePtr(address) || size <= 0 || size > 0x10000)
                return null;

            var data = new byte[size];
            return _mem.ReadBytesQuiet(address, data, 0, size) ? data : null;
        }

        private void TestEntityCandidate(long ptr, string source)
        {
            if (LooksLikeNativeEntity(ptr, out uint id, out float x, out float y, out float z,
                out bool selfControlled, out long modelField, out long begin, out long end))
            {
                Console.WriteLine(
                    $"  [ENTITY-CANDIDATE-ACCEPT] source={source} ptr=0x{ptr:X} id=0x{id:X8} " +
                    $"self={Convert.ToInt32(selfControlled)} xyz=({x:F2},{y:F2},{z:F2}) " +
                    $"model=0x{modelField:X} models=[0x{begin:X},0x{end:X})");
            }
            else
            {
                Console.WriteLine($"  [ENTITY-CANDIDATE-REJECT] source={source} ptr=0x{ptr:X}");
            }
        }

        private IEnumerable<MemoryRegion> EnumerateReadablePrivateRegions()
        {
            if (_mem.Handle == IntPtr.Zero)
                yield break;

            IntPtr cursor = IntPtr.Zero;
            int mbiSize = Marshal.SizeOf<MEMORY_BASIC_INFORMATION64>();

            while (true)
            {
                UIntPtr result = VirtualQueryEx(
                    _mem.Handle,
                    cursor,
                    out MEMORY_BASIC_INFORMATION64 mbi,
                    (UIntPtr)mbiSize);

                if (result == UIntPtr.Zero)
                    yield break;

                long baseAddress = mbi.BaseAddress.ToInt64();
                long size = unchecked((long)mbi.RegionSize.ToUInt64());
                if (size <= 0)
                    yield break;

                if (mbi.State == 0x1000 && mbi.Type == 0x20000 && IsReadableProtection(mbi.Protect))
                {
                    yield return new MemoryRegion
                    {
                        Base = baseAddress,
                        Size = size,
                        Protect = mbi.Protect,
                        State = mbi.State,
                        Type = mbi.Type,
                    };
                }

                long next = baseAddress + size;
                if (next <= baseAddress)
                    yield break;

                cursor = new IntPtr(next);
            }
        }

        private static bool IsReadableProtection(uint protect)
        {
            const uint PAGE_NOACCESS = 0x01;
            const uint PAGE_GUARD = 0x100;
            uint basic = protect & 0xFF;
            if (basic == PAGE_NOACCESS || (protect & PAGE_GUARD) != 0)
                return false;

            return basic == 0x02 || // PAGE_READONLY
                   basic == 0x04 || // PAGE_READWRITE
                   basic == 0x08 || // PAGE_WRITECOPY
                   basic == 0x20 || // PAGE_EXECUTE_READ
                   basic == 0x40 || // PAGE_EXECUTE_READWRITE
                   basic == 0x80;   // PAGE_EXECUTE_WRITECOPY
        }

        private static bool IsPlausiblePtr(long ptr) =>
            ptr >= 0x10000 && ptr < 0x0000800000000000L;

        private static bool IsValidCoordinate(float v) =>
            float.IsFinite(v) && v > -MAX_WORLD_COORD && v < MAX_WORLD_COORD;

        private static float Distance(float ax, float ay, float az, float bx, float by, float bz)
        {
            float dx = ax - bx;
            float dy = ay - by;
            float dz = az - bz;
            return MathF.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            try { _timer?.Dispose(); } catch { }
            _timer = null;
        }
    }
}
