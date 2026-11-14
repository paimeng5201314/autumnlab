using System.Text.Json;
using AutumnOS.Tests;
var results = new List<object>(); int failed = 0;
foreach (var (name, run) in NativeBrowserCloseSequenceTests.Cases())
{
    try { run(); results.Add(new { name, status = "passed" }); }
    catch (Exception error) { failed++; results.Add(new { name, status = "failed", error = error.GetType().Name }); }
}
Console.WriteLine(JsonSerializer.Serialize(new { scope = "pure deterministic fault injection only; no native owner or GUI", passed = results.Count - failed, failed, results }, new JsonSerializerOptions { WriteIndented = true }));
return failed == 0 ? 0 : 1;
