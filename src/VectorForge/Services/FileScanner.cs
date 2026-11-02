namespace VectorForge.Services;

public sealed class FileScanner(AppLogger logger)
{
    private sealed record StabilityObservation(FileSnapshot Snapshot, DateTime FirstSeenUtc);
    private readonly object _stabilityGate = new();
    private readonly Dictionary<string, StabilityObservation> _stability = new(StringComparer.OrdinalIgnoreCase);
    internal TimeSpan StabilityWindow { get; set; } = TimeSpan.FromSeconds(30);
    internal Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;

    internal static string? FindSkipKeyword(FileInfo source, MonitorJobSettings settings)
    {
        var fileName = Path.GetFileNameWithoutExtension(source.Name);
        return settings.SkipFileNameKeywords.FirstOrDefault(keyword =>
            fileName.Contains(keyword, StringComparison.OrdinalIgnoreCase));
    }

    public Task<IReadOnlyList<FileInfo>> FindStableCandidatesAsync(
        MonitorJobSettings settings,
        CancellationToken cancellationToken,
        Func<FileInfo, bool>? needsExport = null,
        IReadOnlySet<string>? changedPaths = null,
        bool pendingOnly = false)
    {
        _ = changedPaths;
        var roots = settings.RootPaths
            .Where(Directory.Exists)
            .Where(root => FileSafety.IsVisible(root, root))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (roots.Length == 0)
        {
            ClearKind(settings.Kind);
            return Task.FromResult<IReadOnlyList<FileInfo>>([]);
        }

        var now = UtcNow();
        var cutoff = now.AddHours(-settings.ModifiedWithinHours);
        // The selected range is a strict last-modified-time rule. Watcher events only make
        // the scan happen sooner; copying/moving a file or changing a directory must not
        // allow an older AI to bypass the configured 24-hour/day window.
        var sourceFiles = pendingOnly
            ? PendingPaths(settings.Kind)
                .Where(File.Exists)
                .Where(path => roots.Any(root => FileSafety.UnderRoot(path, root)))
                .Select(path => new FileInfo(path))
            : roots.SelectMany(EnumerateAiFiles);
        var possible = sourceFiles
            .Where(f => roots.Any(root => FileSafety.UnderRoot(f.FullName, root) && FileSafety.IsVisible(f.FullName, root)))
            .Where(f => IsWithinDateWindow(f, cutoff))
            .GroupBy(f => f.FullName, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(f => f.LastWriteTimeUtc)
            .ToArray();
        var observed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var stable = new List<FileInfo>();
        foreach (var source in possible)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (needsExport is not null && !needsExport(source)) continue;
                var key = StabilityKey(settings.Kind, source.FullName);
                observed.Add(key);
                var snapshot = Snapshot(source);
                lock (_stabilityGate)
                {
                    if (!_stability.TryGetValue(key, out var previous) || previous.Snapshot != snapshot)
                    {
                        _stability[key] = new(snapshot, now);
                        if (StabilityWindow <= TimeSpan.Zero) stable.Add(source);
                    }
                    else if (now - previous.FirstSeenUtc >= StabilityWindow)
                        stable.Add(source);
                }
            }
            catch (Exception ex)
            {
                logger.Error($"复查文件失败：{source.FullName}；{ex.Message}");
            }
        }
        lock (_stabilityGate)
        {
            var prefix = settings.Kind + "|";
            foreach (var stale in _stability.Keys
                .Where(key => key.StartsWith(prefix, StringComparison.Ordinal) && !observed.Contains(key)).ToArray())
                _stability.Remove(stale);
        }
        return Task.FromResult<IReadOnlyList<FileInfo>>(stable);
    }

    internal bool HasPendingStability(ExportKind kind)
    {
        lock (_stabilityGate)
            return _stability.Keys.Any(key => key.StartsWith(kind + "|", StringComparison.Ordinal));
    }

    internal int PendingStabilityCount(ExportKind kind)
    {
        lock (_stabilityGate)
            return _stability.Keys.Count(key => key.StartsWith(kind + "|", StringComparison.Ordinal));
    }

    private string[] PendingPaths(ExportKind kind)
    {
        lock (_stabilityGate)
        {
            var prefix = kind + "|";
            return _stability.Keys
                .Where(key => key.StartsWith(prefix, StringComparison.Ordinal))
                .Select(key => key[prefix.Length..])
                .ToArray();
        }
    }

    internal void AcknowledgeStability(ExportKind kind, string sourcePath)
    {
        lock (_stabilityGate) _stability.Remove(StabilityKey(kind, sourcePath));
    }

    internal void ResetStability(ExportKind kind) => ClearKind(kind);

    private void ClearKind(ExportKind kind)
    {
        lock (_stabilityGate)
            foreach (var key in _stability.Keys.Where(key => key.StartsWith(kind + "|", StringComparison.Ordinal)).ToArray())
                _stability.Remove(key);
    }

    private static string StabilityKey(ExportKind kind, string sourcePath) =>
        kind + "|" + Path.GetFullPath(sourcePath);

    internal static bool IsWithinDateWindow(FileInfo file, DateTime cutoffUtc) =>
        file.LastWriteTimeUtc >= cutoffUtc;

    private IEnumerable<FileInfo> EnumerateAiFiles(string root)
    {
        if (!FileSafety.IsPlainPath(root) || !FileSafety.IsVisible(root, root)) yield break;
        var pending = new Stack<string>();
        pending.Push(Path.GetFullPath(root));

        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if (!FileSafety.IsVisible(current, root)) continue;
            IEnumerable<string> files;
            IEnumerable<string> directories;
            try
            {
                files = Directory.EnumerateFiles(current, "*.ai", SearchOption.TopDirectoryOnly).ToArray();
                directories = Directory.EnumerateDirectories(current).ToArray();
            }
            catch (Exception ex)
            {
                logger.Error($"无法扫描目录：{current}；{ex.Message}");
                continue;
            }

            foreach (var file in files)
            {
                var name = Path.GetFileName(file);
                if (name.Contains(".vectorforge-temp-", StringComparison.OrdinalIgnoreCase) ||
                    name.StartsWith("vectorforge_temp_", StringComparison.OrdinalIgnoreCase))
                    continue;
                FileInfo info;
                try { info = new FileInfo(file); }
                catch { continue; }
                bool safe;
                try { safe = (info.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Hidden)) == 0; }
                catch { continue; }
                if (safe) yield return info;
            }

            foreach (var directory in directories)
            {
                try
                {
                    var info = new DirectoryInfo(directory);
                    if ((info.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Hidden)) == 0) pending.Push(directory);
                }
                catch (Exception ex)
                {
                    logger.Error($"无法读取子目录：{directory}；{ex.Message}");
                }
            }
        }
    }

    private static FileSnapshot Snapshot(FileInfo info)
    {
        info.Refresh();
        return new FileSnapshot(info.Length, info.LastWriteTimeUtc);
    }
}
