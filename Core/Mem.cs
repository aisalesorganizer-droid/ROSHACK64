using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace ROS64Hack.Core
{
    /// <summary>
    /// 64-bit external memory reader. All addresses are absolute VAs.
    /// Use BaseAddress to compute image-relative addresses: BaseAddress + OFFSET.
    /// </summary>
    public class Mem : IDisposable
    {
        // ── Win32 ──────────────────────────────────────────────────────────────

        [DllImport("kernel32.dll")]
        private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll")]
        private static extern bool ReadProcessMemory(
            IntPtr hProcess, IntPtr lpBaseAddress,
            byte[] lpBuffer, int nSize, out int lpBytesRead);

        [DllImport("kernel32.dll")]
        private static extern bool WriteProcessMemory(
            IntPtr hProcess, IntPtr lpBaseAddress,
            byte[] lpBuffer, int nSize, out int lpBytesWritten);

        // Only request what we need — less suspicious than PROCESS_ALL_ACCESS
        private const uint PROCESS_VM_READ        = 0x0010;
        private const uint PROCESS_VM_WRITE       = 0x0020;
        private const uint PROCESS_VM_OPERATION   = 0x0008;
        private const uint PROCESS_QUERY_INFO     = 0x0400;

        // ── State ──────────────────────────────────────────────────────────────

        private IntPtr _handle = IntPtr.Zero;
        public  IntPtr Handle     => _handle;
        public  long   BaseAddress { get; private set; }
        public  bool   IsAttached  => _handle != IntPtr.Zero;

        // ── Attach ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Attach to the first process named <paramref name="processName"/> (no .exe).
        /// Resolves the module base address of the main executable.
        /// </summary>
        public bool Attach(string processName)
        {
            var procs = Process.GetProcessesByName(processName);
            if (procs.Length == 0) return false;

            var proc = procs[0];
            _handle = OpenProcess(
                PROCESS_VM_READ | PROCESS_VM_WRITE |
                PROCESS_VM_OPERATION | PROCESS_QUERY_INFO,
                false, proc.Id);

            if (_handle == IntPtr.Zero) return false;

            // Enumerate modules to find the main module base
            foreach (ProcessModule mod in proc.Modules)
            {
                string name = mod.ModuleName.ToLowerInvariant();
                if (name == processName.ToLowerInvariant() + ".exe" ||
                    name == processName.ToLowerInvariant())
                {
                    BaseAddress = (long)mod.BaseAddress;
                    return true;
                }
            }

            // Fallback: first module is always the main exe
            if (proc.Modules.Count > 0)
            {
                BaseAddress = (long)proc.Modules[0].BaseAddress;
                return true;
            }

            return false;
        }

        // ── Generic read ───────────────────────────────────────────────────────

        public bool ReadBytes(long address, byte[] buffer, int size)
        {
            return ReadBytes(address, buffer, 0, size);
        }

        /// <summary>
        /// ReadProcessMemory helper for diagnostics that must not inflate the
        /// main RuntimeDiagnostics RPM counters. It is still read-only.
        /// </summary>
        public bool ReadBytesQuiet(long address, byte[] buffer, int bufferOffset, int size)
        {
            if (buffer == null || bufferOffset < 0 || size < 0 || bufferOffset + size > buffer.Length)
                return false;

            byte[] target = bufferOffset == 0 ? buffer : new byte[size];
            bool ok = ReadProcessMemory(_handle, (IntPtr)address, target, size, out int read);
            if (!ok || read != size)
                return false;

            if (bufferOffset != 0)
                Buffer.BlockCopy(target, 0, buffer, bufferOffset, size);

            return true;
        }

        public bool ReadBytes(long address, byte[] buffer, int bufferOffset, int size)
        {
            if (buffer == null || bufferOffset < 0 || size < 0 || bufferOffset + size > buffer.Length)
                return false;

            RuntimeDiagnostics.RpmCall();

            // ReadProcessMemory writes into the supplied byte[] starting at index 0.
            // Use a temporary buffer for non-zero destination offsets so the public
            // API remains safe and the dictionary reader can assemble page-sized chunks.
            byte[] target = bufferOffset == 0 ? buffer : new byte[size];
            bool ok = ReadProcessMemory(_handle, (IntPtr)address, target, size, out int read);
            if (!ok || read != size)
            {
                RuntimeDiagnostics.ReadFailure(address, size, read, Marshal.GetLastWin32Error(), null);
                return false;
            }

            if (bufferOffset != 0)
                Buffer.BlockCopy(target, 0, buffer, bufferOffset, size);

            return true;
        }

        public T Read<T>(long address) where T : struct => Read<T>(address, null);

        public T Read<T>(long address, string tag) where T : struct
        {
            int  size = Marshal.SizeOf<T>();
            var  buf  = new byte[size];
            RuntimeDiagnostics.RpmCall();
            bool ok   = ReadProcessMemory(_handle, (IntPtr)address, buf, size, out int read);
            if (!ok || read != size)
            {
                RuntimeDiagnostics.ReadFailure(address, size, read, Marshal.GetLastWin32Error(), tag);
                return default;
            }

            var h   = GCHandle.Alloc(buf, GCHandleType.Pinned);
            T   val = Marshal.PtrToStructure<T>(h.AddrOfPinnedObject());
            h.Free();
            return val;
        }

        // ── Typed reads ────────────────────────────────────────────────────────

        public long   ReadLong  (long a) => Read<long>(a);
        public long   ReadLong  (long a, string tag) => Read<long>(a, tag);

        public ulong  ReadUlong (long a) => Read<ulong>(a);
        public ulong  ReadUlong (long a, string tag) => Read<ulong>(a, tag);

        public int    ReadInt   (long a) => Read<int>(a);
        public int    ReadInt   (long a, string tag) => Read<int>(a, tag);

        public uint   ReadUint  (long a) => Read<uint>(a);
        public uint   ReadUint  (long a, string tag) => Read<uint>(a, tag);

        public float  ReadFloat (long a) => Read<float>(a);
        public float  ReadFloat (long a, string tag) => Read<float>(a, tag);

        public byte   ReadByte  (long a) => Read<byte>(a);
        public byte   ReadByte  (long a, string tag) => Read<byte>(a, tag);

        public bool   ReadBool  (long a) => ReadByte(a) != 0;
        public bool   ReadBool  (long a, string tag) => ReadByte(a, tag) != 0;

        /// <summary>Read a null-terminated ASCII string up to maxLen bytes.</summary>
        public string ReadString(long address, int maxLen = 64)
        {
            var buf = new byte[maxLen];
            RuntimeDiagnostics.RpmCall();
            ReadProcessMemory(_handle, (IntPtr)address, buf, maxLen, out int read);
            int end = Array.IndexOf(buf, (byte)0);
            return Encoding.ASCII.GetString(buf, 0, end < 0 ? read : end);
        }

        // ── Write (for NoClip) ─────────────────────────────────────────────────

        public bool WriteFloat(long address, float value)
        {
            var buf = BitConverter.GetBytes(value);
            return WriteProcessMemory(_handle, (IntPtr)address, buf, buf.Length, out _);
        }

        public bool WriteLong(long address, long value)
        {
            var buf = BitConverter.GetBytes(value);
            return WriteProcessMemory(_handle, (IntPtr)address, buf, buf.Length, out _);
        }

        // ── Dispose ────────────────────────────────────────────────────────────

        public void Dispose()
        {
            if (_handle != IntPtr.Zero)
            {
                CloseHandle(_handle);
                _handle = IntPtr.Zero;
            }
        }
    }
}
