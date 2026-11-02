using System.Text.RegularExpressions;

namespace VectorForge.Services;

public static class OutputMatcher
{
    public static string ManifestKey(string sourcePath, ExportKind kind) =>
        $"{NormalizePath(sourcePath)}|{kind}";

    public static string NormalizePath(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    public static bool ManifestIsCurrent(
        ExportManifestEntry entry,
        FileInfo source,
        ExportKind kind)
    {
        if (entry.Kind != kind ||
            entry.SourceLength != source.Length ||
            entry.SourceModifiedUtc != source.LastWriteTimeUtc ||
            entry.OutputFiles.Count != 1)
            return false;

        return entry.OutputFiles.All(path =>
        {
            var file = new FileInfo(path);
            // The manifest records the exact source version exported successfully.
            // Output timestamps may be rounded or rewritten by a sync service.
            return IsRecognizedOutput(source.FullName, path, kind) && file.Exists && file.Length > 0;
        });
    }

    public static IReadOnlyList<string> FindLegacyOutputs(FileInfo source, ExportKind kind)
    {
        return FindRecognizedOutputs(source, kind)
            .Where(path => IsCurrentNonEmpty(path, source.LastWriteTimeUtc))
            .ToArray();
    }

    public static IReadOnlyList<string> FindRecognizedOutputs(FileInfo source, ExportKind kind)
    {
        var directory = source.DirectoryName!;
        var pattern = BuildOutputNamePattern(Path.GetFileNameWithoutExtension(source.Name), kind);

        try
        {
            return Directory.EnumerateFiles(directory)
                .Where(path => pattern.IsMatch(Path.GetFileName(path)))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch { return []; }
    }

    public static bool IsRecognizedOutput(string sourcePath, string outputPath, ExportKind kind)
    {
        try
        {
            var sourceDirectory = NormalizePath(Path.GetDirectoryName(sourcePath)!);
            var outputDirectory = NormalizePath(Path.GetDirectoryName(outputPath)!);
            if (!string.Equals(sourceDirectory, outputDirectory, StringComparison.OrdinalIgnoreCase))
                return false;

            var baseName = Path.GetFileNameWithoutExtension(sourcePath);
            return BuildOutputNamePattern(baseName, kind).IsMatch(Path.GetFileName(outputPath));
        }
        catch
        {
            return false;
        }
    }

    public static bool IsRecognizedLegacyJpgName(string aiBaseName, string candidateName)
    {
        var alternatives = JpgBaseAlternatives(aiBaseName);
        return Regex.IsMatch(candidateName,
            $"^(?:{alternatives})(?:(?:[-_ ](?:画板)?\\d+)|(?:__AB\\d+))?\\.jpe?g$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static bool IsCurrentNonEmpty(string path, DateTime sourceModifiedUtc)
    {
        var file = new FileInfo(path);
        return file.Exists && file.Length > 0 && file.LastWriteTimeUtc >= sourceModifiedUtc;
    }

    private static Regex BuildOutputNamePattern(string baseName, ExportKind kind)
    {
        var escaped = kind == ExportKind.Jpg ? JpgBaseAlternatives(baseName) : Regex.Escape(baseName);
        var extension = kind == ExportKind.Pdf ? "pdf" : "jpe?g";
        return new Regex(
            $"^{escaped}(?:(?:[-_ ](?:画板)?\\d+)|(?:__AB\\d+))?\\.{extension}$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static string JpgBaseAlternatives(string baseName)
    {
        var original = Regex.Escape(baseName);
        var illustratorName = Regex.Escape(Regex.Replace(baseName.Trim(), @"\s+", "-"));
        return string.Equals(original, illustratorName, StringComparison.Ordinal)
            ? original
            : $"(?:{original}|{illustratorName})";
    }
}
