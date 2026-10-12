<<<<<<< HEAD
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace ROS64EntityCorrelation;

internal sealed class Mem : IDisposable
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(IntPtr h, IntPtr addr, byte[] buf, int size, out int read);

    private const uint PROCESS_VM_READ = 0x0010;
    private const uint PROCESS_QUERY_INFORMATION = 0x0400;
    private IntPtr _h;
    public long BaseAddress { get; private set; }
    public bool Attached => _h != IntPtr.Zero;

    public bool Attach(string processName)
    {
        var p = Process.GetProcessesByName(processName).FirstOrDefault();
        if (p == null) return false;
        _h = OpenProcess(PROCESS_VM_READ | PROCESS_QUERY_INFORMATION, false, p.Id);
        if (_h == IntPtr.Zero) return false;
        try
        {
            BaseAddress = p.MainModule?.BaseAddress.ToInt64() ?? 0;
        }
        catch { BaseAddress = 0; }
        return BaseAddress != 0;
    }

    public bool ReadBytes(long address, byte[] buf, int offset, int count)
    {
        if (!Attached || buf == null || offset < 0 || count < 0 || offset + count > buf.Length) return false;
        byte[] tmp = offset == 0 ? buf : new byte[count];
        bool ok = ReadProcessMemory(_h, (IntPtr)address, tmp, count, out int read);
        if (!ok || read != count) return false;
        if (offset != 0) Buffer.BlockCopy(tmp, 0, buf, offset, count);
        return true;
    }

    public bool ReadQ(long addr, out long value)
    {
        value = 0;
        var b = new byte[8];
        if (!ReadBytes(addr, b, 0, 8)) return false;
        value = BitConverter.ToInt64(b, 0);
        return true;
    }

    public bool ReadU32(long addr, out uint value)
    {
        value = 0;
        var b = new byte[4];
        if (!ReadBytes(addr, b, 0, 4)) return false;
        value = BitConverter.ToUInt32(b, 0);
        return true;
    }

    public bool ReadI32(long addr, out int value)
    {
        value = 0;
        var b = new byte[4];
        if (!ReadBytes(addr, b, 0, 4)) return false;
        value = BitConverter.ToInt32(b, 0);
        return true;
    }

    public bool ReadF(long addr, out float value)
    {
        value = 0;
        var b = new byte[4];
        if (!ReadBytes(addr, b, 0, 4)) return false;
        value = BitConverter.ToSingle(b, 0);
        return true;
    }

    public string ReadAscii(long addr, int maxLen = 96)
    {
        var b = new byte[maxLen];
        if (!ReadBytes(addr, b, 0, maxLen)) return "";
        int n = Array.IndexOf(b, (byte)0);
        if (n < 0) n = b.Length;
        return Encoding.ASCII.GetString(b, 0, n);
    }

    public string ReadUtf16(long addr, int maxChars = 96)
    {
        var b = new byte[maxChars * 2];
        if (!ReadBytes(addr, b, 0, b.Length)) return "";
        int n = 0;
        while (n + 1 < b.Length && (b[n] != 0 || b[n + 1] != 0)) n += 2;
        return Encoding.Unicode.GetString(b, 0, n);
    }

    public void Dispose()
    {
        if (_h != IntPtr.Zero) CloseHandle(_h);
        _h = IntPtr.Zero;
    }
}

internal static class Program
{
    private const long EM_STATIC_PTR = 0x031F53D8;
    private const long EM_ALL_ENTITIES = 0x10E0;
    private const long EM_CONTROLLED = 0x10F8;
    private const long NODE_LEFT = 0x00;
    private const long NODE_PARENT = 0x08;
    private const long NODE_RIGHT = 0x10;
    private const long NODE_SENTINEL = 0x18;
    private const long NODE_KEY = 0x20;
    private const long NODE_VALUE = 0x28;

    private const long ENT_TYPE = 0x08;
    private const long ENT_ID = 0x18;
    private const long ENT_X = 0x1C;
    private const long ENT_Y = 0x20;
    private const long ENT_Z = 0x24;
    private const long PY_TYPE_NAME = 0x18;
    private const long PY_DICTO = 0x1B0;
    private const long PY_USED = 0x18;
    private const long PY_MASK = 0x20;
    private const long PY_TABLE = 0x28;
    private const long DICT_KEY = 0x08;
    private const long DICT_VALUE = 0x10;
    private const long DICT_STRIDE = 0x18;
    private const int AOI_SLOT = 770;

    private sealed record Bga(long Ptr, uint Id, float X, float Y, float Z, ulong Type);
    private sealed record TreeEntry(long Node, uint Key, long Value);

    private static bool Ptr(long p) => p >= 0x10000 && p < 0x0000800000000000L;
    private static bool Coord(float f) => float.IsFinite(f) && f > -50000 && f < 50000;

    private static bool DictHeader(Mem m, long dict, out int used, out int mask, out long table)
    {
        used = mask = 0; table = 0;
        if (!Ptr(dict)) return false;
        if (!m.ReadQ(dict + PY_USED, out var ur) || !m.ReadQ(dict + PY_MASK, out var mr) || !m.ReadQ(dict + PY_TABLE, out table)) return false;
        if (ur < 0 || ur > 0x10000 || mr < 0 || mr > 0xFFFF || !Ptr(table)) return false;
        used = (int)ur; mask = (int)mr;
        return used <= mask + 1;
    }

    private static string TypeName(Mem m, long obj)
    {
        if (!Ptr(obj) || !m.ReadQ(obj + ENT_TYPE, out var t) || !Ptr(t) || !m.ReadQ(t + PY_TYPE_NAME, out var n) || !Ptr(n)) return "";
        return m.ReadAscii(n);
    }

    private static long ResolvePlayer(Mem m, long em)
    {
        if (!m.ReadQ(em + EM_CONTROLLED, out var c) || !Ptr(c)) return 0;
        if (!m.ReadQ(c + NODE_PARENT, out var root) || !Ptr(root) || root == c) return 0;
        if (!m.ReadQ(root + 0x20, out var p) || !Ptr(p)) return 0;
        if (!string.Equals(TypeName(m, p), "PlayerBattleGroundAvatar", StringComparison.Ordinal)) return 0;
        return p;
    }

    private static List<Bga> EnumerateAoi(Mem m, long player)
    {
        var list = new List<Bga>();
        if (!m.ReadQ(player + PY_DICTO, out var pd) || !DictHeader(m, pd, out _, out var pmask, out var ptable)) return list;
        long entry = ptable + (long)AOI_SLOT * DICT_STRIDE;
        if (!m.ReadQ(entry + DICT_VALUE, out var aoi) || !Ptr(aoi) || !DictHeader(m, aoi, out var used, out var mask, out var table)) return list;

        var seen = new HashSet<long>();
        int slots = Math.Min(mask + 1, 0x10000);
        for (int i = 0; i < slots; i++)
        {
            long ep = table + (long)i * DICT_STRIDE;
            if (!m.ReadQ(ep + DICT_VALUE, out var obj) || !Ptr(obj) || !seen.Add(obj)) continue;
            if (!string.Equals(TypeName(m, obj), "BattleGroundAvatar", StringComparison.Ordinal)) continue;
            if (!m.ReadU32(obj + ENT_ID, out var id)) continue;
            if (!m.ReadF(obj + ENT_X, out var x) || !m.ReadF(obj + ENT_Y, out var y) || !m.ReadF(obj + ENT_Z, out var z)) continue;
            if (!Coord(x) || !Coord(y) || !Coord(z)) continue;
            list.Add(new Bga(obj, id, x, y, z, (ulong)(m.ReadQ(obj + ENT_TYPE, out var t) ? t : 0)));
        }
        return list;
    }

    private static void DumpQwords(Mem m, TextWriter log, long addr, int count, string label)
    {
        log.WriteLine($"[{label}] addr=0x{addr:X} qwords={count}");
        for (int i = 0; i < count; i++)
        {
            if (!m.ReadQ(addr + i * 8L, out var q))
            {
                log.WriteLine($"  +0x{i * 8:X3}: <unreadable>");
                continue;
            }
            log.WriteLine($"  +0x{i * 8:X3}: 0x{q:X16}");
        }
    }

    private static void DumpCandidate(Mem m, TextWriter log, long p, string label)
    {
        if (!Ptr(p)) { log.WriteLine($"[{label}] ptr=0x{p:X} invalid"); return; }
        string tn = TypeName(m, p);
        m.ReadU32(p + ENT_ID, out var id);
        m.ReadF(p + ENT_X, out var x); m.ReadF(p + ENT_Y, out var y); m.ReadF(p + ENT_Z, out var z);
        m.ReadQ(p, out var q0); m.ReadQ(p + 8, out var q8); m.ReadQ(p + 0x10, out var q10); m.ReadQ(p + 0x18, out var q18);
        m.ReadQ(p + 0x20, out var q20); m.ReadQ(p + 0x28, out var q28); m.ReadQ(p + 0x38, out var q38); m.ReadQ(p + 0x48, out var q48);
        log.WriteLine($"[{label}] ptr=0x{p:X} type='{tn}' id=0x{id:X8} xyz=({x:F3},{y:F3},{z:F3})");
        log.WriteLine($"  +00=0x{q0:X16} +08=0x{q8:X16} +10=0x{q10:X16} +18=0x{q18:X16}");
        log.WriteLine($"  +20=0x{q20:X16} +28=0x{q28:X16} +38=0x{q38:X16} +48=0x{q48:X16}");
    }

    private static List<TreeEntry> WalkTree(Mem m, long sentinel, int maxNodes = 512)
    {
        var outList = new List<TreeEntry>();
        if (!Ptr(sentinel) || !m.ReadQ(sentinel + NODE_LEFT, out var root) || !Ptr(root) || root == sentinel) return outList;
        var q = new Queue<long>();
        var seen = new HashSet<long>();
        q.Enqueue(root);
        while (q.Count > 0 && outList.Count < maxNodes)
        {
            long n = q.Dequeue();
            if (!Ptr(n) || n == sentinel || !seen.Add(n)) continue;
            m.ReadQ(n + NODE_LEFT, out var l);
            m.ReadQ(n + NODE_RIGHT, out var r);
            m.ReadU32(n + NODE_KEY, out var key);
            m.ReadQ(n + NODE_VALUE, out var val);
            outList.Add(new TreeEntry(n, key, val));
            if (Ptr(l) && l != sentinel) q.Enqueue(l);
            if (Ptr(r) && r != sentinel) q.Enqueue(r);
        }
        return outList;
    }

    public static void Main()
    {
        Console.WriteLine("ROS64 Entity-ID Correlation Capture v1");
        Console.WriteLine("Target: ros64.exe");
        Console.WriteLine("Purpose: correlate AOI BattleGroundAvatar IDs with the EM+0x10E0 collection/tree without modifying ESP logic.");
        Console.WriteLine();

        using var mem = new Mem();
        while (!mem.Attach("ros64"))
        {
            Console.WriteLine("[*] Waiting for ros64.exe...");
            Thread.Sleep(1000);
        }
        Console.WriteLine($"[+] Attached base=0x{mem.BaseAddress:X}");

        string path = Path.Combine(Environment.CurrentDirectory, $"ROS64_EntityCorrelation_{DateTime.UtcNow:yyyyMMdd_HHmmss}.txt");
        using var log = new StreamWriter(path, false, new UTF8Encoding(false)) { AutoFlush = true };

        log.WriteLine("ROS64 ENTITY-ID CORRELATION CAPTURE V1");
        log.WriteLine($"UTC={DateTime.UtcNow:O}");
        log.WriteLine($"Base=0x{mem.BaseAddress:X}");

        for (int snap = 1; snap <= 8; snap++)
        {
            Console.WriteLine($"[SNAPSHOT {snap}/8]");
            log.WriteLine();
            log.WriteLine($"================ SNAPSHOT {snap} UTC={DateTime.UtcNow:O} ================");

            if (!mem.ReadQ(mem.BaseAddress + EM_STATIC_PTR, out var em) || !Ptr(em))
            {
                log.WriteLine("[EM] unreadable");
                Thread.Sleep(500);
                continue;
            }
            log.WriteLine($"[EM] 0x{em:X}");

            long player = ResolvePlayer(mem, em);
            log.WriteLine($"[PLAYER] ptr=0x{player:X} type='{TypeName(mem, player)}'");
            if (player == 0) { Thread.Sleep(500); continue; }

            var bga = EnumerateAoi(mem, player);
            Console.WriteLine($"  AOI BGAs={bga.Count}");
            log.WriteLine($"[AOI] count={bga.Count}");
            foreach (var e in bga.OrderBy(x => x.Id).Take(64))
                log.WriteLine($"[BGA] id=0x{e.Id:X8} ptr=0x{e.Ptr:X} xyz=({e.X:F3},{e.Y:F3},{e.Z:F3})");

            long allField = em + EM_ALL_ENTITIES;
            if (!mem.ReadQ(allField, out var all) || !Ptr(all))
            {
                log.WriteLine($"[EM+10E0] field=0x{allField:X} unreadable");
                Thread.Sleep(500);
                continue;
            }

            log.WriteLine($"[EM+10E0] field=0x{allField:X} value=0x{all:X}");
            DumpQwords(mem, log, allField, 4, "EM+10E0 HEADER");
            DumpQwords(mem, log, all, 12, "EM+10E0 TARGET");

            // Historical evidence shows +0x10E8 as a count alongside +0x10E0.
            long countRaw = 0;
            mem.ReadQ(em + 0x10E8, out countRaw);
            int count = countRaw >= 0 && countRaw <= 0x1000 ? (int)countRaw : -1;
            log.WriteLine($"[EM+10E8] raw=0x{countRaw:X} interpretedCount={count}");

            if (count > 0)
            {
                log.WriteLine("[COLLECTION CANDIDATE] Inspecting pointer-sized entries at EM+0x10E0 target with stride 8.");
                var bgaIds = new HashSet<uint>(bga.Select(x => x.Id));
                for (int i = 0; i < Math.Min(count, 64); i++)
                {
                    long ep = all + i * 8L;
                    if (!mem.ReadQ(ep, out var candidate)) continue;
                    uint cid = 0;
                    bool idMatch = false;
                    if (Ptr(candidate) && mem.ReadU32(candidate + ENT_ID, out cid))
                        idMatch = bgaIds.Contains(cid);
                    log.WriteLine($"[COL ENTRY] i={i} addr=0x{ep:X} ptr=0x{candidate:X} bgaIdMatch={idMatch}");
                    if (Ptr(candidate)) DumpCandidate(mem, log, candidate, $"COL CANDIDATE {i}");
                    if (idMatch)
                    {
                        var target = bga.First(x => x.Id == cid);
                        log.WriteLine($"[*** DIRECT MATCH ***] BGA id=0x{cid:X8} AOIptr=0x{target.Ptr:X} -> collectionPtr=0x{candidate:X}");
                    }
                }
            }

            // The tree itself is also recorded, but we only treat ID matches as correlation evidence.
            var tree = WalkTree(mem, all, 512);
            log.WriteLine($"[TREE] sentinel=0x{all:X} nodes={tree.Count}");
            var byId = bga.ToDictionary(x => x.Id, x => x.Ptr);
            foreach (var n in tree.Where(x => byId.ContainsKey(x.Key)).Take(128))
            {
                log.WriteLine($"[TREE-ID-MATCH] node=0x{n.Node:X} key=0x{n.Key:X8} value=0x{n.Value:X} AOIptr=0x{byId[n.Key]:X}");
                DumpCandidate(mem, log, n.Value, "TREE VALUE");
                DumpQwords(mem, log, n.Node, 10, "MATCHED TREE NODE RAW");
            }

            Thread.Sleep(500);
        }

        Console.WriteLine();
        Console.WriteLine($"[+] Capture complete: {path}");
        Console.WriteLine("    Send that TXT back. No ESP/aim code was changed by this capture.");
=======
using System;
using System.Threading;
using System.Windows.Forms;
using ROS64Hack.Core;
using ROS64Hack.Overlay;

namespace ROS64Hack
{
    internal static class Program
    {
        [STAThread]
        static void Main()
        {
            // Catch any unhandled exception and print it before the window closes
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("\n[CRASH] " + e.ExceptionObject);
                Console.ResetColor();
                Console.WriteLine("\nPress any key to exit...");
                Console.ReadKey();
            };

            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (s, e) =>
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("\n[UI CRASH] " + e.Exception);
                Console.ResetColor();
                Console.WriteLine("\nPress any key to exit...");
                Console.ReadKey();
            };

            Application.SetHighDpiMode(HighDpiMode.DpiUnaware);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("  ROS64 Hack v1.0  |  BigWorld Engine  |  RE: 100%");
            Console.ResetColor();
            Console.WriteLine();

            var mem = new Mem();

            Console.Write("  [*] Waiting for ros64.exe");
            int attempts = 0;
            while (!mem.Attach("ros64"))
            {
                Console.Write(".");
                attempts++;
                if (attempts % 10 == 0) Console.WriteLine($" ({attempts}s)");
                Thread.Sleep(1000);
            }
            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"  [+] Attached!  PID found.");
            Console.ResetColor();
            Console.WriteLine($"  [+] Base address : 0x{mem.BaseAddress:X}");
            Console.WriteLine($"  [+] EM ptr VA    : 0x{mem.BaseAddress + Offsets.EM_STATIC_PTR:X}");
            Console.WriteLine($"  [+] Cam anchor VA: 0x{mem.BaseAddress + Offsets.CAM_ANCHOR_OFFSET:X}");
            Console.WriteLine();

            // Quick sanity — read the EM ptr right now
            try
            {
                long emTest = mem.Read<long>(mem.BaseAddress + Offsets.EM_STATIC_PTR, "PROGRAM.EM");
                Console.WriteLine($"  [+] EM ptr read  : 0x{emTest:X}  {(emTest != 0 ? "OK" : "WARNING: NULL")}");
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"  [!] EM read FAILED: {ex.Message}");
                Console.ResetColor();
            }

            // Quick sanity — camera chain
            try
            {
                long a = mem.Read<long>(mem.BaseAddress + Offsets.CAM_ANCHOR_OFFSET, "PROGRAM.CamAnchor");
                long b = a != 0 ? mem.Read<long>(a, "PROGRAM.CamChain1") : 0;
                long c = b != 0 ? mem.Read<long>(b, "PROGRAM.CamChain2") : 0;
                Console.WriteLine($"  [+] Cam chain    : 0x{a:X} -> 0x{b:X} -> 0x{c:X}  {(c != 0 ? "OK" : "WARNING: NULL")}");
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"  [!] Cam read FAILED: {ex.Message}");
                Console.ResetColor();
            }

            Console.WriteLine();
            Console.WriteLine("  [*] Launching overlay...");
            Console.WriteLine("  [*] INSERT = toggle menu  |  DELETE = exit");
            Console.WriteLine();

            try
            {
                var overlay = new HackOverlay(mem);
                Application.Run(overlay);
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"\n[OVERLAY CRASH] {ex}");
                Console.ResetColor();
            }

            Console.WriteLine("\n  [*] Exited. Press any key...");
            Console.ReadKey();
        }
>>>>>>> 5beea08de40c5c9f3ea2978b87d57747f9856aea
    }
}
