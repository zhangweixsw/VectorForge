using System.Collections.Concurrent;

namespace VectorForge.Services;

public sealed class ChangeMonitor : IDisposable
{
    private readonly Dictionary<string, FileSystemWatcher> _watchers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, long> _changed = new(StringComparer.OrdinalIgnoreCase);
    private long _version;
    private string[] _roots = [];
    private int _dirty;
    public void Configure(IEnumerable<string> roots)
    {
        var desired = roots.Where(Directory.Exists).Select(Path.GetFullPath)
            .Where(root => FileSafety.IsVisible(root, root))
            .Where(FileSafety.IsPlainPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Volatile.Write(ref _roots, desired.ToArray());
        foreach (var stale in _watchers.Keys.Except(desired, StringComparer.OrdinalIgnoreCase).ToArray())
        { _watchers[stale].Dispose(); _watchers.Remove(stale); }
        foreach (var root in desired.Where(r => !_watchers.ContainsKey(r)))
        {
            try
            {
                var watcher = new FileSystemWatcher(root) { IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.Attributes };
                watcher.Created += (_, e) => Mark(e.FullPath);
                watcher.Changed += (_, e) => Mark(e.FullPath);
                watcher.Deleted += (_, e) => Mark(e.FullPath);
                watcher.Renamed += (_, e) => { Mark(e.OldFullPath); Mark(e.FullPath); };
                watcher.Error += (_, _) => Interlocked.Exchange(ref _dirty, 1);
                watcher.EnableRaisingEvents = true;
                _watchers[root] = watcher;
            }
            catch { Interlocked.Exchange(ref _dirty, 1); }
        }
    }
    private void Mark(string path)
    {
        if (!Volatile.Read(ref _roots).Any(root => FileSafety.UnderRoot(path, root) && FileSafety.IsVisible(path, root))) return;
        var name = Path.GetFileName(path);
        if (path.Contains(".vectorforge-", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("vectorforge_temp_", StringComparison.OrdinalIgnoreCase)) return;
        if (Path.GetExtension(path).Equals(".ai", StringComparison.OrdinalIgnoreCase))
            _changed[path] = Interlocked.Increment(ref _version);
        else if (Directory.Exists(path) || !Path.HasExtension(path))
            _changed[path] = Interlocked.Increment(ref _version);
        else return;
        Interlocked.Exchange(ref _dirty, 1);
    }
    public bool ConsumeSignal() => Interlocked.Exchange(ref _dirty, 0) != 0;
    public Dictionary<string, long> Snapshot()
    {
        foreach (var path in _changed.Keys)
            if (!Volatile.Read(ref _roots).Any(root => FileSafety.UnderRoot(path, root) && FileSafety.IsVisible(path, root)))
                _changed.TryRemove(path, out _);
        return new(_changed, StringComparer.OrdinalIgnoreCase);
    }
    public void Acknowledge(string path, long version) =>
        ((ICollection<KeyValuePair<string, long>>)_changed).Remove(new(path, version));
    public void Dispose() { foreach (var watcher in _watchers.Values) watcher.Dispose(); }
}
