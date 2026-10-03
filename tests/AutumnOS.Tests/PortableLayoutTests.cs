using AutumnOS.Contracts;
using AutumnOS.Storage;
using AutumnOS.UpdateProtocol;

namespace AutumnOS.Tests;

public static class PortableLayoutTests
{
    public static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return ("portable.same_adjacent_data_survives_runtime_relocation", PreservesData);
        yield return ("portable.marker_cannot_select_an_arbitrary_root", RejectsArbitraryRoot);
        yield return ("portable.corrupt_marker_never_creates_a_nested_data_root", RejectsCorruptMarker);
        if (OperatingSystem.IsWindows()) yield return ("portable.update_handoff_uses_actual_data_and_protects_layout", UpdateDataBoundary);
    }

    private static void InScope(Action<string, string> run)
    {
        string entry = Path.Combine(Path.GetTempPath(), "AutumnOS-portable-layout-" + Guid.NewGuid().ToString("N"));
        string product = Path.Combine(entry, "AutumnOS_Data", "System", "Product");
        Directory.CreateDirectory(product);
        try { run(entry, product); }
        finally { Directory.Delete(entry, true); }
    }

    private static void PreservesData() => InScope((entry, product) =>
    {
        File.WriteAllText(Path.Combine(product, PortableLayout.MarkerName), PortableLayout.Marker);
        string saves = Path.Combine(entry, "AutumnOS_Data", "Saves");
        Directory.CreateDirectory(saves);
        string save = Path.Combine(saves, "owned-existing-save.bin");
        File.WriteAllBytes(save, [0, 12, 255]);
        var root = new InstallationRoot(product);
        if (root.InstallationDirectory != entry || root.ProgramDirectory != product || root.DataDirectory != Path.Combine(entry, "AutumnOS_Data") || !root.EnsureCreated().Success)
            throw new InvalidOperationException("Portable program and user data must use separate validated roots.");
        if (!File.ReadAllBytes(save).SequenceEqual(new byte[] { 0, 12, 255 }) || Directory.Exists(Path.Combine(product, "AutumnOS_Data")))
            throw new InvalidOperationException("Existing save altered or nested data created.");
    });

    private static void RejectsArbitraryRoot() => InScope((entry, unusedProduct) =>
    {
        File.WriteAllText(Path.Combine(entry, PortableLayout.MarkerName), PortableLayout.Marker);
        try { _ = new InstallationRoot(entry); }
        catch (IOException e) when (e.Message == "PORTABLE_LAYOUT_INVALID") { return; }
        throw new InvalidOperationException("A marker outside the fixed layout must fail closed.");
    });

    private static void RejectsCorruptMarker() => InScope((unusedEntry, product) =>
    {
        File.WriteAllText(Path.Combine(product, PortableLayout.MarkerName), "invalid");
        try { _ = new InstallationRoot(product); }
        catch (IOException e) when (e.Message == "PORTABLE_LAYOUT_INVALID")
        {
            if (Directory.Exists(Path.Combine(product, "AutumnOS_Data"))) throw new InvalidOperationException("Created data for invalid layout.");
            return;
        }
        throw new InvalidOperationException("Invalid marker must fail closed.");
    });

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void UpdateDataBoundary() => InScope((entry, product) =>
    {
        File.WriteAllText(Path.Combine(product, PortableLayout.MarkerName), PortableLayout.Marker);
        if (UpdateFiles.DataRoot(product) != Path.Combine(entry, "AutumnOS_Data")) throw new InvalidOperationException("Wrong update data boundary.");
        try { _ = UpdateFiles.Managed(product, PortableLayout.MarkerName); }
        catch (IOException e) when (e.Message == "UPDATE_STABLE_FILE_FORBIDDEN") { return; }
        throw new InvalidOperationException("Update must not overwrite the portable layout boundary.");
    });
}
