namespace AutumnOS.Update;

public static class UpdatePaths
{
    public static bool IsStableOrPrivate(string relative)
    {
        string first = relative.Replace('\\', '/').Split('/')[0];
        string leaf = relative.Replace('\\', '/').Split('/')[^1];
        return first.Equals("AutumnOS_Data", StringComparison.OrdinalIgnoreCase) || first.StartsWith(".autumnos", StringComparison.OrdinalIgnoreCase) ||
            first.Equals("AutumnOS.exe", StringComparison.OrdinalIgnoreCase) || first.Equals("AutumnOS.Updater.exe", StringComparison.OrdinalIgnoreCase) ||
            first.StartsWith("AutumnOS.Setup", StringComparison.OrdinalIgnoreCase) || first.Equals("AutumnOS.Uninstall.exe", StringComparison.OrdinalIgnoreCase) || first.StartsWith("Uninstall", StringComparison.OrdinalIgnoreCase) ||
            first.Equals("autumn.portable", StringComparison.OrdinalIgnoreCase) || first.Equals("autumn.install.json", StringComparison.OrdinalIgnoreCase) || first.Equals("autumn.update.json", StringComparison.OrdinalIgnoreCase) ||
            first.Equals("autumn.update.sig", StringComparison.OrdinalIgnoreCase) || first.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
            leaf.EndsWith(".pfx", StringComparison.OrdinalIgnoreCase) || leaf.EndsWith(".key", StringComparison.OrdinalIgnoreCase) || leaf.EndsWith(".pem", StringComparison.OrdinalIgnoreCase) ||
            leaf.EndsWith(".pk8", StringComparison.OrdinalIgnoreCase) || leaf.EndsWith(".p12", StringComparison.OrdinalIgnoreCase) || leaf.EndsWith(".dpapi", StringComparison.OrdinalIgnoreCase) || leaf.EndsWith(".snk", StringComparison.OrdinalIgnoreCase);
    }
    public static void ValidateManagedRelativePath(string path)
    {
        if (string.IsNullOrEmpty(path) || path.Length > 230 || path.Contains('\\') || path.StartsWith('/') || path.EndsWith('/') ||
            path.Any(c => c < 32 || c is ':' or '*' or '?' or '"' or '<' or '>' or '|') || IsStableOrPrivate(path)) throw new UpdateException("UPDATE_PATH_NOT_MANAGED");
        foreach (string part in path.Split('/'))
        {
            string stem = part.Split('.')[0].ToUpperInvariant();
            if (part is "" or "." or ".." || part.EndsWith(' ') || part.EndsWith('.') || part.Length > 200 ||
                stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" ||
                stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) && (char.IsAsciiDigit(stem[3]) || "¹²³".Contains(stem[3])))
                throw new UpdateException("UPDATE_PATH_NOT_MANAGED");
        }
    }
    public static string ResolveManagedPath(string root, string relative)
    {
        ValidateManagedRelativePath(relative);
        string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string path = Path.GetFullPath(Path.Combine(fullRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)) throw new UpdateException("UPDATE_PATH_ESCAPE");
        EnsureNoReparsePoints(path); return path;
    }
    public static void EnsureNoReparsePoints(string path)
    {
        for (string? p = Path.GetFullPath(path); p is not null; p = Path.GetDirectoryName(p))
        {
            try { if ((File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0) throw new UpdateException("UPDATE_REPARSE_POINT"); }
            catch (FileNotFoundException) { } catch (DirectoryNotFoundException) { }
        }
    }
}
