using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace BingoQoL;

public static class Startup
{
    private static int started;
    private static string? logPath;

    // Rust's C ABI bool is one byte. Do not use Windows BOOL (four bytes).
    [UnmanagedCallersOnly(EntryPoint = "BingoQoL_Initialize", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static byte Initialize()
    {
        try
        {
            if (!string.Equals(Process.GetCurrentProcess().ProcessName, "sekiro", StringComparison.OrdinalIgnoreCase))
                return 0;
            if (Interlocked.CompareExchange(ref started, 1, 0) != 0) return 1;
            try
            {
                new Thread(Run) { IsBackground = true, Name = "Sekiro Bingo QoL" }.Start();
                return 1;
            }
            catch { Interlocked.Exchange(ref started, 0); return 0; }
        }
        catch { return 0; }
    }

    private static unsafe void Run()
    {
        var borderless = new BorderlessWindow();
        IntPtr previousDpi = IntPtr.Zero;
        try
        {
            // Resolve this native image by an address, avoiding same-name managed assemblies.
            delegate* unmanaged[Cdecl]<byte> entry = &Initialize;
            if (!GetModuleHandleExW(0x6, (IntPtr)entry, out var module))
                throw new InvalidOperationException("Could not resolve BingoQoL.dll module.");
            var path = new StringBuilder(32768);
            uint length = GetModuleFileNameW(module, path, path.Capacity);
            if (module == IntPtr.Zero || length == 0 || length >= path.Capacity)
                throw new InvalidOperationException("Could not resolve BingoQoL.dll location.");
            var directory = Path.GetDirectoryName(path.ToString())!;
            logPath = Path.Combine(directory, "qol.log");
            Log("Starting QoL helper.");
            var settings = QolSettings.Parse(File.ReadAllText(Path.Combine(directory, "settings.json")));
            Log($"Settings loaded: DisableTutorials={settings.DisableTutorials}, Borderless={settings.Borderless}.");
            using var process = Process.GetCurrentProcess();
            if (settings.DisableTutorials)
            {
                // Poll only read-only validation. Once writes begin, never retry a failed patch.
                var deadline = Stopwatch.StartNew();
                bool ready = false;
                while (deadline.Elapsed < TimeSpan.FromSeconds(120))
                {
                    process.Refresh();
                    var gameModule = process.MainModule!;
                    using var memory = new ProcessMemory(process.Id, gameModule.BaseAddress);
                    try
                    {
                        var version = Tutorials.DetectVersion(gameModule.ModuleMemorySize, memory);
                        ready = Tutorials.Offsets(version).All(offset => memory.Read(offset, 1)[0] is 0x74 or 0x75);
                    }
                    catch (InvalidOperationException) { }
                    if (ready) break;
                    Thread.Sleep(250);
                }
                if (ready)
                {
                    try { Log($"Tutorials disabled (Sekiro {Tutorials.ApplyToProcess(process)})."); }
                    catch (Exception error) { Log($"Tutorial patch failed: {error.Message}"); }
                }
                else Log("Tutorial code was not recognized within 120 seconds. No tutorial bytes changed.");
            }
            if (!settings.Borderless) return;
            // Scope DPI awareness to our worker, preserving the game's own setting.
            previousDpi = SetThreadDpiAwarenessContext(new IntPtr(-4));
            Log("Borderless enabled. Select WINDOWED mode in Sekiro.");
            string lastStatus = "";
            while (true)
            {
                process.Refresh();
                var status = borderless.Update(process.MainWindowHandle);
                if (status != lastStatus) { Log($"Borderless: {status}"); lastStatus = status; }
                Thread.Sleep(500);
            }
        }
        catch (Exception error) { Log($"QoL stopped: {error.Message}"); }
        finally
        {
            try { borderless.Restore(); } catch (Exception error) { Log($"Window restoration failed: {error.Message}"); }
            if (previousDpi != IntPtr.Zero) SetThreadDpiAwarenessContext(previousDpi);
        }
    }

    private static void Log(string message)
    {
        try
        {
            if (logPath != null) File.AppendAllText(logPath, $"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
        }
        catch { /* An unwritable log must not stop the game or QoL worker. */ }
    }

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern bool GetModuleHandleExW(uint flags, IntPtr address, out IntPtr module);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetModuleFileNameW(IntPtr module, StringBuilder path, int size);
    [DllImport("user32.dll")]
    private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
}
