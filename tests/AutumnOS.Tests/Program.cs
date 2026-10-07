using System.Diagnostics;
using System.Text.Json;
using AutumnOS.Contracts;
using AutumnOS.Identity;
using AutumnOS.Tests;

if (args.Contains("--probe-store"))
{
    int index = Array.IndexOf(args, "--report");
    if (index < 0 || index + 1 >= args.Length) return 2;
    return await StoreLiveProbe.RunAsync(args[index + 1]);
}

if (args.Contains("--probe-logto"))
{
    var config = LogtoConfigurationLoader.Load(Path.Combine(AppContext.BaseDirectory, "config", "logto.public.json"));
    if (!config.IsValid)
    {
        WriteProbeReport(new { schema_version = 1, source_snapshot_id = BrandInfo.SourceSnapshotId,
            build_id = BrandInfo.BuildId, executed_utc = DateTimeOffset.UtcNow, test = "public_discovery_only",
            passed = false, status = "not_run", result = config.SafeSummary, issues = config.Issues,
            correlation_id = Guid.NewGuid().ToString("N"), login = "not_run", registration = "unverified" });
        return 2;
    }
    var result = await LogtoDiscoveryProbe.ProbeAsync(config.Options!, CancellationToken.None);
    var report = new { schema_version = 1, source_snapshot_id = BrandInfo.SourceSnapshotId, build_id = BrandInfo.BuildId,
        executed_utc = DateTimeOffset.UtcNow, test = "public_discovery_only", passed = result.IsValid,
        status = result.IsValid ? "passed" : "failed", result = result.SafeSummary, issues = result.Issues,
        correlation_id = result.CorrelationId, login = "not_run", registration = "unverified" };
    WriteProbeReport(report);
    return result.IsValid ? 0 : 1;

    void WriteProbeReport(object report)
    {
        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        var reportIndex = Array.IndexOf(args, "--report");
        if (reportIndex >= 0 && reportIndex + 1 < args.Length)
        {
            var reportPath = Path.GetFullPath(args[reportIndex + 1]);
            Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
            File.WriteAllText(reportPath, json);
        }
        Console.WriteLine(json);
    }
}

var cases = StorageTests.Cases().Concat(DesktopPreferencesTests.Cases()).Concat(DesktopLayoutTests.Cases()).Concat(IdentityTests.Cases()).Concat(RuntimeTests.Cases()).Concat(PackageTests.Cases()).Concat(ContractCases());
cases = cases.Concat(ScopedStorageTests.Cases()).Concat(T03RuntimeTests.Cases()).Concat(DataBridgeTests.Cases());
cases = cases.Concat(MaintenanceTests.Cases());
cases = cases.Concat(SupportBundleTests.Cases());
cases = cases.Concat(PortableLayoutTests.Cases());
cases = cases.Concat(DeveloperSimulationTests.Cases());
cases = cases.Concat(UpdateCoreTests.Cases());
if (OperatingSystem.IsWindows()) cases = cases.Concat(UpdateProtocolTests.Cases()).Concat(SetupTransactionTests.Cases());
cases = cases.Concat(ServerIdentityTests.Cases()).Concat(DeveloperToolsTests.Cases()).Concat(PackageDesktopTests.Cases());
cases = cases.Concat(StoreCatalogTests.Cases()).Concat(StoreDownloadTests.Cases()).Concat(StoreInstallTests.Cases()).Concat(StoreCoordinatorTests.Cases());
if (OperatingSystem.IsWindows()) cases = cases.Concat(LauncherTests.Cases()).Concat(IdentitySessionTests.Cases()).Concat(IdentityProtocolTests.Cases());
int filterIndex = Array.IndexOf(args, "--filter");
string[] filters = [];
if (filterIndex >= 0)
{
    if (filterIndex + 1 >= args.Length || args[filterIndex + 1].StartsWith("--", StringComparison.Ordinal)
        || string.IsNullOrWhiteSpace(args[filterIndex + 1]))
    {
        Console.Error.WriteLine("--filter requires one or more comma-separated test-name prefixes.");
        return 2;
    }
    filters = args[filterIndex + 1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    if (filters.Length == 0) return 2;
    cases = cases.Where(test => filters.Any(prefix => test.Name.StartsWith(prefix, StringComparison.Ordinal)));
}
var results = new List<object>();
var failed = 0;
foreach (var (name, run) in cases)
{
    var timer = Stopwatch.StartNew();
    try
    {
        run();
        results.Add(new { name, status = "passed", milliseconds = timer.ElapsedMilliseconds });
        Console.WriteLine($"PASS {name}");
    }
    catch (Exception e)
    {
        failed++;
        results.Add(new { name, status = "failed", milliseconds = timer.ElapsedMilliseconds, error_type = e.GetType().Name, detail = e.Message });
        Console.WriteLine($"FAIL {name}: {e.GetType().Name}: {e.Message}");
    }
}
var document = new { schema_version = 1, task_id = BrandInfo.BuildId.StartsWith("T06-", StringComparison.Ordinal) ? "T06" : "T05", source_snapshot_id = BrandInfo.SourceSnapshotId,
    build_id = BrandInfo.BuildId, executed_utc = DateTimeOffset.UtcNow, environment = Environment.OSVersion.VersionString,
    status = failed == 0 && results.Count > 0 ? "passed" : "failed", total = results.Count, failed, filters, results };
var outputIndex = Array.IndexOf(args, "--report");
if (outputIndex >= 0 && outputIndex + 1 < args.Length)
{
    var path = Path.GetFullPath(args[outputIndex + 1]);
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllText(path, JsonSerializer.Serialize(document, new JsonSerializerOptions { WriteIndented = true }));
}
Console.WriteLine($"{results.Count - failed}/{results.Count} passed");
if (results.Count == 0) Console.Error.WriteLine("No tests matched; this run did not validate the workflow.");
return failed == 0 && results.Count > 0 ? 0 : 1;

static IEnumerable<(string Name, Action Run)> ContractCases()
{
    yield return ("Brand metadata is available to every module", () =>
    {
        if (string.IsNullOrWhiteSpace(BrandInfo.ProducerCredit) || string.IsNullOrWhiteSpace(BrandInfo.ProductName))
            throw new InvalidOperationException("Missing central brand metadata.");
    });
    yield return ("All live game states block maintenance", () =>
    {
        foreach (var state in Enum.GetValues<AppLifecycleState>())
        {
            var game = new AppInstance(new(Guid.NewGuid()), new("test.game", null, null), true, state, new(1));
            if (game.BlocksMaintenance != (state is not AppLifecycleState.Closed and not AppLifecycleState.Crashed))
                throw new InvalidOperationException($"Incorrect game state {state}.");
        }
    });
}
