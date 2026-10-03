namespace AutumnOS.Contracts;

/// <summary>Resolves the fixed single-file layout without trusting environment variables or a process working directory.</summary>
public static class PortableLayout
{
    public const string MarkerName = "autumn.portable";
    public const string Marker = "AutumnOS.single-file.layout.v1\n";

    public static string EntryDirectory(string programDirectory)
    {
        string program = Path.TrimEndingDirectorySeparator(Path.GetFullPath(programDirectory));
        string marker = Path.Combine(program, MarkerName);
        if (!File.Exists(marker)) return Path.GetFullPath(programDirectory);
        CheckPlain(marker);
        if (new FileInfo(marker).Length != System.Text.Encoding.UTF8.GetByteCount(Marker) || File.ReadAllText(marker) != Marker)
            throw new IOException("PORTABLE_LAYOUT_INVALID");
        var product = new DirectoryInfo(program);
        bool productName = product.Name == "Product" || product.Name.StartsWith("Product-", StringComparison.Ordinal) && product.Name.Length == 28 && product.Name[8..].All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
        if (!productName || product.Parent?.Name != "System" || product.Parent.Parent?.Name != "AutumnOS_Data" || product.Parent.Parent.Parent is null)
            throw new IOException("PORTABLE_LAYOUT_INVALID");
        return product.Parent.Parent.Parent.FullName;
    }

    public static string DataDirectory(string programDirectory) => Path.Combine(EntryDirectory(programDirectory), "AutumnOS_Data");

    private static void CheckPlain(string path)
    {
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("PORTABLE_REDIRECTED_PATH");
    }
}
