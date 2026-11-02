using System.Security.Cryptography;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace VectorForge.Services;

public sealed class MonitorCoordinator : IDisposable
{
    private sealed record QueuedExport(string Path, ExportKind Kind, FileSnapshot Version, bool Force);
    private readonly AppSettings _settings;
    private readonly FileScanner _scanner;
    private readonly ManifestStore _manifest;
    private readonly IIllustratorExporter _exporter;
    private readonly AppLogger _logger;
    private readonly OutputValidator _validator;
    private readonly MonitorState _state;
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<ExportKind, int> _commands = new();
    private readonly Dictionary<ExportKind, ChangeMonitor> _changes = new()
        { [ExportKind.Pdf] = new(), [ExportKind.Jpg] = new() };
    private readonly Dictionary<ExportKind, DateTimeOffset> _lastScan = new();
    private readonly Dictionary<string, QueuedExport> _ready = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<ExportKind> _forcing = [];
    private readonly Dictionary<ExportKind, CancellationTokenSource> _jobCancellations = new()
        { [ExportKind.Pdf] = new(), [ExportKind.Jpg] = new() };
    private readonly Dictionary<ExportKind, ScanStatistics> _totals = new()
        { [ExportKind.Pdf] = new(), [ExportKind.Jpg] = new() };
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private Task? _dispatchLoop;
    private QueuedExport? _active;
    private ExportKind _nextKind = ExportKind.Pdf;
    private bool _sharedBlocked;
    private DateTimeOffset _sessionNextCheck;
    private string _sessionMessage = "";
    internal Func<DateTimeOffset> Now { get; set; } = () => DateTimeOffset.UtcNow;
    public bool IsRunning => _cts is { IsCancellationRequested: false };
    public event EventHandler<JobStatus>? StatusChanged;
    public event EventHandler<ProcessingRecord>? RecordAdded;

    public Task<IReadOnlyList<string>> ReadPdfPresetsAsync(CancellationToken token = default) =>
        _exporter is WorkerProcessIllustratorExporter worker
            ? worker.ReadPdfPresetsAsync(Snapshot().SessionMode, token)
            : Task.FromResult<IReadOnlyList<string>>([]);

    public MonitorCoordinator(AppSettings settings, AppLogger logger,
        IIllustratorExporter? exporter = null, ManifestStore? manifest = null)
    {
        _settings = settings; _logger = logger; _scanner = new(logger);
        _manifest = manifest ?? new ManifestStore();
        _state = AtomicJsonStore.Load(_manifest.StatePath, () => new MonitorState());
        _state.Failures = new(_state.Failures ?? [], StringComparer.OrdinalIgnoreCase);
        _state.OpenFailures = new(_state.OpenFailures ?? [], StringComparer.OrdinalIgnoreCase);
        _state.FailureNotes = new(_state.FailureNotes ?? [], StringComparer.OrdinalIgnoreCase);
        _state.MissingSince = new(_state.MissingSince ?? [], StringComparer.OrdinalIgnoreCase);
        _state.Cleanup = new(_state.Cleanup ?? [], StringComparer.OrdinalIgnoreCase);
        _state.OutputChecks = new(_state.OutputChecks ?? [], StringComparer.OrdinalIgnoreCase);
        _state.Abandoned ??= [];
        foreach (var (key, failure) in _state.Failures)
        {
            var delimiter = key.LastIndexOf('|');
            if (delimiter < 0) continue;
            failure.SourcePath = key[..delimiter];
            if (Enum.TryParse<ExportKind>(key[(delimiter + 1)..], out var kind)) failure.Kind = kind;
            if (!string.IsNullOrEmpty(failure.NotePath)) _state.FailureNotes[key] = failure.NotePath;
        }
        _validator = new(_state.OutputChecks);
        _exporter = exporter ?? new WorkerProcessIllustratorExporter(logger);
        if (_exporter is WorkerProcessIllustratorExporter worker)
            worker.Progress += text =>
            {
                // Never let a slow directory scan block the worker's deadline watchdog.
                if (Volatile.Read(ref _active) is { } task) Publish(task.Kind, "处理中", 0,
                    Path.GetFileName(task.Path) + " · " + text);
            };
    }

    private AppSettings Snapshot()
    { lock (_settings) return AtomicJsonStore.Deserialize<AppSettings>(AtomicJsonStore.Serialize(_settings))!; }

    public void Start()
    {
        if (IsRunning) return;
        _cts = new();
        _loop = Task.Run(() => ScanLoopAsync(_cts.Token));
        _dispatchLoop = Task.Run(() => DispatchLoopAsync(_cts.Token));
    }

    public async Task PauseAsync()
    {
        if (_cts is null) return;
        _cts.Cancel();
        try { await Task.WhenAll(_loop ?? Task.CompletedTask, _dispatchLoop ?? Task.CompletedTask); }
        catch (OperationCanceledException) { }
        _cts.Dispose(); _cts = null;
        lock (_gate) { _ready.Clear(); _forcing.Clear(); }
        if (_exporter is WorkerProcessIllustratorExporter worker) await worker.CloseAsync();
        foreach (var kind in Enum.GetValues<ExportKind>()) Publish(kind, "已暂停", 0, "监控已暂停");
    }

    public void RunNow(ExportKind kind) => Command(kind, 1);
    public void RetryFailures(ExportKind kind) => Command(kind, 2);
    public void Reexport(ExportKind kind) => Command(kind, 3);
    public void StopJob(ExportKind kind)
    {
        ResetJob(kind);
        _commands.TryRemove(kind, out _);
        Publish(kind, "已停止", 0, "任务已停止；重新启用前不会继续导出");
    }
    public void RefreshJob(ExportKind kind) { ResetJob(kind); RunNow(kind); }
    private void ResetJob(ExportKind kind)
    {
        CancellationTokenSource old;
        lock (_gate)
        {
            old = _jobCancellations[kind]; _jobCancellations[kind] = new();
            foreach (var key in _ready.Where(p => p.Value.Kind == kind).Select(p => p.Key).ToArray()) _ready.Remove(key);
            _forcing.Remove(kind); _scanner.ResetStability(kind);
        }
        old.Cancel(); old.Dispose();
    }
    private void Command(ExportKind kind, int command) =>
        _commands.AddOrUpdate(kind, command, (_, old) => Math.Max(old, command));

    private async Task ScanLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var settings = Snapshot();
            foreach (var job in new[] { settings.PdfJob, settings.JpgJob })
            {
                try
                {
                    _changes[job.Kind].Configure(job.Enabled ? job.RootPaths : []);
                    Scan(job, token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception ex) { Record(job.Kind, "", "扫描异常：" + ex.Message); }
            }
            await Task.Delay(TimeSpan.FromSeconds(2), token);
        }
    }

    private void Scan(MonitorJobSettings job, CancellationToken token)
    {
        if (!job.Enabled) return;
        CancellationToken jobToken;
        lock (_gate) jobToken = _jobCancellations[job.Kind].Token;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, jobToken);
        token = cancellation.Token;
        _commands.TryRemove(job.Kind, out var command);
        lock (_gate)
        {
        if (command >= 2)
        {
            foreach (var key in _state.Failures.Where(p => p.Value.Kind == job.Kind && Eligible(new(p.Value.SourcePath), job)).Select(p => p.Key).ToArray()) _state.Failures.Remove(key);
            foreach (var key in _state.OpenFailures.Where(p => Eligible(new(p.Value.SourcePath), job)).Select(p => p.Key).ToArray()) _state.OpenFailures.Remove(key);
            _sharedBlocked = false; _sessionNextCheck = DateTimeOffset.MinValue;
            _sessionMessage = "";
            if (_exporter is WorkerProcessIllustratorExporter worker) worker.AllowRecoveryCheck();
            SaveState();
        }
        if (command == 3) _forcing.Add(job.Kind);
        }
        var changeSignal = _changes[job.Kind].ConsumeSignal();
        var pending = _scanner.HasPendingStability(job.Kind);
        var withinInterval = _lastScan.TryGetValue(job.Kind, out var last) && Now() - last < TimeSpan.FromSeconds(job.ScanIntervalSeconds);
        if (command == 0 && !changeSignal && !pending && withinInterval) return;
        var stats = new ScanStatistics();
        try
        {
            job.RootPaths = job.RootPaths.Where(root =>
            {
                try
                {
                    if (!FileSafety.IsPlainPath(root) || !FileSafety.IsVisible(root, root))
                    {
                        lock (_gate)
                            foreach (var entry in _manifest.GetEntries(job.Kind))
                                if (FileSafety.UnderRoot(entry.SourcePath, root)) _state.MissingSince.Remove(Key(entry.SourcePath, job.Kind));
                        return false;
                    }
                    _ = Directory.GetFileSystemEntries(root); return true;
                }
                catch (Exception ex)
                {
                    Record(job.Kind, root, "目录暂不可用：" + ex.Message);
                    lock (_gate)
                    foreach (var entry in _manifest.GetEntries(job.Kind))
                        if (FileSafety.UnderRoot(entry.SourcePath, root)) _state.MissingSince.Remove(Key(entry.SourcePath, job.Kind));
                    return false;
                }
            }).ToList();
            lock (_gate) CleanupAbandoned();
            if (job.RootPaths.Count == 0)
            { PublishUnlessActive(job.Kind, "等待目录恢复", 0, "没有可访问且可见的监控路径"); return; }
            lock (_gate)
            {
                stats.Deleted = ReconcileDeletedSources(job); _totals[job.Kind].Deleted += stats.Deleted;
                RetryCleanup(job);
            }
            var changed = _changes[job.Kind].Snapshot();
            bool force;
            lock (_gate) force = _forcing.Contains(job.Kind);
            var candidates = _scanner.FindStableCandidatesAsync(job, token, source =>
            {
                lock (_gate)
                {
                stats.Scanned++;
                var key = Key(source.FullName, job.Kind);
                var keyword = FileScanner.FindSkipKeyword(source, job);
                if (keyword is not null)
                { stats.Skipped++; Record(job.Kind, source.FullName, $"文件名包含跳过关键词“{keyword}”，已跳过"); return false; }
                if (!Eligible(source, job) || _active is { } active && Key(active.Path, active.Kind) == key) return false;
                if (_ready.TryGetValue(key, out var queued) && queued.Version == Version(source)) return false;
                var need = NeedsExport(source, job.Kind, force, true);
                if (!need) { stats.Skipped++; if (changed.TryGetValue(source.FullName, out var v)) _changes[job.Kind].Acknowledge(source.FullName, v); }
                return need;
                }
            }, null, command == 0 && !changeSignal && pending && withinInterval).GetAwaiter().GetResult();
            token.ThrowIfCancellationRequested();
            lock (_gate)
            {
            foreach (var source in candidates)
            {
                _ready[Key(source.FullName, job.Kind)] = new(source.FullName, job.Kind, Version(source), force);
                _scanner.AcknowledgeStability(job.Kind, source.FullName);
            }
            if (!_scanner.HasPendingStability(job.Kind)) _forcing.Remove(job.Kind);
            foreach (var failure in _state.Failures.Values.Concat(_state.OpenFailures.Values).Where(f => f.Attempts >= 3).ToArray())
                if (failure.Kind == job.Kind && Eligible(new(failure.SourcePath), job)) EnsureFailureNotes(failure, failure.Stage == "Opening");
            var readyCount = _ready.Values.Count(q => q.Kind == job.Kind);
            var deferredCount = _state.Failures.Values.Count(f => f.Kind == job.Kind && f.Attempts < 3) + _state.OpenFailures.Values.Count(f => f.Attempts < 3);
            PublishUnlessActive(job.Kind, _sharedBlocked ? "导出暂停，扫描继续" : _sessionMessage.Length > 0 ? "等待 Illustrator" : "监控中", readyCount,
                (_sessionMessage.Length > 0 ? _sessionMessage + "；" : "") + $"{stats}；就绪 {readyCount} · 等待稳定 {_scanner.PendingStabilityCount(job.Kind)} · 延后 {deferredCount} · 累计成功 {_totals[job.Kind].Succeeded} / 失败 {_totals[job.Kind].Failed}");
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        finally { lock (_gate) { _lastScan[job.Kind] = Now(); SaveState(); } }
    }

    private async Task DispatchLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await DispatchOnceAsync(token);
                if (_exporter is WorkerProcessIllustratorExporter worker) await worker.CloseIfIdleAsync();
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex) { _logger.Error("导出队列异常：" + ex.Message); }
            await Task.Delay(500, token);
        }
    }

    private async Task<bool> DispatchOnceAsync(CancellationToken token, ExportKind? onlyKind = null)
    {
        QueuedExport? selected;
        MonitorJobSettings job;
        CancellationTokenSource linked;
        lock (_gate)
        {
            if (_sharedBlocked || Now() < _sessionNextCheck) return false;
            var settings = Snapshot();
            foreach (var pair in _ready.ToArray())
            {
                var source = new FileInfo(pair.Value.Path);
                var live = pair.Value.Kind == ExportKind.Pdf ? settings.PdfJob : settings.JpgJob;
                if (!Eligible(source, live) || Version(source) != pair.Value.Version || GetFailure(source, pair.Value.Kind) is not null)
                    _ready.Remove(pair.Key);
            }
            selected = _ready.Values.Where(q => onlyKind is null || q.Kind == onlyKind)
                .OrderBy(q => q.Kind == _nextKind ? 0 : 1).ThenBy(q => q.Version.ModifiedUtc).FirstOrDefault();
            if (selected is null)
            {
                // Both ordinary queues have priority over delayed retries.
                if (_ready.Count > 0) return false;
                foreach (var failure in _state.OpenFailures.Values.Concat(_state.Failures.Values)
                    .Where(f => f.Attempts < 3 && f.NextAttemptUtc <= Now()).OrderBy(f => f.NextAttemptUtc).ToArray())
                {
                    foreach (var kind in failure.Stage == "Opening" ? new[] { failure.Kind, Other(failure.Kind) } : new[] { failure.Kind })
                    {
                        if (onlyKind is not null && kind != onlyKind) continue;
                        var candidateJob = kind == ExportKind.Pdf ? settings.PdfJob : settings.JpgJob;
                        var source = new FileInfo(failure.SourcePath);
                        if (!Eligible(source, candidateJob) || Version(source) != new FileSnapshot(failure.Length, failure.ModifiedUtc)) continue;
                        if (!NeedsExport(source, kind, bypassFailures: true)) continue;
                        selected = new(source.FullName, kind, Version(source), false); break;
                    }
                    if (selected is not null) break;
                }
            }
            if (selected is null) return false;
            _ready.Remove(Key(selected.Path, selected.Kind));
            job = selected.Kind == ExportKind.Pdf ? settings.PdfJob : settings.JpgJob;
            if (!Eligible(new(selected.Path), job) || !NeedsExport(new(selected.Path), selected.Kind, selected.Force, bypassFailures: true)) return false;
            _active = selected; _nextKind = Other(selected.Kind);
            linked = CancellationTokenSource.CreateLinkedTokenSource(token, _jobCancellations[selected.Kind].Token);
        }
        using (linked)
        {
            try { await ExportWithRetriesAsync(new(selected.Path), job, linked.Token); }
            catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
            finally
            {
                lock (_gate)
                {
                    _active = null; RunNow(selected.Kind); SaveState();
                }
            }
        }
        return true;
    }

    private bool Eligible(FileInfo source, MonitorJobSettings job)
    {
        try
        {
            source.Refresh();
            return job.Enabled && source.Exists && !source.Name.StartsWith("vectorforge_temp_", StringComparison.OrdinalIgnoreCase) &&
                FileSafety.IsPlainPath(source.FullName) && FileScanner.IsWithinDateWindow(source, Now().UtcDateTime.AddHours(-job.ModifiedWithinHours)) &&
                job.RootPaths.Any(root => Directory.Exists(root) && FileSafety.UnderRoot(source.FullName, root) && FileSafety.IsVisible(source.FullName, root)) &&
                FileScanner.FindSkipKeyword(source, job) is null;
        }
        catch { return false; }
    }
    private bool InCurrentScope(FileInfo source, ExportKind kind) => Eligible(source, kind == ExportKind.Pdf ? Snapshot().PdfJob : Snapshot().JpgJob);
    private static string Key(string source, ExportKind kind) => OutputMatcher.ManifestKey(source, kind);
    private static FileSnapshot Version(FileInfo file) => new(file.Length, file.LastWriteTimeUtc);
    private static ExportKind Other(ExportKind kind) => kind == ExportKind.Pdf ? ExportKind.Jpg : ExportKind.Pdf;

    private FailureState? GetFailure(FileInfo source, ExportKind kind)
    {
        if (_state.OpenFailures.TryGetValue(source.FullName, out var open))
        {
            if (Version(source) == new FileSnapshot(open.Length, open.ModifiedUtc)) return open;
            _state.OpenFailures.Remove(source.FullName);
        }
        var key = Key(source.FullName, kind);
        if (_state.Failures.TryGetValue(key, out var failure))
        {
            if (Version(source) == new FileSnapshot(failure.Length, failure.ModifiedUtc)) return failure;
            _state.Failures.Remove(key);
        }
        return null;
    }

    private bool NeedsExport(FileInfo source, ExportKind kind, bool force = false, bool explain = false, bool bypassFailures = false)
    {
        source.Refresh();
        if (!source.Exists || !FileSafety.IsPlainPath(source.FullName) || !FileSafety.IsVisibleOutput(source.FullName)) return false;
        var entry = _manifest.Get(source.FullName, kind);
        var canonical = Path.ChangeExtension(source.FullName, kind == ExportKind.Pdf ? ".pdf" : ".jpg");
        if (!FileSafety.IsVisibleOutput(canonical) || (entry?.OutputFiles ?? []).Any(p => !FileSafety.IsVisibleOutput(p)))
        { if (explain) Record(kind, source.FullName, "对应输出已隐藏或不可访问，跳过处理"); return false; }
        if (force) return true;
        var desiredPreset = kind == ExportKind.Pdf ? Snapshot().PdfJob.PdfPreset : "";
        var presetChanged = kind == ExportKind.Pdf && entry is not null && !string.Equals(entry.PdfPreset ?? "", desiredPreset, StringComparison.Ordinal);
        if (entry is not null && entry.SourceLength == source.Length && entry.SourceModifiedUtc == source.LastWriteTimeUtc &&
            !presetChanged && entry.OutputFiles.Count == 1)
        {
            FileSafety.RequireOutput(source.FullName, entry.OutputFiles[0], kind);
            var health = _validator.Check(entry.OutputFiles[0], kind);
            if (health != OutputHealth.Invalid)
            {
                if (health == OutputHealth.Valid) ClearFailure(source.FullName, kind, false);
                if (explain) Record(kind, source.FullName, health == OutputHealth.Valid ? "未修改，已跳过" : "输出暂不可读，稍后检查");
                return false;
            }
        }
        if (!presetChanged && (kind != ExportKind.Pdf || string.IsNullOrEmpty(desiredPreset)))
        {
            foreach (var output in OutputMatcher.FindRecognizedOutputs(source, kind).Select(p => new FileInfo(p))
                .Where(f => f.Exists && f.Length > 0 && f.LastWriteTimeUtc >= source.LastWriteTimeUtc)
                .OrderByDescending(f => string.Equals(f.FullName, canonical, StringComparison.OrdinalIgnoreCase)))
            {
                if (!FileSafety.IsVisibleOutput(output.FullName)) return false;
                FileSafety.RequireOutput(source.FullName, output.FullName, kind);
                var health = _validator.Check(output.FullName, kind);
                if (health == OutputHealth.Invalid) continue;
                if (health == OutputHealth.Valid)
                {
                    _manifest.Put(new ExportManifestEntry { SourcePath = source.FullName, Kind = kind,
                        SourceLength = source.Length, SourceModifiedUtc = source.LastWriteTimeUtc, ArtboardCount = 1,
                        OutputFiles = [output.FullName], LastSuccessUtc = Now(), PdfPreset = desiredPreset });
                    ClearFailure(source.FullName, kind, false);
                }
                if (explain) Record(kind, source.FullName, "旁边已有最新输出，已跳过");
                return false;
            }
        }
        var failed = GetFailure(source, kind);
        if (failed is not null && !bypassFailures)
        {
            if (explain) Record(kind, source.FullName, failed.Attempts >= 3 ? "三次失败，已挂起" : $"延后重试，第 {failed.Attempts + 1}/3 次最早 {failed.NextAttemptUtc.ToLocalTime():HH:mm:ss}，空闲时执行");
            return false;
        }
        if (explain) Record(kind, source.FullName, presetChanged ? "PDF 预设变化，按新预设导出" : "新增文件、源更新或无有效输出");
        return true;
    }

    // One physical attempt per call. Scheduling, rather than sleeping here, owns retries.
    private async Task<bool> ExportWithRetriesAsync(FileInfo source, MonitorJobSettings job, CancellationToken token)
    {
        source.Refresh();
        if (!source.Exists || !FileSafety.IsPlainPath(source.FullName)) return false;
        var initial = Version(source);
        FailureState? previous;
        lock (_gate) previous = GetFailure(source, job.Kind);
        var id = Guid.NewGuid();
        var working = Path.Combine(source.DirectoryName!, "vectorforge_temp_source_" + id.ToString("N") + ".ai");
        var prefix = Path.Combine(source.DirectoryName!, "vectorforge_temp_output_" + id.ToString("N"));
        var temporary = prefix + (job.Kind == ExportKind.Pdf ? ".pdf" : ".jpg");
        var progress = Path.Combine(AppPaths.WorkerDirectory, id.ToString("N") + ".stage");
        var workingCreated = false;
        var submitted = false;
        var safeCleanup = true;
        var stage = "Preparing";
        try
        {
            token.ThrowIfCancellationRequested();
            source.Refresh();
            if (!source.Exists || initial != Version(source)) return false;
            using var sourceLock = new FileStream(source.FullName, FileMode.Open, FileAccess.Read, FileShare.Read);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(sourceLock, token));
            sourceLock.Position = 0;
            await using (var copy = new FileStream(working, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { workingCreated = true; await sourceLock.CopyToAsync(copy, token); }
            sourceLock.Dispose();
            var settings = Snapshot();
            var request = new ExportRequest { JobId = id, Kind = job.Kind, SourcePath = working, WorkingPrefix = prefix,
                OwnershipFile = "", ProgressPath = progress, SessionMode = settings.SessionMode,
                JpgDpi = job.JpgDpi, JpgSizing = job.JpgSizing, JpgPixelWidth = job.JpgPixelWidth,
                JpgQuality = job.JpgQuality, PdfPreset = job.PdfPreset, Attempt = (previous?.Attempts ?? 0) + 1 };
            if (!InCurrentScope(source, job.Kind)) return false;
            Publish(job.Kind, "处理中", 0, "准备导出：" + source.Name);
            submitted = true;
            var result = await _exporter.ExportAsync(request, TimeSpan.FromMinutes(job.TimeoutMinutes), token);
            safeCleanup = result.SafeToCleanup; stage = result.Stage;
            if (!result.Success)
            {
                lock (_gate)
                {
                    if (result.ErrorCode is "WAITING_USER" or "USER_TAKEOVER" or "OWNERSHIP_AMBIGUOUS" or "SESSION_UNAVAILABLE" or "RECOVERY_BLOCKED")
                    {
                        if (result.ErrorCode == "RECOVERY_BLOCKED") _sharedBlocked = true;
                        _sessionMessage = result.ErrorMessage ?? "会话尚未就绪";
                        _sessionNextCheck = Now().AddSeconds(result.ErrorCode == "SESSION_UNAVAILABLE" ? 60 : 10);
                        if (Eligible(source, job)) _ready[Key(source.FullName, job.Kind)] = new(source.FullName, job.Kind, initial, false);
                        Publish(job.Kind, "等待 Illustrator", _ready.Count, result.ErrorMessage ?? "会话尚未就绪");
                        return false;
                    }
                    if (result.SharedInstance && !result.SafeToCleanup)
                    { _sharedBlocked = true; _sessionMessage = "共享 Illustrator 异常，请恢复后点击重试失败项；扫描继续"; }
                }
                throw new IOException(result.ErrorCode + "：" + result.ErrorMessage);
            }
            if (result.TemporaryOutputs.Count != 1 || !string.Equals(Path.GetFullPath(result.TemporaryOutputs[0]), temporary, StringComparison.OrdinalIgnoreCase) ||
                !FileSafety.IsPlainPath(temporary)) throw new IOException("输出路径不符合本任务约定");
            stage = "Validating";
            if (new OutputValidator().Check(temporary, job.Kind) != OutputHealth.Valid) throw new IOException("输出完整性验证失败");
            source.Refresh();
            using var verify = new FileStream(source.FullName, FileMode.Open, FileAccess.Read, FileShare.Read);
            var afterHash = Convert.ToHexString(await SHA256.HashDataAsync(verify, token));
            if (hash != afterHash || initial != Version(source))
            { Record(job.Kind, source.FullName, "源文件变化，等待稳定后重新检查"); return false; }
            lock (_gate)
            {
                token.ThrowIfCancellationRequested();
                if (!InCurrentScope(source, job.Kind)) return false;
                var destination = Path.ChangeExtension(source.FullName, job.Kind == ExportKind.Pdf ? ".pdf" : ".jpg");
                if (!FileSafety.IsVisibleOutput(destination)) return false;
                FileSafety.RequireOutput(source.FullName, destination, job.Kind);
                var old = _manifest.Get(source.FullName, job.Kind);
                var stale = (old?.OutputFiles ?? []).Concat(OutputMatcher.FindRecognizedOutputs(source, job.Kind))
                    .Where(p => !string.Equals(p, destination, StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                var backup = destination + ".vectorforge-backup-" + id.ToString("N");
                if (File.Exists(destination)) File.Replace(temporary, destination, backup); else File.Move(temporary, destination);
                try { _manifest.Put(new ExportManifestEntry { SourcePath = source.FullName, Kind = job.Kind,
                    SourceLength = initial.Length, SourceModifiedUtc = initial.ModifiedUtc, SourceSha256 = hash,
                    ArtboardCount = result.ArtboardCount, OutputFiles = [destination], LastSuccessUtc = Now(),
                    JpgDpi = job.JpgDpi, JpgSizing = job.JpgSizing, JpgPixelWidth = job.JpgPixelWidth,
                    JpgQuality = job.JpgQuality, PdfPreset = job.PdfPreset }); }
                catch { if (File.Exists(backup)) File.Move(backup, destination, true); else File.Delete(destination); throw; }
                try { if (File.Exists(backup)) File.Delete(backup); } catch { stale.Add(backup); }
                if (stale.Count > 0) _state.Cleanup[Key(source.FullName, job.Kind)] = stale;
                ClearFailure(source.FullName, job.Kind, true);
                _sessionMessage = "";
                RetryCleanup(job); _totals[job.Kind].Succeeded++;
                Record(job.Kind, source.FullName, "导出成功"); SaveState(); return true;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (IOException ex) when ((ex.HResult & 0xFFFF) is 32 or 33)
        { Record(job.Kind, source.FullName, "文件被占用，延后处理"); return false; }
        catch (Exception ex)
        {
            lock (_gate)
            {
                source.Refresh();
                if (source.Exists && Version(source) == initial && InCurrentScope(source, job.Kind))
                    StoreFailure(source, job.Kind, initial, previous, stage, ex.Message);
            }
            return false;
        }
        finally
        {
            if (submitted && _exporter is WorkerProcessIllustratorExporter worker) safeCleanup &= worker.LastCleanupSafe;
            var paths = (workingCreated ? new[] { working, temporary, progress } : new[] { temporary, progress }).ToList();
            if (safeCleanup)
            {
                foreach (var path in paths) try { if (FileSafety.IsPlainPath(path) && File.Exists(path)) File.Delete(path); }
                    catch (Exception ex) { _logger.Error("临时文件保留：" + path + " " + ex.Message); }
            }
            else lock (_gate)
            {
                _state.Abandoned.Add(new() { Paths = paths,
                    Blockers = _exporter is WorkerProcessIllustratorExporter worker2 ? worker2.LastBlockers.ToList() : [] });
                SaveState();
            }
        }
    }

    private void StoreFailure(FileInfo source, ExportKind kind, FileSnapshot version, FailureState? previous, string stage, string message)
    {
        var failure = new FailureState { SourcePath = source.FullName, Kind = kind, Length = version.Length, ModifiedUtc = version.ModifiedUtc,
            Attempts = (previous?.Attempts ?? 0) + 1, Stage = stage, Errors = previous?.Errors.ToList() ?? [] };
        failure.Errors.Add($"{Now().ToLocalTime():yyyy-MM-dd HH:mm:ss zzz} [{FailureNote.StageLabel(stage)}] {message}");
        failure.NextAttemptUtc = Now().AddMinutes(failure.Attempts == 1 ? 5 : 15);
        _state.Failures.Remove(Key(source.FullName, kind)); _state.OpenFailures.Remove(source.FullName);
        if (stage == "Opening") _state.OpenFailures[source.FullName] = failure;
        else _state.Failures[Key(source.FullName, kind)] = failure;
        _totals[kind].Failed++;
        if (failure.Attempts >= 3)
        {
            var notices = EnsureFailureNotes(failure, stage == "Opening", true);
            _logger.WriteFailure(source.FullName, kind, failure.Errors, notices);
            Record(kind, source.FullName, "三次失败已挂起；同目录失败说明已尝试写入，点击重试失败项后恢复");
        }
        else Record(kind, source.FullName, $"失败 {failure.Attempts}/3，延后至 {failure.NextAttemptUtc.ToLocalTime():HH:mm:ss} 后空闲重试：{message}");
        SaveState();
    }

    private IReadOnlyList<string> EnsureFailureNotes(FailureState failure, bool openFailure, bool refresh = false)
    {
        var notices = new List<string>();
        var settings = Snapshot();
        foreach (var kind in openFailure ? new[] { ExportKind.Pdf, ExportKind.Jpg } : new[] { failure.Kind })
        {
            var job = kind == ExportKind.Pdf ? settings.PdfJob : settings.JpgJob;
            if (!Eligible(new(failure.SourcePath), job)) continue;
            var key = Key(failure.SourcePath, kind);
            if (!refresh && _state.FailureNotes.TryGetValue(key, out var old) && File.Exists(old)) continue;
            try
            {
                var path = FailureNote.Write(failure.SourcePath, kind, failure, Now());
                _state.FailureNotes[key] = path; failure.NotePath = path;
                Record(kind, failure.SourcePath, "已生成失败说明：" + path);
            }
            catch (Exception ex)
            {
                notices.Add("同目录失败说明未写入：" + kind + " " + ex.Message);
                _logger.Error("同目录失败说明写入失败：" + failure.SourcePath + " " + ex.Message);
                Record(kind, failure.SourcePath, "同目录无法写入失败说明，详见桌面失败记录：" + ex.Message);
            }
        }
        return notices;
    }

    private void ClearFailure(string source, ExportKind kind, bool opened)
    {
        _state.Failures.Remove(Key(source, kind));
        if (opened) _state.OpenFailures.Remove(source);
        var key = Key(source, kind);
        if (_state.FailureNotes.TryGetValue(key, out var path))
            try { FailureNote.Remove(source, kind, path); if (!File.Exists(path)) _state.FailureNotes.Remove(key); }
            catch (Exception ex) { _logger.Error("失败说明清理稍后重试：" + ex.Message); }
    }

    private int ReconcileDeletedSources(MonitorJobSettings job)
    {
        var count = 0;
        foreach (var entry in _manifest.GetEntries(job.Kind))
        {
            var key = Key(entry.SourcePath, job.Kind);
            try
            {
                if (!job.RootPaths.Any(root => Directory.Exists(root) && FileSafety.UnderRoot(entry.SourcePath, root) && FileSafety.IsVisible(entry.SourcePath, root)) ||
                    entry.OutputFiles.Any(p => !FileSafety.IsVisibleOutput(p)))
                { _state.MissingSince.Remove(key); continue; }
                if (!FileSafety.ConfirmMissing(entry.SourcePath)) { _state.MissingSince.Remove(key); continue; }
                if (!_state.MissingSince.TryGetValue(key, out var since)) { _state.MissingSince[key] = Now(); continue; }
                if (Now() - since < TimeSpan.FromSeconds(30)) continue;
                foreach (var output in entry.OutputFiles) FileSafety.RequireOutput(entry.SourcePath, output, job.Kind);
                foreach (var output in entry.OutputFiles)
                {
                    if (!FileSafety.ConfirmMissing(entry.SourcePath)) break;
                    if (File.Exists(output)) { File.Delete(output); count++; Record(job.Kind, entry.SourcePath, "源已删除，清理：" + output); }
                }
                if (!entry.OutputFiles.Any(File.Exists) && FileSafety.ConfirmMissing(entry.SourcePath))
                { _manifest.Remove(entry.SourcePath, job.Kind); _state.MissingSince.Remove(key); _state.Failures.Remove(key); }
            }
            catch (Exception ex) { _state.MissingSince.Remove(key); Record(job.Kind, entry.SourcePath, "暂缓清理：" + ex.Message); }
        }
        SaveState(); return count;
    }

    private void RetryCleanup(MonitorJobSettings job)
    {
        foreach (var entry in _manifest.GetEntries(job.Kind))
        {
            var key = Key(entry.SourcePath, job.Kind);
            if (!_state.Cleanup.TryGetValue(key, out var paths) || !job.RootPaths.Any(root => FileSafety.UnderRoot(entry.SourcePath, root) && FileSafety.IsVisible(entry.SourcePath, root))) continue;
            foreach (var path in paths.ToArray())
            {
                try
                {
                    if (!FileSafety.IsVisibleOutput(path)) continue;
                    var marker = path.LastIndexOf(".vectorforge-backup-", StringComparison.Ordinal);
                    if (marker >= 0 && Guid.TryParseExact(path[(marker + ".vectorforge-backup-".Length)..], "N", out _))
                    { FileSafety.RequireOutput(entry.SourcePath, path[..marker], job.Kind); if (!FileSafety.IsPlainPath(path)) continue; }
                    else FileSafety.RequireOutput(entry.SourcePath, path, job.Kind);
                    if (entry.OutputFiles.Contains(path, StringComparer.OrdinalIgnoreCase)) continue;
                    File.Delete(path); paths.Remove(path);
                }
                catch (Exception ex) { Record(job.Kind, path, "清理稍后重试：" + ex.Message); }
            }
            if (paths.Count == 0) _state.Cleanup.Remove(key);
        }
    }

    private void CleanupAbandoned()
    {
        foreach (var work in _state.Abandoned.ToArray())
        {
            if (work.Blockers.Count == 0 || work.Blockers.Any(p => p.MatchesLiveProcess())) continue;
            foreach (var path in work.Paths.ToArray())
            {
                var name = Path.GetFileName(path);
                var isWork = Regex.IsMatch(name, @"^vectorforge_temp_source_[0-9a-f]{32}\.ai$") ||
                    Regex.IsMatch(name, @"^vectorforge_temp_output_[0-9a-f]{32}\.(pdf|jpg)$") ||
                    Path.GetDirectoryName(path) == AppPaths.WorkerDirectory && Regex.IsMatch(name, @"^[0-9a-f]{32}\.stage$");
                if (!isWork || !FileSafety.IsPlainPath(path) || !FileSafety.IsVisibleOutput(path)) continue;
                try { File.Delete(path); work.Paths.Remove(path); } catch { }
            }
            if (work.Paths.Count == 0) _state.Abandoned.Remove(work);
        }
    }

    private void PublishUnlessActive(ExportKind kind, string state, int pending, string result)
    { if (_active?.Kind != kind) Publish(kind, state, pending, result); }
    private void SaveState() => AtomicJsonStore.Save(_manifest.StatePath, _state);
    private void Record(ExportKind kind, string path, string reason)
    { _logger.Info(kind + " " + reason + " " + path); RecordAdded?.Invoke(this, new(Now().ToLocalTime(), kind, path, reason)); }
    private void Publish(ExportKind kind, string state, int pending, string result) => StatusChanged?.Invoke(this, new(kind, state, pending, result));

    internal Task ExportSingleForTestingAsync(FileInfo source, MonitorJobSettings job, CancellationToken token = default)
    {
        job.Normalize(); job.Enabled = true;
        lock (_settings) { if (job.Kind == ExportKind.Pdf) _settings.PdfJob = job; else _settings.JpgJob = job; }
        return ExportWithRetriesAsync(source, job, token);
    }
    internal bool NeedsExportForTesting(FileInfo source, ExportKind kind) { lock (_gate) return NeedsExport(source, kind); }
    internal int ReconcileDeletedSourcesForTesting(MonitorJobSettings job) { lock (_gate) return ReconcileDeletedSources(job); }
    internal void DisableStabilityDelayForTesting() => _scanner.StabilityWindow = TimeSpan.Zero;
    internal async Task ScanForTestingAsync(MonitorJobSettings job, CancellationToken token = default)
    {
        lock (_settings) { if (job.Kind == ExportKind.Pdf) _settings.PdfJob = job; else _settings.JpgJob = job; }
        RunNow(job.Kind);
        Scan(job, token);
        await DispatchOnceAsync(token, job.Kind);
    }
    internal Task<bool> DispatchForTestingAsync(CancellationToken token = default) => DispatchOnceAsync(token);
    internal void ScanOnlyForTesting(MonitorJobSettings job)
    {
        lock (_settings) { if (job.Kind == ExportKind.Pdf) _settings.PdfJob = job; else _settings.JpgJob = job; }
        RunNow(job.Kind);
        Scan(job, default);
    }
    public void Dispose()
    {
        _cts?.Cancel();
        foreach (var change in _changes.Values) change.Dispose();
        // The UI awaits PauseAsync before disposal, so no active worker is abandoned on normal exit.
        foreach (var cancellation in _jobCancellations.Values) cancellation.Dispose();
        (_exporter as IDisposable)?.Dispose();
    }
}
