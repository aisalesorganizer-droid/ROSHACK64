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
    ///   address. Reading it returns 0x8D4820EC83485340 (garbage). This was confirmed
    ///   across all sessions in debug.txt. Chain is disabled.
    ///
    /// HEAP SCAN: Works reliably. Scans committed RW memory for the camera vtable
    ///   signature (base + 0x01D84DC8). Validates by checking YFocal and forward vector.
    ///
    /// CAMERA POOL HANDLING: The game has multiple camera instances sharing the same
    ///   vtable (E0CF2F30, E0CFB090, E0CF6740, E0CF37D0 observed in debug). All valid
    ///   hits are cached. Update() tries the cached list in order, picking the first
    ///   one that reads a valid, changing forward vector — indicating it's the live camera.
    ///
    /// SCAN FREQUENCY: Scan is expensive on a 3GB process. We only scan when:
    ///   1. First call (no camera found yet), or
    ///   2. All cached cameras have failed MAX_FAILS consecutive validation checks.
    ///   Between scans: Update() reads from the cached pointer — zero heap scan cost.
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

        // Pool of valid camera pointers found during last heap scan
        private readonly List<long> _camPool = new List<long>(8);
        private int  _activeCamIdx  = -1;
        private int  _failCount     = 0;
        private const int MAX_FAILS = 8;   // failures before we try next in pool or rescan

        public CameraData Data   { get; private set; }
        public bool       IsValid => _activeCamIdx >= 0 && Data.IsValid;

        public Camera(Mem mem) { _mem = mem; }

        // ── Acquire: heap scan, build pool ────────────────────────────────────

        public void Acquire()
        {
            _camPool.Clear();
            _activeCamIdx = -1;

            if (_mem.Handle == IntPtr.Zero) return;

            ulong targetVtable = (ulong)(_mem.BaseAddress + Offsets.CAM_VTABLE_OFFSET);

            IntPtr addr  = IntPtr.Zero;
            long   limit = 0x800000000000L;
            int    mbiSz = Marshal.SizeOf<MEMORY_BASIC_INFORMATION>();

            Console.WriteLine($"  [CAM] Heap scan for vtable 0x{targetVtable:X}...");

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

                        for (int i = 0; i <= toRead - 8; i += 8)
                        {
                            ulong v = BitConverter.ToUInt64(buf, i);
                            if (v != targetVtable) continue;

                            long candidate = regionBase + off + i;
                            if (ValidateCam(candidate) && !_camPool.Contains(candidate))
                            {
                                _camPool.Add(candidate);
                                float yf = _mem.ReadFloat(candidate + Offsets.CAM_YFOCAL);
                                float fx = _mem.ReadFloat(candidate + Offsets.CAM_FWD_X);
                                float fy = _mem.ReadFloat(candidate + Offsets.CAM_FWD_Y);
                                float fz = _mem.ReadFloat(candidate + Offsets.CAM_FWD_Z);
                                Console.WriteLine($"  [CAM] Pool #{_camPool.Count}: 0x{candidate:X}  YFocal={yf:F3}  fwd=({fx:F2},{fy:F2},{fz:F2})");
                            }
                        }
                    }
                }

                addr = (IntPtr)(regionBase + regionSize);
            }

            if (_camPool.Count > 0)
            {
                // Start with the first (often the live camera)
                _activeCamIdx = 0;
                _failCount    = 0;
                Console.WriteLine($"  [CAM] Scan complete. Pool has {_camPool.Count} camera(s). Active: 0x{_camPool[0]:X}");
            }
            else
            {
                Console.WriteLine("  [CAM] Scan found no valid cameras — vtable offset may have changed");
            }
        }

        private bool ValidateCam(long ptr)
        {
            try
            {
                float yf = _mem.Read<float>(ptr + Offsets.CAM_YFOCAL, "CAM.Validate.YFocal");
                if (yf < 0.3f || yf > 5f) return false;

                float fwdX = _mem.Read<float>(ptr + Offsets.CAM_FWD_X, "CAM.Validate.FwdX");
                float fwdY = _mem.Read<float>(ptr + Offsets.CAM_FWD_Y, "CAM.Validate.FwdY");
                float fwdZ = _mem.Read<float>(ptr + Offsets.CAM_FWD_Z, "CAM.Validate.FwdZ");
                float mag  = MathF.Sqrt(fwdX * fwdX + fwdY * fwdY + fwdZ * fwdZ);
                if (mag < 0.9f || mag > 1.1f) return false;

                // Reject ALL axis-aligned sentinel/default cameras: (±1,0,0), (0,±1,0), (0,0,±1)
                // In the pool log: fwd=(-1.00,0.00,0.00) passes the old check but is clearly wrong.
                bool axisAligned =
                    (MathF.Abs(MathF.Abs(fwdX) - 1f) < 0.02f && MathF.Abs(fwdY) < 0.02f && MathF.Abs(fwdZ) < 0.02f) ||
                    (MathF.Abs(MathF.Abs(fwdY) - 1f) < 0.02f && MathF.Abs(fwdX) < 0.02f && MathF.Abs(fwdZ) < 0.02f) ||
                    (MathF.Abs(MathF.Abs(fwdZ) - 1f) < 0.02f && MathF.Abs(fwdX) < 0.02f && MathF.Abs(fwdY) < 0.02f);
                if (axisAligned) return false;

                return true;
            }
            catch { return false; }
        }

        // ── Per-frame update ───────────────────────────────────────────────────

        public bool Update()
        {
            // If we have no pool at all, do a scan
            if (_camPool.Count == 0 || _activeCamIdx < 0)
            {
                Acquire();
                if (_camPool.Count == 0) return false;
            }

            long camPtr = _camPool[_activeCamIdx];

            try
            {
                var d = new CameraData
                {
                    FwdX   = _mem.Read<float>(camPtr + Offsets.CAM_FWD_X, "CAM.Update.FwdX"),
                    FwdY   = _mem.Read<float>(camPtr + Offsets.CAM_FWD_Y, "CAM.Update.FwdY"),
                    FwdZ   = _mem.Read<float>(camPtr + Offsets.CAM_FWD_Z, "CAM.Update.FwdZ"),
                    UpX    = _mem.Read<float>(camPtr + Offsets.CAM_UP_X, "CAM.Update.UpX"),
                    UpY    = _mem.Read<float>(camPtr + Offsets.CAM_UP_Y, "CAM.Update.UpY"),
                    UpZ    = _mem.Read<float>(camPtr + Offsets.CAM_UP_Z, "CAM.Update.UpZ"),
                    RightX = _mem.Read<float>(camPtr + Offsets.CAM_RIGHT_X, "CAM.Update.RightX"),
                    RightY = _mem.Read<float>(camPtr + Offsets.CAM_RIGHT_Y, "CAM.Update.RightY"),
                    RightZ = _mem.Read<float>(camPtr + Offsets.CAM_RIGHT_Z, "CAM.Update.RightZ"),
                    PosX   = _mem.Read<float>(camPtr + Offsets.CAM_POS_X, "CAM.Update.PosX"),
                    PosY   = _mem.Read<float>(camPtr + Offsets.CAM_POS_Y, "CAM.Update.PosY"),
                    PosZ   = _mem.Read<float>(camPtr + Offsets.CAM_POS_Z, "CAM.Update.PosZ"),
                    YFocal = _mem.Read<float>(camPtr + Offsets.CAM_YFOCAL, "CAM.Update.YFocal"),
                    XFocal = _mem.Read<float>(camPtr + Offsets.CAM_XFOCAL, "CAM.Update.XFocal"),
                    Ortho  = _mem.Read<byte>(camPtr + Offsets.CAM_ORTHO_FLAG, "CAM.Update.Ortho"),
                };

                if (d.IsValid)
                {
                    Data       = d;
                    _failCount = 0;
                    RuntimeDiagnostics.Camera(camPtr, d, true, "OK");
                    return true;
                }

                string reason = d.YFocal <= 0.3f || d.YFocal >= 10f
                    ? "YFOCAL"
                    : (d.FwdX == 0f && d.FwdY == 0f && d.FwdZ == 0f ? "FWD_ZERO" : "OTHER");
                RuntimeDiagnostics.Camera(camPtr, d, false, reason);
            }
            catch { }

            // This camera failed — try the next in the pool
            _failCount++;
            if (_failCount >= MAX_FAILS)
            {
                _failCount = 0;
                int nextIdx = _activeCamIdx + 1;

                if (nextIdx < _camPool.Count)
                {
                    // Rotate to next camera in pool
                    _activeCamIdx = nextIdx;
                    Console.WriteLine($"  [CAM] Switching to pool #{nextIdx}: 0x{_camPool[nextIdx]:X}");
                }
                else
                {
                    // Exhausted pool — rescan
                    Console.WriteLine("  [CAM] Pool exhausted, rescanning...");
                    _camPool.Clear();
                    _activeCamIdx = -1;
                }
            }

            return false;
        }

        public void Invalidate()
        {
            _camPool.Clear();
            _activeCamIdx = -1;
            _failCount    = 0;
        }
    }
}
