using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using ROS64Hack.Core;

namespace ROS64Hack.Engine
{
    /// <summary>
    /// Acquires the live camera pointer via heap scan.
    ///
    /// STATIC CHAIN STATUS: PERMANENTLY BROKEN on this private server build.
    ///   Chain: [base + 0x03151EF0] → 0x14289D630 → 0x141A431CC → cam_ptr
    ///   Problem: ptr2 = 0x141A431CC is inside the .exe image section, not a heap
    ///   address. Reading it returns 0x8D4820EC83485340 (garbage). Confirmed across
    ///   all sessions. Chain is disabled.
    ///
    /// HEAP SCAN: Scans committed RW memory for camera-shaped data. Candidates are
    ///   validated from the already-read scan buffer using YFocal, basis vectors,
    ///   and position.
    ///
    ///   NOTE: The old axis-aligned shortcut was removed because it could reject
    ///   legitimate camera orientations. Candidate acceptance is now based on the
    ///   complete orthonormal basis plus sane projection data.
    ///
    /// CAMERA POOL: All structurally valid candidates are cached. Initial/recovery
    ///   selection prefers continuity with the last known-good camera; player
    ///   proximity is only a secondary cue. Normal frames read the selected camera.
    ///
    /// RECOVERY: Triggered only at startup or when the active camera becomes structurally
    ///   invalid/unreadable. Player-to-camera distance is diagnostic telemetry only.
    /// </summary>
    public class Camera
    {
        // ── Win32 ──────────────────────────────────────────────────────────────

        [DllImport("kernel32.dll")]
        private static extern int VirtualQueryEx(
            IntPtr hProcess, IntPtr lpAddress,
            out MEMORY_BASIC_INFORMATION lpBuffer, uint dwLength);

        [StructLayout(LayoutKind.Sequential)]
        private struct MEMORY_BASIC_INFORMATION
        {
            public IntPtr BaseAddress;
            public IntPtr AllocationBase;
            public uint   AllocationProtect;
            public IntPtr RegionSize;
            public uint   State;
            public uint   Protect;
            public uint   Type;
        }

        private const uint MEM_COMMIT       = 0x1000;
        private const uint PAGE_NOACCESS    = 0x01;
        private const uint PAGE_GUARD       = 0x100;
        private const uint PAGE_READWRITE   = 0x04;
        private const uint PAGE_EXEC_RW     = 0x40;
        private const uint PAGE_WRITECOPY   = 0x08;
        private const uint PAGE_EXEC_WC     = 0x80;

        // ── State ──────────────────────────────────────────────────────────────

        private readonly Mem _mem;

        private readonly List<long> _camPool = new List<long>(8);
        private readonly byte[] _cameraCoreBuffer = new byte[0xA0];
        private const int CAMERA_SCAN_MIN_BYTES = 0x324;
        private const float CAMERA_ASPECT_FIELD = 1.81560f;
        private const float CAMERA_ASPECT_TOLERANCE = 0.05f;
        private readonly byte[] _cameraXFocalBuffer = new byte[4];
        private readonly byte[] _cameraFocalProbeBuffer = new byte[0x100];
        private readonly byte[] _cameraOrthoBuffer = new byte[1];
        private int  _activeCamIdx  = -1;

        private bool _needsSelectionScan = true;
        private long _lastGoodCamPtr = 0;

        // Selection retry throttle. A candidate must first pass the proven camera
        // identity/structure gates; player distance is used as a ranking cue, not as
        // a hard rejection criterion, because scoped camera states can move far away.
        private long _nextSelectionRetryTicks = 0;

        // Scope/ADS diagnostics. These fields only observe the right-mouse transition;
        // they do not alter camera selection, recovery, or projection behavior.
        private bool _adsStateInitialized = false;
        private bool _adsDown = false;
        private long _lastAdsHoldDiagTicks = 0;
        private int _adsHoldDiagCount = 0;

        public CameraData Data   { get; private set; }
        public bool       IsValid => _activeCamIdx >= 0 && Data.IsValid;

        public Camera(Mem mem) { _mem = mem; }

        // ── Acquire: heap scan, build pool ────────────────────────────────────

        public void Acquire()
        {
            _camPool.Clear();
            _activeCamIdx = -1;
            _needsSelectionScan = true;

            if (_mem.Handle == IntPtr.Zero) return;

            // Data-pattern scan — no vtable assumption.
            // The FPS camera and the overhead/spectator cameras are DIFFERENT C++
            // classes with DIFFERENT vtables. Scanning for one vtable only finds
            // one class. Scanning by struct shape (YFocal in [1.5,3.0] + unit vectors)
            // finds ALL camera instances regardless of class. Selection prefers continuity
            // with the known-good camera and uses player proximity only as a secondary cue.
            IntPtr addr  = IntPtr.Zero;
            long   limit = 0x800000000000L;
            int    mbiSz = Marshal.SizeOf<MEMORY_BASIC_INFORMATION>();

            Console.WriteLine($"  [CAM] Heap scan (data-pattern, no vtable)...");

            while ((long)addr < limit)
            {
                int q = VirtualQueryEx(_mem.Handle, addr, out var mbi, (uint)mbiSz);
                if (q == 0) break;

                long regionBase = (long)mbi.BaseAddress;
                long regionSize = (long)mbi.RegionSize;
                if (regionSize <= 0) break;

                bool readable = (mbi.State == MEM_COMMIT);
                bool writable = (mbi.Protect & (PAGE_READWRITE | PAGE_EXEC_RW |
                                                PAGE_WRITECOPY | PAGE_EXEC_WC)) != 0;
                bool guarded  = (mbi.Protect & PAGE_GUARD)    != 0;
                bool noaccess = (mbi.Protect & PAGE_NOACCESS) != 0;

                if (readable && writable && !guarded && !noaccess && regionSize >= 8)
                {
                    int  chunkSize = 0x80000;
                    var  buf       = new byte[chunkSize];

                    for (long off = 0; off < regionSize; off += chunkSize)
                    {
                        int toRead = (int)Math.Min(chunkSize, regionSize - off);
                        if (!_mem.ReadBytes(regionBase + off, buf, toRead)) continue;

                        // Camera struct layout (offsets from struct base):
                        //   +0x08  RightX +0x0C  RightY +0x10  RightZ (unit vector)
                        //   +0x18  UpX    +0x1C  UpY    +0x20  UpZ    (unit vector)
                        //   +0x28  FwdX   +0x2C  FwdY   +0x30  FwdZ   (unit vector)
                        //   +0x38  PosX   +0x3C  PosY   +0x40  PosZ
                        //   +0x9C  YFocal (float32, little-endian, MSB at +0x9F)
                        //
                        // FIX: Previous scan checked only YFocal MSB (0x3F or 0x40),
                        // then RPM-validated each hit. Including 0x3F meant YFocal=1.000
                        // (0x3F800000) triggered RPM on millions of heap positions →
                        // 96,000+ false positives → game froze during scan.
                        //
                        // New approach: validate the complete candidate through the same
                        // CameraData.IsStructurallyValid() routine used by live reads.
                        // Zero RPM calls are required for candidate discovery.
                        if (toRead < CAMERA_SCAN_MIN_BYTES) continue;
                        for (int i = 0; i + CAMERA_SCAN_MIN_BYTES <= toRead; i += 4)
                        {
                            // Proven camera identity field: +0x320 repeatedly reads as the
                            // display aspect (1280/705 = 1.81560) on the live render camera,
                            // including through ADS/scope transitions. Random heap transforms
                            // do not carry this same value. Use it only as a discovery gate;
                            // it is NOT used as XFocal by WorldToScreen.
                            float aspect320 = BitConverter.ToSingle(buf, i + 0x320);
                            if (!float.IsFinite(aspect320) ||
                                MathF.Abs(aspect320 - CAMERA_ASPECT_FIELD) > CAMERA_ASPECT_TOLERANCE)
                                continue;

                            float yFocal = BitConverter.ToSingle(buf, i + 0x9C);

                            // Recovery-only discriminator: the known live render camera
                            // has YFocal >= 1.8 on this build. Keep the upper bound in the
                            // shared structural validator so high-zoom states remain valid.
                            // This rejects the large class of 0.3..1.8 false-positive
                            // transforms seen in heap memory without altering live-camera
                            // validity.
                            if (yFocal < 1.8f) continue;

                            float rightX = BitConverter.ToSingle(buf, i + 0x08);
                            float rightY = BitConverter.ToSingle(buf, i + 0x0C);
                            float rightZ = BitConverter.ToSingle(buf, i + 0x10);

                            float upX = BitConverter.ToSingle(buf, i + 0x18);
                            float upY = BitConverter.ToSingle(buf, i + 0x1C);
                            float upZ = BitConverter.ToSingle(buf, i + 0x20);

                            float fwdX = BitConverter.ToSingle(buf, i + 0x28);
                            float fwdY = BitConverter.ToSingle(buf, i + 0x2C);
                            float fwdZ = BitConverter.ToSingle(buf, i + 0x30);

                            float posX = BitConverter.ToSingle(buf, i + 0x38);
                            float posY = BitConverter.ToSingle(buf, i + 0x3C);
                            float posZ = BitConverter.ToSingle(buf, i + 0x40);

                            if (!CameraData.IsStructurallyValid(
                                    posX, posY, posZ, yFocal,
                                    fwdX, fwdY, fwdZ,
                                    upX, upY, upZ,
                                    rightX, rightY, rightZ))
                                continue;

                            // All structural checks passed — genuine camera candidate
                            long candidate = regionBase + off + i;
                            if (!_camPool.Contains(candidate))
                            {
                                _camPool.Add(candidate);
                                Console.WriteLine(
                                    $"  [CAM] Pool #{_camPool.Count}: 0x{candidate:X}" +
                                    $"  YFocal={yFocal:F3}" +
                                    $"  +320={aspect320:F5}" +
                                    $"  fwd=({fwdX:F2},{fwdY:F2},{fwdZ:F2})" +
                                    $"  pos=({posX:F0},{posY:F0},{posZ:F0})");
                            }
                        }
                    }
                }

                addr = (IntPtr)(regionBase + regionSize);
            }

            if (_camPool.Count > 0)
            {
                _activeCamIdx = 0;
                _needsSelectionScan = true;
                Console.WriteLine(
                    $"  [CAM] Scan complete. Pool has {_camPool.Count} camera(s). " +
                    $"Nearest selection deferred to the next update.");
            }
            else
            {
                Console.WriteLine("  [CAM] Scan found no valid cameras — vtable offset may have changed");
            }
        }

        private static bool ContainsPointer(List<long> pool, long ptr)
        {
            if (ptr == 0) return false;
            for (int i = 0; i < pool.Count; i++)
            {
                if (pool[i] == ptr) return true;
            }
            return false;
        }

        // ── Per-frame update ───────────────────────────────────────────────────
        //
        // Normal updates read only the already-selected camera. A full pool scan is
        // reserved for initial selection or recovery.

        public bool Update(
            float playerX = 0f,
            float playerY = 0f,
            float playerZ = 0f,
            bool adsDown = false)
        {
            bool havePlayerPos = (playerX != 0f || playerY != 0f || playerZ != 0f);
            bool adsTransition = false;
            string adsEvent = null;

            if (!_adsStateInitialized)
            {
                _adsStateInitialized = true;
                _adsDown = adsDown;
            }
            else if (adsDown != _adsDown)
            {
                _adsDown = adsDown;
                adsTransition = true;
                adsEvent = adsDown ? "ADS_DOWN" : "ADS_UP";

                LogScopeCameraState(
                    adsEvent + "_PRE",
                    adsDown,
                    _activeCamIdx,
                    _activeCamIdx >= 0 && _activeCamIdx < _camPool.Count
                        ? _camPool[_activeCamIdx]
                        : 0,
                    Data,
                    playerX, playerY, playerZ,
                    havePlayerPos);
            }

            if (_camPool.Count == 0)
            {
                long nowTicks = System.Diagnostics.Stopwatch.GetTimestamp();
                if (_nextSelectionRetryTicks != 0 && nowTicks < _nextSelectionRetryTicks)
                    return false;

                Acquire();
                if (_camPool.Count == 0)
                {
                    _nextSelectionRetryTicks = nowTicks +
                        (long)(0.5 * System.Diagnostics.Stopwatch.Frequency);
                    return false;
                }

                _nextSelectionRetryTicks = 0;
            }

            // The pool is scanned only when a camera needs to be selected/recovered.
            // Normal frames read the already-selected camera only.
            if (_needsSelectionScan || _activeCamIdx < 0 || _activeCamIdx >= _camPool.Count)
            {
                long nowTicks = System.Diagnostics.Stopwatch.GetTimestamp();
                if (_nextSelectionRetryTicks != 0 && nowTicks < _nextSelectionRetryTicks)
                    return false;

                int bestIdx = -1;
                CameraData bestData = default;
                float bestScore = float.MaxValue;
                bool haveContinuity = Data.IsValid && _lastGoodCamPtr != 0;
                long lastGoodPtr = _lastGoodCamPtr;

                // Tier 1: preserve the exact known-good camera pointer whenever the
                // freshly rebuilt pool still contains it. This is stronger than any
                // geometric/proximity score and prevents a recovery scan from silently
                // replacing the render camera with another camera-shaped allocation.
                if (haveContinuity)
                {
                    for (int i = 0; i < _camPool.Count; i++)
                    {
                        if (_camPool[i] != lastGoodPtr) continue;

                        CameraData knownGood = ReadCam(lastGoodPtr);
                        if (knownGood.IsValid && knownGood.YFocal >= 1.8f)
                        {
                            bestIdx = i;
                            bestData = knownGood;
                        }
                        break;
                    }
                }

                // Tier 2: no exact continuity pointer was available. Only then do we
                // consider another candidate. The scan has already applied the proven
                // +0x320 camera-identity gate and full structural validation; player
                // distance is used only to rank the remaining candidates.
                if (bestIdx < 0)
                {
                    // Without an exact continuity pointer, player position is still
                    // required so distance can rank the remaining identity-filtered
                    // candidates. It is intentionally NOT a hard acceptance gate.
                    if (!havePlayerPos)
                    {
                        _nextSelectionRetryTicks = nowTicks +
                            (long)(0.5 * System.Diagnostics.Stopwatch.Frequency);
                        Console.WriteLine(
                            "  [CAM] Selection deferred: no player position available for ranking");
                        return false;
                    }

                    for (int i = 0; i < _camPool.Count; i++)
                    {
                        long candidatePtr = _camPool[i];
                        if (candidatePtr == lastGoodPtr) continue;

                        CameraData d = ReadCam(candidatePtr);
                        if (!d.IsValid) continue;

                        // Keep the recovery-only low-YFocal discriminator at the final
                        // selection boundary as well as at heap discovery time.
                        if (d.YFocal < 1.8f) continue;

                        float playerDistance = 0f;
                        if (havePlayerPos)
                        {
                            float dx = d.PosX - playerX;
                            float dy = d.PosY - playerY;
                            float dz = d.PosZ - playerZ;
                            playerDistance = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
                        }

                        // The +0x320 identity gate has already removed the known
                        // false-positive class. Use distance only as the fallback rank.
                        float score = playerDistance;

                        if (haveContinuity)
                        {
                            float dx = d.PosX - Data.PosX;
                            float dy = d.PosY - Data.PosY;
                            float dz = d.PosZ - Data.PosZ;
                            float positionDelta = MathF.Sqrt(dx * dx + dy * dy + dz * dz);

                            float basisDelta =
                                MathF.Abs(d.RightX - Data.RightX) +
                                MathF.Abs(d.RightY - Data.RightY) +
                                MathF.Abs(d.RightZ - Data.RightZ) +
                                MathF.Abs(d.UpX - Data.UpX) +
                                MathF.Abs(d.UpY - Data.UpY) +
                                MathF.Abs(d.UpZ - Data.UpZ) +
                                MathF.Abs(d.FwdX - Data.FwdX) +
                                MathF.Abs(d.FwdY - Data.FwdY) +
                                MathF.Abs(d.FwdZ - Data.FwdZ);

                            float focalDelta = MathF.Abs(d.YFocal - Data.YFocal);
                            score = positionDelta + basisDelta * 100f + focalDelta * 100f +
                                    playerDistance * 0.001f;
                        }

                        if (bestIdx < 0 || score < bestScore)
                        {
                            bestScore = score;
                            bestIdx = i;
                            bestData = d;
                        }
                    }
                }

                if (bestIdx < 0)
                {
                    _nextSelectionRetryTicks = nowTicks +
                        (long)(0.5 * System.Diagnostics.Stopwatch.Frequency);
                    Console.WriteLine(
                        $"  [CAM] No confident camera candidate; retrying in 500 ms (pool={_camPool.Count})");
                    return false;
                }

                _nextSelectionRetryTicks = 0;
                _activeCamIdx = bestIdx;
                Data = bestData;
                _lastGoodCamPtr = _camPool[bestIdx];
                _needsSelectionScan = false;
                RuntimeDiagnostics.Camera(_camPool[bestIdx], Data, true, "SELECTED");
                LogScopeCameraState(
                    adsTransition ? adsEvent + "_SELECTED" : "SELECTED",
                    adsDown,
                    _activeCamIdx,
                    _camPool[bestIdx],
                    Data,
                    playerX, playerY, playerZ,
                    havePlayerPos);
            }
            else
            {
                CameraData cur = ReadCam(_camPool[_activeCamIdx]);
                if (!cur.IsValid)
                {
                    _needsSelectionScan = true;
                    RuntimeDiagnostics.Camera(_camPool[_activeCamIdx], cur, false, "ACTIVE_INVALID");
                    _activeCamIdx = -1;
                    Data = default;
                    _lastGoodCamPtr = 0;
                    _nextSelectionRetryTicks = 0;
                    return false;
                }

                Data = cur;
                _lastGoodCamPtr = _camPool[_activeCamIdx];
                RuntimeDiagnostics.Camera(_camPool[_activeCamIdx], Data, true, "OK");

                // IMPORTANT: player-to-camera distance is telemetry only. A legitimate
                // scoped camera can be far from the avatar. Keep the structurally-valid
                // active camera until an actual memory/basis validation failure occurs.
                }

            if (adsTransition)
            {
                LogScopeCameraState(
                    adsEvent + "_POST",
                    adsDown,
                    _activeCamIdx,
                    _activeCamIdx >= 0 && _activeCamIdx < _camPool.Count
                        ? _camPool[_activeCamIdx]
                        : 0,
                    Data,
                    playerX, playerY, playerZ,
                    havePlayerPos);
            }

            if (adsDown)
                MaybeLogAdsHoldProjectionState(playerX, playerY, playerZ, havePlayerPos);
            else
            {
                _adsHoldDiagCount = 0;
                _lastAdsHoldDiagTicks = 0;
            }

            return true;
        }

        // Diagnostic-only camera snapshot logger for ADS/scope transitions and
        // recovery/selection events. It intentionally does not alter any state.
        private void LogScopeCameraState(
            string eventName,
            bool adsDown,
            int poolIndex,
            long ptr,
            in CameraData cam,
            float playerX,
            float playerY,
            float playerZ,
            bool havePlayerPos,
            float? distanceOverride = null)
        {
            float distanceMeters = -1f;
            if (havePlayerPos && cam.IsValid)
            {
                float dx = cam.PosX - playerX;
                float dy = cam.PosY - playerY;
                float dz = cam.PosZ - playerZ;
                distanceMeters = MathF.Sqrt(dx * dx + dy * dy + dz * dz) / 10f;
            }
            if (distanceOverride.HasValue)
                distanceMeters = distanceOverride.Value / 10f;

            float aspect = 1280f / 705f;
            float fallbackAr = cam.YFocal / aspect;
            float rawAr = (cam.XFocal > 0.3f && cam.XFocal < 10f)
                ? cam.YFocal / cam.XFocal
                : float.NaN;
            bool scanCompatible = cam.YFocal >= 1.8f && cam.YFocal <= 6.0f;

            BasisQuality(cam,
                out float rMag, out float uMag, out float fMag,
                out float ru, out float rf, out float uf,
                out float crossErr);

            byte ortho = ReadOrthoFlag(ptr, out bool orthoReadOk);
            float aspectField = ReadFloatAt(ptr + 0x320, out bool aspectReadOk);

            Console.WriteLine(
                $"  [CAM][SCOPE] event={eventName} ads={(adsDown ? 1 : 0)} ptr=0x{ptr:X} poolIdx={poolIndex} " +
                $"YF={cam.YFocal:F5} XF={cam.XFocal:F5} aspect=1.81560(+0x320={(aspectReadOk ? aspectField.ToString("F5") : "READFAIL")}) " +
                $"arFallback={fallbackAr:F5} arRaw={(float.IsFinite(rawAr) ? rawAr.ToString("F5") : "INVALID")} " +
                $"ortho={(orthoReadOk ? ortho.ToString() : "READFAIL")} scanYFocalOK={(scanCompatible ? 1 : 0)} " +
                $"basis=R{rMag:F4}/U{uMag:F4}/F{fMag:F4},dots=RU{ru:F4}/RF{rf:F4}/UF{uf:F4},crossErr={crossErr:F5} " +
                $"distM={(distanceMeters >= 0f ? distanceMeters.ToString("F3") : "N/A")}");

            if (ptr != 0)
                LogFocusedFocalWindow(ptr, cam.YFocal, eventName);
        }

        private void MaybeLogAdsHoldProjectionState(float playerX, float playerY, float playerZ, bool havePlayerPos)
        {
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            double elapsedMs = _lastAdsHoldDiagTicks == 0
                ? double.MaxValue
                : (now - _lastAdsHoldDiagTicks) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

            if (_lastAdsHoldDiagTicks != 0 && elapsedMs < 500.0)
                return;

            _lastAdsHoldDiagTicks = now;
            _adsHoldDiagCount++;

            long ptr = (_activeCamIdx >= 0 && _activeCamIdx < _camPool.Count) ? _camPool[_activeCamIdx] : 0;
            if (ptr == 0) return;

            float distM = -1f;
            if (havePlayerPos && Data.IsValid)
            {
                float dx = Data.PosX - playerX;
                float dy = Data.PosY - playerY;
                float dz = Data.PosZ - playerZ;
                distM = MathF.Sqrt(dx * dx + dy * dy + dz * dz) / 10f;
            }

            float aspect = 1280f / 705f;
            float arFallback = Data.YFocal / aspect;
            byte ortho = ReadOrthoFlag(ptr, out bool orthoOk);
            float aspectField = ReadFloatAt(ptr + 0x320, out bool aspectOk);

            Console.WriteLine(
                $"  [CAM][ADS-HOLD] n={_adsHoldDiagCount} ptr=0x{ptr:X} " +
                $"YF={Data.YFocal:F5} XF={Data.XFocal:F5} ortho={(orthoOk ? ortho.ToString() : "READFAIL")} " +
                $"+320={(aspectOk ? aspectField.ToString("F5") : "READFAIL")} arFallback={arFallback:F5} " +
                $"distM={(distM >= 0f ? distM.ToString("F3") : "N/A")} scanYFocalOK={(Data.YFocal >= 1.8f && Data.YFocal <= 6.0f ? 1 : 0)}");

            if (_adsHoldDiagCount <= 8)
                LogFocusedFocalWindow(ptr, Data.YFocal, "ADS_HOLD");
        }

        private void LogFocusedFocalWindow(long ptr, float yFocal, string eventName)
        {
            const long probeBase = 0x300;
            if (!_mem.ReadBytes(ptr + probeBase, _cameraFocalProbeBuffer, 0, _cameraFocalProbeBuffer.Length))
            {
                Console.WriteLine($"  [CAM][FOCAL-WINDOW] event={eventName} ptr=0x{ptr:X} read=FAIL");
                return;
            }

            var candidates = new List<string>();
            for (int o = 0; o + 4 <= _cameraFocalProbeBuffer.Length; o += 4)
            {
                float v = BitConverter.ToSingle(_cameraFocalProbeBuffer, o);
                if (!float.IsFinite(v) || v < 0.5f || v > 12.0f) continue;

                float ratio = yFocal / v;
                candidates.Add($"+0x{probeBase + o:X3}={v:F5}/YF={ratio:F5}");
            }

            Console.WriteLine(
                $"  [CAM][FOCAL-WINDOW] event={eventName} ptr=0x{ptr:X} " +
                $"YF={yFocal:F5} XF@360={Data.XFocal:F5} " +
                $"values=[{string.Join(",", candidates)}]");
        }

        private byte ReadOrthoFlag(long ptr, out bool ok)
        {
            ok = ptr != 0 && _mem.ReadBytes(ptr + 0x348, _cameraOrthoBuffer, 0, 1);
            return ok ? _cameraOrthoBuffer[0] : (byte)0;
        }

        private float ReadFloatAt(long address, out bool ok)
        {
            byte[] b = new byte[4];
            ok = _mem.ReadBytes(address, b, 0, 4);
            return ok ? BitConverter.ToSingle(b, 0) : float.NaN;
        }

        private static void BasisQuality(
            in CameraData c,
            out float rMag, out float uMag, out float fMag,
            out float ru, out float rf, out float uf,
            out float crossErr)
        {
            rMag = MathF.Sqrt(c.RightX*c.RightX + c.RightY*c.RightY + c.RightZ*c.RightZ);
            uMag = MathF.Sqrt(c.UpX*c.UpX + c.UpY*c.UpY + c.UpZ*c.UpZ);
            fMag = MathF.Sqrt(c.FwdX*c.FwdX + c.FwdY*c.FwdY + c.FwdZ*c.FwdZ);
            ru = c.RightX*c.UpX + c.RightY*c.UpY + c.RightZ*c.UpZ;
            rf = c.RightX*c.FwdX + c.RightY*c.FwdY + c.RightZ*c.FwdZ;
            uf = c.UpX*c.FwdX + c.UpY*c.FwdY + c.UpZ*c.FwdZ;

            float cx = c.UpY*c.FwdZ - c.UpZ*c.FwdY;
            float cy = c.UpZ*c.FwdX - c.UpX*c.FwdZ;
            float cz = c.UpX*c.FwdY - c.UpY*c.FwdX;
            float dx = cx - c.RightX;
            float dy = cy - c.RightY;
            float dz = cz - c.RightZ;
            crossErr = MathF.Sqrt(dx*dx + dy*dy + dz*dz);
        }

        // Camera data is refreshed exclusively by Update() on the read thread and
        // published to HackOverlay as part of the same render snapshot as entities.
        // There is intentionally no paint-thread RPM path.

        // ── Read all camera fields from a pointer into a CameraData struct ────

        private CameraData ReadCam(long ptr)
        {
            try
            {
                // +0x00..+0x9F contains all basis vectors, position, and YFocal.
                if (!_mem.ReadBytes(ptr, _cameraCoreBuffer, 0, _cameraCoreBuffer.Length))
                    return default;

                static float F(byte[] b, int o) => BitConverter.ToSingle(b, o);

                var cam = new CameraData
                {
                    RightX = F(_cameraCoreBuffer, 0x08),
                    RightY = F(_cameraCoreBuffer, 0x0C),
                    RightZ = F(_cameraCoreBuffer, 0x10),
                    UpX    = F(_cameraCoreBuffer, 0x18),
                    UpY    = F(_cameraCoreBuffer, 0x1C),
                    UpZ    = F(_cameraCoreBuffer, 0x20),
                    FwdX   = F(_cameraCoreBuffer, 0x28),
                    FwdY   = F(_cameraCoreBuffer, 0x2C),
                    FwdZ   = F(_cameraCoreBuffer, 0x30),
                    PosX   = F(_cameraCoreBuffer, 0x38),
                    PosY   = F(_cameraCoreBuffer, 0x3C),
                    PosZ   = F(_cameraCoreBuffer, 0x40),
                    YFocal = F(_cameraCoreBuffer, 0x9C),
                };

                // XFocal is needed only when it is valid; W2S has a proven fallback.
                if (_mem.ReadBytes(ptr + Offsets.CAM_XFOCAL, _cameraXFocalBuffer, 0, 4))
                    cam.XFocal = BitConverter.ToSingle(_cameraXFocalBuffer, 0);
                return cam;
            }
            catch
            {
                return default;
            }
        }

        public void Invalidate()
        {
            _camPool.Clear();
            _activeCamIdx = -1;
            _needsSelectionScan = true;
            _lastGoodCamPtr = 0;
        }
    }
}
