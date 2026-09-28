using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

static class Offsets
{
    public const long EM_STATIC_PTR = 0x031F53D8;
    public const long EM_ALL_ENTITIES = 0x10E0;
    public const long EM_CONTROLLED_ENT = 0x10F8;

    public const long NODE_LEFT = 0x00;
    public const long NODE_RIGHT = 0x10;
    public const long NODE_ENTITY_ID = 0x20;
    public const long NODE_ENTITY_PTR = 0x28;

    public const long ENT_OB_TYPE = 0x08;
    public const long ENT_ENTITY_ID = 0x18;
    public const long ENT_X = 0x1C;
    public const long ENT_Y = 0x20;
    public const long ENT_Z = 0x24;

    public const long PY_TYPE_NAME = 0x18;

    public const long CAM_VTABLE_OFFSET = 0x01D84DC8;
    public const long CAM_FWD_X = 0x08;
    public const long CAM_FWD_Y = 0x0C;
    public const long CAM_FWD_Z = 0x10;
    public const long CAM_UP_X = 0x18;
    public const long CAM_UP_Y = 0x1C;
    public const long CAM_UP_Z = 0x20;
    public const long CAM_RIGHT_X = 0x28;
    public const long CAM_RIGHT_Y = 0x2C;
    public const long CAM_RIGHT_Z = 0x30;
    public const long CAM_POS_X = 0x38;
    public const long CAM_POS_Y = 0x3C;
    public const long CAM_POS_Z = 0x40;
    public const long CAM_YFOCAL = 0x9C;
    public const long CAM_ORTHO = 0x348;
    public const long CAM_XFOCAL = 0x360;
}

sealed class Reader : IDisposable
{
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr h);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool ReadProcessMemory(IntPtr h, IntPtr addr, byte[] buffer, int size, out IntPtr read);

    [DllImport("kernel32.dll")]
    static extern int VirtualQueryEx(IntPtr hProcess, IntPtr lpAddress, out MBI lpBuffer, uint length);

    [StructLayout(LayoutKind.Sequential)]
    struct MBI
    {
        public IntPtr BaseAddress;
        public IntPtr AllocationBase;
        public uint AllocationProtect;
        public IntPtr RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
    }

    const uint PROCESS_VM_READ = 0x0010;
    const uint PROCESS_QUERY_INFORMATION = 0x0400;
    const uint MEM_COMMIT = 0x1000;
    const uint PAGE_GUARD = 0x100;
    const uint PAGE_NOACCESS = 0x01;
    const uint PAGE_READWRITE = 0x04;
    const uint PAGE_WRITECOPY = 0x08;
    const uint PAGE_EXECUTE_READWRITE = 0x40;
    const uint PAGE_EXECUTE_WRITECOPY = 0x80;

    readonly IntPtr _handle;
    public long Base { get; }
    public int Pid { get; }

    public Reader(Process p)
    {
        Pid = p.Id;
        Base = p.MainModule?.BaseAddress.ToInt64() ?? throw new InvalidOperationException("Main module unavailable.");
        _handle = OpenProcess(PROCESS_VM_READ | PROCESS_QUERY_INFORMATION, false, p.Id);
        if (_handle == IntPtr.Zero) throw new InvalidOperationException($"OpenProcess failed. Win32={Marshal.GetLastWin32Error()}");
    }

    public bool TryRead<T>(long address, out T value) where T : unmanaged
    {
        int size = Marshal.SizeOf<T>();
        var b = new byte[size];
        if (!ReadProcessMemory(_handle, (IntPtr)address, b, size, out var got) || got.ToInt64() != size)
        {
            value = default;
            return false;
        }
        value = MemoryMarshal.Read<T>(b);
        return true;
    }

    public bool TryReadString(long address, out string value, int maxLen = 64)
    {
        var b = new byte[maxLen];
        if (!ReadProcessMemory(_handle, (IntPtr)address, b, b.Length, out var got) || got.ToInt64() <= 0)
        {
            value = "";
            return false;
        }
        int n = 0;
        int limit = (int)Math.Min(got.ToInt64(), b.Length);
        while (n < limit && b[n] != 0) n++;
        value = Encoding.ASCII.GetString(b, 0, n);
        return true;
    }

    public IEnumerable<(long Base, long Size, uint Protect)> ReadableWritableRegions()
    {
        long addr = 0;
        long limit = 0x800000000000L;
        int mbiSize = Marshal.SizeOf<MBI>();
        while (addr < limit)
        {
            int ok = VirtualQueryEx(_handle, (IntPtr)addr, out var mbi, (uint)mbiSize);
            if (ok == 0) yield break;
            long regionBase = mbi.BaseAddress.ToInt64();
            long regionSize = mbi.RegionSize.ToInt64();
            if (regionSize <= 0) yield break;
            bool committed = mbi.State == MEM_COMMIT;
            bool guarded = (mbi.Protect & PAGE_GUARD) != 0;
            bool noAccess = (mbi.Protect & PAGE_NOACCESS) != 0;
            bool writable = (mbi.Protect & (PAGE_READWRITE | PAGE_WRITECOPY | PAGE_EXECUTE_READWRITE | PAGE_EXECUTE_WRITECOPY)) != 0;
            if (committed && writable && !guarded && !noAccess)
                yield return (regionBase, regionSize, mbi.Protect);
            addr = regionBase + regionSize;
        }
    }

    public bool TryReadBytes(long address, byte[] buffer)
    {
        return ReadProcessMemory(_handle, (IntPtr)address, buffer, buffer.Length, out var got) && got.ToInt64() == buffer.Length;
    }

    public void Dispose()
    {
        if (_handle != IntPtr.Zero) CloseHandle(_handle);
    }
}

struct CameraSample
{
    public long Ptr;
    public float FwdX, FwdY, FwdZ;
    public float UpX, UpY, UpZ;
    public float RightX, RightY, RightZ;
    public float PosX, PosY, PosZ;
    public float YFocal, XFocal;
    public byte Ortho;
}

class Program
{
    static readonly double MaxEntityCoord = 50000.0;

    static bool ValidPtr(long p) => p > 0x10000 && p < 0x7FFFFFFFFFFF;
    static bool ValidCoord(float x) => float.IsFinite(x) && x > -MaxEntityCoord && x < MaxEntityCoord;

    static bool ReadCamera(Reader r, long p, out CameraSample c)
    {
        c = new CameraSample { Ptr = p };
        if (!r.TryRead(p + Offsets.CAM_FWD_X, out c.FwdX) ||
            !r.TryRead(p + Offsets.CAM_FWD_Y, out c.FwdY) ||
            !r.TryRead(p + Offsets.CAM_FWD_Z, out c.FwdZ) ||
            !r.TryRead(p + Offsets.CAM_UP_X, out c.UpX) ||
            !r.TryRead(p + Offsets.CAM_UP_Y, out c.UpY) ||
            !r.TryRead(p + Offsets.CAM_UP_Z, out c.UpZ) ||
            !r.TryRead(p + Offsets.CAM_RIGHT_X, out c.RightX) ||
            !r.TryRead(p + Offsets.CAM_RIGHT_Y, out c.RightY) ||
            !r.TryRead(p + Offsets.CAM_RIGHT_Z, out c.RightZ) ||
            !r.TryRead(p + Offsets.CAM_POS_X, out c.PosX) ||
            !r.TryRead(p + Offsets.CAM_POS_Y, out c.PosY) ||
            !r.TryRead(p + Offsets.CAM_POS_Z, out c.PosZ) ||
            !r.TryRead(p + Offsets.CAM_YFOCAL, out c.YFocal) ||
            !r.TryRead(p + Offsets.CAM_XFOCAL, out c.XFocal) ||
            !r.TryRead(p + Offsets.CAM_ORTHO, out c.Ortho))
            return false;

        float mag = MathF.Sqrt(c.FwdX*c.FwdX + c.FwdY*c.FwdY + c.FwdZ*c.FwdZ);
        return float.IsFinite(mag) && c.YFocal > 0.3f && c.YFocal < 5f && mag > 0.9f && mag < 1.1f;
    }

    static double Dist(float ax, float ay, float az, CameraSample c)
    {
        double dx = ax - c.PosX, dy = ay - c.PosY, dz = az - c.PosZ;
        return Math.Sqrt(dx*dx + dy*dy + dz*dz);
    }

    static void Main()
    {
        Console.WriteLine("============================================================");
        Console.WriteLine(" ROS64 READ-ONLY MEMORY VERIFIER");
        Console.WriteLine(" No WriteProcessMemory / no memory modification");
        Console.WriteLine("============================================================\n");

        var procs = Process.GetProcessesByName("ros64");
        if (procs.Length == 0)
        {
            Console.WriteLine("[FAIL] ros64.exe is not running.");
            Console.WriteLine("Start the game session first, then run this verifier.");
            return;
        }

        using var r = new Reader(procs[0]);
        Console.WriteLine($"[OK] PID          = {r.Pid}");
        Console.WriteLine($"[OK] Module base  = 0x{r.Base:X}");
        Console.WriteLine();

        long emAnchor = r.Base + Offsets.EM_STATIC_PTR;
        if (!r.TryRead(emAnchor, out long em) || !ValidPtr(em))
        {
            Console.WriteLine($"[FAIL] EM anchor 0x{emAnchor:X} -> 0x{em:X}");
            return;
        }
        Console.WriteLine($"[EM] anchor=0x{emAnchor:X} -> em=0x{em:X}");

        r.TryRead(em + Offsets.EM_ALL_ENTITIES, out long sentinel);
        r.TryRead(em + Offsets.EM_ALL_ENTITIES + 0x08, out long size);
        long root = 0;
        if (ValidPtr(sentinel)) r.TryRead(sentinel + 0x08, out root);
        Console.WriteLine($"[TREE] sentinel=0x{sentinel:X} size={size} root=0x{root:X}");
        Console.WriteLine();

        Console.WriteLine("[ENTITY CHECK] First 32 nodes from EM+0x10E0");
        Console.WriteLine("#   node             ep               id       ob_type          tp_name              XYZ                         dist(cam)");
        Console.WriteLine("----------------------------------------------------------------------------------------------------------------");

        CameraSample? cam = null;
        foreach (var region in r.ReadableWritableRegions())
        {
            const int chunk = 0x80000;
            byte[] buf = new byte[chunk];
            long end = region.Base + region.Size;
            for (long off = 0; off < region.Size; off += chunk)
            {
                int len = (int)Math.Min(chunk, region.Size - off);
                Array.Clear(buf);
                if (!r.TryReadBytes(region.Base + off, buf[..len])) continue;
                for (int i = 0; i <= len - 8; i += 8)
                {
                    ulong v = BitConverter.ToUInt64(buf, i);
                    if (v != unchecked((ulong)(r.Base + Offsets.CAM_VTABLE_OFFSET))) continue;
                    long candidate = region.Base + off + i;
                    if (ReadCamera(r, candidate, out var cs)) { cam = cs; break; }
                }
                if (cam.HasValue) break;
            }
            if (cam.HasValue) break;
        }

        if (cam.HasValue)
        {
            var c = cam.Value;
            float mag = MathF.Sqrt(c.FwdX*c.FwdX + c.FwdY*c.FwdY + c.FwdZ*c.FwdZ);
            Console.WriteLine($"[CAM] ptr=0x{c.Ptr:X} fwd=({c.FwdX:F4},{c.FwdY:F4},{c.FwdZ:F4}) |mag|={mag:F4} pos=({c.PosX:F2},{c.PosY:F2},{c.PosZ:F2}) focal={c.YFocal:F5}/{c.XFocal:F5} ortho={c.Ortho}");
        }
        else Console.WriteLine("[CAM] No validated camera found by vtable scan.");

        if (!ValidPtr(root))
        {
            Console.WriteLine("[FAIL] Tree root is not a valid pointer.");
            return;
        }

        var stack = new Stack<long>();
        var seen = new HashSet<long>();
        stack.Push(root);
        int n = 0;
        while (stack.Count > 0 && n < 32)
        {
            long node = stack.Pop();
            if (!ValidPtr(node) || node == sentinel || !seen.Add(node)) continue;
            n++;

            r.TryRead(node + Offsets.NODE_LEFT, out long left);
            r.TryRead(node + Offsets.NODE_RIGHT, out long right);
            if (ValidPtr(left) && left != sentinel) stack.Push(left);
            if (ValidPtr(right) && right != sentinel) stack.Push(right);

            r.TryRead(node + Offsets.NODE_ENTITY_PTR, out long ep);
            r.TryRead(node + Offsets.NODE_ENTITY_ID, out uint id);

            long obType = 0;
            uint entId = 0;
            float x = float.NaN, y = float.NaN, z = float.NaN;
            string typeName = "<none>";
            if (ValidPtr(ep))
            {
                r.TryRead(ep + Offsets.ENT_OB_TYPE, out obType);
                r.TryRead(ep + Offsets.ENT_ENTITY_ID, out entId);
                r.TryRead(ep + Offsets.ENT_X, out x);
                r.TryRead(ep + Offsets.ENT_Y, out y);
                r.TryRead(ep + Offsets.ENT_Z, out z);
                if (ValidPtr(obType) && r.TryRead(obType + Offsets.PY_TYPE_NAME, out long namePtr) && ValidPtr(namePtr) && r.TryReadString(namePtr, out var s))
                    typeName = string.IsNullOrWhiteSpace(s) ? "<empty>" : s;
            }

            string dist = cam.HasValue && ValidCoord(x) && ValidCoord(y) && ValidCoord(z)
                ? Dist(x, y, z, cam.Value).ToString("F2")
                : "--";

            Console.WriteLine($"{n,-3} 0x{node:X12} 0x{ep:X12} {id,8} 0x{obType:X12} {typeName,-20} ({x,8:F2},{y,8:F2},{z,8:F2}) {dist,10}");
        }

        Console.WriteLine();
        Console.WriteLine($"[RESULT] nodes read = {n}");
        Console.WriteLine("[RESULT] Compare tp_name, XYZ and distance against the ESP diagnostic output.");
        Console.WriteLine("[RESULT] This program performs reads only.");
    }
}
