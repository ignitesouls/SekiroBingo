using BingoQoL;
using System.Runtime.InteropServices;

if (args.Length == 2 && args[0] == "--native")
{
    var handle = NativeLibrary.Load(Path.GetFullPath(args[1]));
    var initialize = Marshal.GetDelegateForFunctionPointer<InitializeNative>(NativeLibrary.GetExport(handle, "BingoQoL_Initialize"));
    byte expected = System.Diagnostics.Process.GetCurrentProcess().ProcessName.Equals("sekiro", StringComparison.OrdinalIgnoreCase) ? (byte)1 : (byte)0;
    if (initialize() != expected || initialize() != expected) throw new Exception("Native initializer returned an unexpected value.");
    if (expected == 1)
    {
        var log = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(args[1]))!, "qol.log");
        var deadline = System.Diagnostics.Stopwatch.StartNew();
        while ((!File.Exists(log) || !File.ReadAllText(log).Contains("Settings loaded:")) && deadline.ElapsedMilliseconds < 5000) Thread.Sleep(50);
        Thread.Sleep(200);
        if (!File.Exists(log) || File.ReadAllLines(log).Count(line => line.Contains("Starting QoL helper.")) != 1)
            throw new Exception("Initializer did not start exactly one worker.");
        if (!File.ReadAllText(log).Contains("Settings loaded: DisableTutorials=False, Borderless=False."))
            throw new Exception("Worker did not read disabled settings beside the native DLL.");
    }
    Console.WriteLine("PASS: native C ABI export and repeated initialization.");
    // NativeAOT does not support unloading. Process exit releases the module.
    return;
}

var valid = QolSettings.Parse("{\"DisableTutorials\":false,\"Borderless\":true}");
if (valid.DisableTutorials) throw new Exception("Disabled tutorials ignored.");
if (!valid.Borderless) throw new Exception("Borderless ignored.");
foreach (var invalid in new[] { "{}", "{\"DisableTutorials\":1,\"Borderless\":true}", "null", "{" })
{
    try { QolSettings.Parse(invalid); throw new Exception("Invalid settings accepted."); }
    catch (Exception error) when (error is InvalidDataException or System.Text.Json.JsonException) { }
}
Console.WriteLine("PASS: settings enable/disable and invalid input.");

foreach (var size in new[] { 67727360, 67731456, 70066176 })
{
    foreach (bool six in size == 70066176 ? new[] { false, true } : new[] { false })
    {
        var memory = new TestMemory(size, six);
        Tutorials.Apply(size, memory);
        if (memory.Sites.Any(offset => memory.Bytes[offset] != 0x75)) throw new Exception("Tutorials not patched.");
        var writes = memory.Writes;
        Tutorials.Apply(size, memory);
        if (writes != memory.Writes) throw new Exception("Already patched sites rewritten.");
    }
}
var unknown = new TestMemory(67727360, false);
ExpectFailure(() => Tutorials.Apply(1, unknown));
if (unknown.Writes != 0) throw new Exception("Unknown executable modified.");
var invalidSite = new TestMemory(67727360, false);
invalidSite.Bytes[0xD6D51C] = 0x90;
ExpectFailure(() => Tutorials.Apply(67727360, invalidSite));
if (invalidSite.Writes != 0) throw new Exception("Validation did not precede every write.");
var partial = new TestMemory(67727360, false) { FailOnceAt = 0xD6EB98 };
ExpectFailure(() => Tutorials.Apply(67727360, partial));
if (partial.Sites.Any(offset => partial.Bytes[offset] != 0x74)) throw new Exception("Partial write not rolled back.");
var failedReadback = new TestMemory(67727360, false) { IgnoreWrites = true };
ExpectFailure(() => Tutorials.Apply(67727360, failedReadback));
Console.WriteLine("PASS: all supported builds, idempotence, unknown builds, prevalidation, partial-write rollback and readback failure.");

static void ExpectFailure(Action action)
{
    try { action(); } catch (InvalidOperationException) { return; }
    throw new Exception("Expected safe patch failure.");
}

sealed class TestMemory : IMemory
{
    public Dictionary<int, byte> Bytes { get; } = new();
    public int[] Sites { get; }
    public int Writes { get; private set; }
    public int? FailOnceAt { get; set; }
    public bool IgnoreWrites { get; set; }
    public TestMemory(int size, bool six)
    {
        Sites = size switch
        {
            67727360 => [0xDC7A8E, 0xD6EB98, 0xD6D51C],
            67731456 => [0xDC83BE, 0xD6F4C8, 0xD6DE4C],
            _ when six => [0xDEF8AD, 0xD94F38, 0xD9382C],
            _ => [0xDEF53D, 0xD94BC8, 0xD934BC]
        };
        foreach (var offset in Sites) Bytes[offset] = 0x74;
        var signature = six ? 0xE1B51B : 0xE1B1AB;
        byte[] logo = [0x74, 0x30, 0x48, 0x8D];
        for (int i = 0; i < logo.Length; i++) Bytes[signature + i] = logo[i];
    }
    public byte[] Read(int offset, int count) => Enumerable.Range(offset, count).Select(i => Bytes.GetValueOrDefault(i)).ToArray();
    public void Write(int offset, byte value)
    {
        Writes++;
        if (!IgnoreWrites) Bytes[offset] = value;
        if (FailOnceAt == offset) { FailOnceAt = null; throw new InvalidOperationException("Simulated partial write."); }
    }
}

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
delegate byte InitializeNative();
