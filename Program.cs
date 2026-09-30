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
    }
}
