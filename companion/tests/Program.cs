using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using XivCompanionBridge;
using AutoMarketbuddy.Services;

var directory = args[0];
Directory.CreateDirectory(directory);
var path = Path.Combine(directory, "state.json");
var errors = new List<Exception>();
var publisher = new StatePublisher(path, errors.Add);
var actor = new Actor("Offline test player", "Player", 100, 12345, 67890, 10000, 10000,
    new(1, 2, 3), true, 123, 0.5f, 2f);
// Serialize the actual AMB diagnostic types; no duplicate JSON schema lives in this fixture.
var session = new MarketSession(null!);
var operation = session.GetDiagnostics(1000) with
{
    RunId = "offline-fixture", Mode = "Preview", Phase = "WaitClosed", Status = "Preview only",
    Item = new(20, "000000000000001E", 7, 42, true, 5, 1000), Undercut = 1,
    Decision = new(899, "Undercut once", new(4, 1, 0, 1, 1, 1, 0, 900)), QuoteAgeMs = 500,
    ComparisonSource = "Cache", CachedItems = 1,
    Trace = [new(500, "WaitQuote", "Complete matching response"), new(1000, "WaitClosed", "Preview only")],
};
var amb = new DiagnosticsSnapshot(1, DateTimeOffset.UtcNow, "1.1.0.0", operation);
var diagnostics = new Diagnostics("15.0.3.9",
    [new("Auto-Marketbuddy", "AutoMarketbuddy", "1.1.0.0", 15, true, false, false, true),
     new("Old test plugin", "OldTestPlugin", "1.0.0.0", 14, false, true, false, false)],
    ["RetainerSellList"], "Available", JsonSerializer.Serialize(amb, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
var snapshot = new Snapshot(1, DateTimeOffset.UtcNow, 2, "0.2.0.0", true, true,
    new(actor, "Test World", "Test Home", "WHM"), actor,
    new(123, "Offline test area", 456, 1), ["InCombat", "Casting"], Diagnostics: diagnostics);
Check(publisher.Publish(Snapshot.Empty(1, true)), "accept initial snapshot without a reader");
Check(publisher.Publish(snapshot), "replace pending snapshot without blocking");
publisher.Complete();
Check(!publisher.Publish(Snapshot.Empty(3, true)), "reject snapshots after completion");
await publisher.RunAsync().WaitAsync(TimeSpan.FromSeconds(5));
using (var json = JsonDocument.Parse(await File.ReadAllTextAsync(path)))
{
    Check(json.RootElement.GetProperty("sequence").GetInt64() == 2, "latest-only queue drains on shutdown");
    Check(json.RootElement.GetProperty("player").GetProperty("actor").GetProperty("name").GetString() == actor.Name,
        "serialize the real state contract with camelCase fields");
}
Check(!File.Exists(path + ".tmp") && errors.Count == 0, "publish atomic snapshot and clean temporary file");

// Preserve the actual C# wire format for the Python consumer check.
File.Copy(path, Path.Combine(directory, "contract.json"), true);
var stopped = new StatePublisher(path, errors.Add);
stopped.Publish(Snapshot.Empty(3, false));
stopped.Complete();
await stopped.RunAsync().WaitAsync(TimeSpan.FromSeconds(5));
using (var json = JsonDocument.Parse(await File.ReadAllTextAsync(path)))
    Check(!json.RootElement.GetProperty("bridgeActive").GetBoolean(), "publish explicit unloaded state");

var invalidParent = Path.Combine(directory, "not-a-directory");
await File.WriteAllTextAsync(invalidParent, "fixture");
var failing = new StatePublisher(Path.Combine(invalidParent, "state.json"), errors.Add);
failing.Publish(snapshot);
failing.Complete();
await failing.RunAsync().WaitAsync(TimeSpan.FromSeconds(5));
Check(errors.Count == 1, "handle file failure without crashing the worker");
using (var dll = File.OpenRead(args[1]))
using (var pe = new PEReader(dll))
using (var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(args[2])))
{
    var metadata = pe.GetMetadataReader();
    var assembly = metadata.GetAssemblyDefinition();
    var json = manifest.RootElement;
    Check(metadata.GetString(assembly.Name) == "XivCompanionBridge"
        && assembly.Version.ToString() == "0.2.0.0"
        && json.GetProperty("InternalName").GetString() == "XivCompanionBridge"
        && json.GetProperty("AssemblyVersion").GetString() == assembly.Version.ToString()
        && json.GetProperty("DalamudApiLevel").GetInt32() == 15
        && json.GetProperty("CanUnloadAsync").GetBoolean(), "built assembly and manifest agree on identity, version and API");
}
Console.WriteLine("9 bridge checks passed.");

static void Check(bool condition, string name)
{
    if (!condition) throw new Exception(name);
    Console.WriteLine($"PASS {name}");
}
