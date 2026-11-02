namespace VectorForge.Services;

public sealed class ManifestStore
{
    private readonly object _gate = new();
    private readonly string _path;
    private ExportManifest _manifest;
    public string StatePath => _path + ".state-v7.json";

    public ManifestStore(string? path = null)
    {
        AppPaths.EnsureCreated();
        _path = path ?? AppPaths.ManifestFile;
        foreach (var file in new[] { _path, StatePath })
            if (File.Exists(file) && !File.Exists(file + ".pre-v17.bak")) File.Copy(file, file + ".pre-v17.bak");
        if (File.Exists(_path) && !File.Exists(_path + ".pre-v7.bak"))
            File.Copy(_path, _path + ".pre-v7.bak");
        _manifest = AtomicJsonStore.Load(_path, () => new ExportManifest());
        _manifest.Entries = new Dictionary<string, ExportManifestEntry>(
            _manifest.Entries ?? new(), StringComparer.OrdinalIgnoreCase);
    }

    public ExportManifestEntry? Get(string sourcePath, ExportKind kind)
    {
        lock (_gate)
            return _manifest.Entries.GetValueOrDefault(OutputMatcher.ManifestKey(sourcePath, kind));
    }

    public IReadOnlyList<ExportManifestEntry> GetEntries(ExportKind kind)
    {
        lock (_gate)
            return _manifest.Entries.Values.Where(entry => entry.Kind == kind).ToArray();
    }

    public void Put(ExportManifestEntry entry)
    {
        lock (_gate)
        {
            var key = OutputMatcher.ManifestKey(entry.SourcePath, entry.Kind);
            var previous = _manifest.Entries.GetValueOrDefault(key);
            _manifest.Entries[key] = entry;
            try { AtomicJsonStore.Save(_path, _manifest); }
            catch
            {
                if (previous is null) _manifest.Entries.Remove(key); else _manifest.Entries[key] = previous;
                throw;
            }
        }
    }

    public void Remove(string sourcePath, ExportKind kind)
    {
        lock (_gate)
        {
            if (!_manifest.Entries.Remove(OutputMatcher.ManifestKey(sourcePath, kind))) return;
            AtomicJsonStore.Save(_path, _manifest);
        }
    }
}
