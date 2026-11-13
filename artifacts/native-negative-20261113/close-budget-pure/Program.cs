using System.Text.Json;
using AutumnOS.Tests;

var results = new List<object>();
int failed = 0;
foreach (var (name, run) in NativeOwnedJobPolicyTests.Cases())
{
    try { run(); results.Add(new { name, status = "passed" }); }
    catch (Exception error) { failed++; results.Add(new { name, status = "failed", error = error.GetType().Name }); }
}
Console.WriteLine(JsonSerializer.Serialize(new { scope = "pure helper only; no native owner or child started", passed = results.Count - failed, failed, results }, new JsonSerializerOptions { WriteIndented = true }));
return failed == 0 ? 0 : 1;
