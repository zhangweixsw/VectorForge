namespace VectorForge.Services;

public static class FileSafety
{
    // Include the configured root, but not unrelated ancestors above that root.
    // Missing leaves are allowed for deletion reconciliation; unreadable paths are not.
    public static bool IsVisible(string path, string root)
    {
        try
        {
            var full = Path.GetFullPath(path);
            root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            if (!UnderRoot(full, root)) return false;
            while (true)
            {
                try { if ((File.GetAttributes(full) & (FileAttributes.Hidden | FileAttributes.ReparsePoint)) != 0) return false; }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
                if (string.Equals(Path.TrimEndingDirectorySeparator(full), root, StringComparison.OrdinalIgnoreCase)) return true;
                full = Path.GetDirectoryName(full)!;
                if (string.IsNullOrEmpty(full)) return false;
            }
        }
        catch { return false; }
    }

    public static bool IsVisibleOutput(string path) => IsVisible(path, Path.GetDirectoryName(path)!);

    public static bool IsPlainPath(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            while (!string.IsNullOrEmpty(full))
            {
                try { if ((File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0) return false; }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
                var parent = Path.GetDirectoryName(full);
                if (parent == full) break;
                full = parent;
            }
            return true;
        }
        catch { return false; }
    }

    public static bool UnderRoot(string path, string root)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path));
        return !Path.IsPathRooted(relative) && relative != ".." &&
            !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    public static void RequireOutput(string source, string output, ExportKind kind)
    {
        if (!source.EndsWith(".ai", StringComparison.OrdinalIgnoreCase) ||
            !OutputMatcher.IsRecognizedOutput(source, output, kind) ||
            !IsPlainPath(source) || !IsPlainPath(output))
            throw new IOException("拒绝操作不安全的输出路径：" + output);
        var otherSource = Path.ChangeExtension(output, ".ai");
        if (!string.Equals(Path.GetFullPath(otherSource), Path.GetFullPath(source), StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                File.GetAttributes(otherSource);
                throw new IOException("该输出可能属于另一 AI，保留：" + output);
            }
            catch (FileNotFoundException) { }
        }
    }

    // Missing is distinct from inaccessible: File.Exists alone cannot make this decision.
    public static bool ConfirmMissing(string source)
    {
        if (!IsPlainPath(source)) throw new IOException("源路径不可访问或包含链接");
        try { File.GetAttributes(source); return false; }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
        var directory = Path.GetDirectoryName(source)!;
        // Conservative: if its containing folder disappeared, leave any output alone.
        _ = Directory.GetFileSystemEntries(directory);
        try { File.GetAttributes(source); return false; }
        catch (FileNotFoundException) { return true; }
        catch (DirectoryNotFoundException) { return true; }
    }
}
