using System.Diagnostics;
using System.Text;

namespace VectorForge.Services;

public sealed class WorkerProcessIllustratorExporter : IIllustratorExporter, IDisposable
{
    private readonly AppLogger _logger;
    private readonly string? _executablePath;
    private readonly bool _testTransport;
    private readonly IllustratorSessionManager _session;
    private Process? _process;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTimeOffset _lastUse;
    private Guid _workerSession;
    public bool LastCleanupSafe { get; private set; } = true;
    public List<IllustratorIdentity> LastBlockers { get; private set; } = [];
    public event Action<string>? Progress;
    internal TimeSpan StartupTimeout { get; set; } = TimeSpan.FromSeconds(120);

    public WorkerProcessIllustratorExporter(AppLogger logger, string? executablePath = null)
        : this(logger, executablePath, false) { }
    internal WorkerProcessIllustratorExporter(AppLogger logger, string? executablePath, bool testTransport)
    {
        AppPaths.EnsureCreated(); _logger = logger; _executablePath = executablePath;
        _testTransport = testTransport; _session = new(logger);
    }

    public void AllowRecoveryCheck() => _session.AllowRecoveryCheck();

    public async Task<IReadOnlyList<string>> ReadPdfPresetsAsync(IllustratorSessionMode mode = IllustratorSessionMode.Unattended, CancellationToken token = default)
    {
        var request = new ExportRequest { ReadPdfPresets = true, SessionMode = mode, Kind = ExportKind.Pdf,
            SourcePath = "", WorkingPrefix = "", OwnershipFile = "" };
        var result = await ExportAsync(request, TimeSpan.FromMinutes(2), token);
        if (!result.Success) throw new IOException(result.ErrorMessage);
        return result.PdfPresets;
    }

    public async Task<ExportResult> ExportAsync(ExportRequest request, TimeSpan timeout, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        var ready = false;
        var watch = Stopwatch.StartNew();
        LastCleanupSafe = true;
        try
        {
            // A custom executable is used only by self-tests, never by production UI.
            if (!_testTransport)
            {
                _session.Prepare(request);
                if (_process is not null && _workerSession != request.SessionId) await CloseCoreAsync(false);
                _workerSession = request.SessionId;
            }
            StartWorker();
            if (string.IsNullOrEmpty(request.ProgressPath))
                request.ProgressPath = Path.Combine(AppPaths.WorkerDirectory, request.JobId.ToString("N") + ".stage");
            LastBlockers = !_testTransport ? IllustratorSessionManager.CurrentIdentities() : [];
            LastCleanupSafe = false;
            await _process!.StandardInput.WriteLineAsync(AtomicJsonStore.Serialize(request).ReplaceLineEndings(""));
            await _process.StandardInput.FlushAsync(token);
            var startupDeadline = watch.Elapsed + StartupTimeout;
            var fileDeadline = TimeSpan.MaxValue;
            Task<string?>? pendingRead = null;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                pendingRead ??= _process.StandardOutput.ReadLineAsync(token).AsTask();
                var deadline = ready ? fileDeadline : startupDeadline;
                var remaining = deadline - watch.Elapsed;
                if (remaining <= TimeSpan.Zero) throw new TimeoutException(ready ? "单文件处理超过设置的超时时间" : "Illustrator 启动或会话检查超时");
                var tick = Task.Delay(remaining < TimeSpan.FromSeconds(1) ? remaining : TimeSpan.FromSeconds(1), token);
                if (await Task.WhenAny(pendingRead, tick) != pendingRead)
                {
                    Progress?.Invoke($"{StageName(ready ? WorkerHost.ReadStage(request.ProgressPath) : "Starting")} · 已用 {watch.Elapsed:mm\\:ss} · 上限 {(ready ? timeout.TotalMinutes + " 分钟" : "120 秒")}");
                    continue;
                }
                var line = await pendingRead;
                pendingRead = null;
                if (line is null) throw new IOException("Worker 提前退出");
                var result = AtomicJsonStore.Deserialize<ExportResult>(line) ?? throw new IOException("无效 Worker 响应");
                if (result.JobId != request.JobId) throw new IOException("Worker 响应任务 ID 不匹配");
                if (result.IsHandshake)
                {
                    if (ready) throw new IOException("重复 Worker 握手");
                    ready = true; fileDeadline = watch.Elapsed + timeout; continue;
                }
                if (result.Success && !ready && !_testTransport) throw new IOException("Worker 未握手即返回导出成功");
                if (result.ErrorCode is "USER_TAKEOVER" or "OWNERSHIP_AMBIGUOUS")
                { _session.Relinquish(); LastCleanupSafe = true; StopWorkerOnly(); return result; }
                if (!result.Success)
                {
                    result.Stage = WorkerHost.ReadStage(request.ProgressPath);
                    if (!ready && !_testTransport) result.ErrorCode = "SESSION_UNAVAILABLE";
                    await RecoverCoreAsync();
                    result.SafeToCleanup = LastCleanupSafe;
                    result.SharedInstance = !LastCleanupSafe;
                    return result;
                }
                LastCleanupSafe = true;
                return result;
            }
        }
        catch (SessionUnavailableException ex)
        { return ExportResult.Failed(request.JobId, ex.Code, ex.Message); }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { await RecoverCoreAsync(); throw; }
        catch (Exception ex)
        {
            var stage = WorkerHost.ReadStage(request.ProgressPath);
            _logger.Error("Worker 会话异常：" + ex.Message);
            Progress?.Invoke("正在恢复 Illustrator，会话重建后继续其他文件");
            await RecoverCoreAsync();
            var result = ExportResult.Failed(request.JobId, ready || _testTransport
                ? ex is TimeoutException ? "FILE_TIMEOUT" : "SESSION_INTERRUPTED" : "SESSION_UNAVAILABLE", ex.Message);
            result.Stage = stage; result.SharedInstance = !LastCleanupSafe; result.SafeToCleanup = LastCleanupSafe;
            if (_session.RecoveryBlocked) result.ErrorCode = "RECOVERY_BLOCKED";
            return result;
        }
        finally
        {
            _lastUse = DateTimeOffset.UtcNow;
            if (LastCleanupSafe && !string.IsNullOrEmpty(request.ProgressPath)) try { File.Delete(request.ProgressPath); } catch { }
            _gate.Release();
        }
    }

    private void StartWorker()
    {
        if (_process is { HasExited: false }) return;
        _process?.Dispose();
        _process = Process.Start(new ProcessStartInfo(_executablePath ?? Environment.ProcessPath!, "--worker")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false)
        }) ?? throw new IOException("无法启动 Worker");
        _ = _process.StandardError.ReadToEndAsync();
    }

    private async Task RecoverCoreAsync()
    {
        StopWorkerOnly();
        LastCleanupSafe = _testTransport || await _session.RecoverAsync();
        if (!LastCleanupSafe && LastBlockers.Count == 0) LastBlockers = IllustratorSessionManager.CurrentIdentities();
    }

    public async Task CloseIfIdleAsync()
    {
        if (!await _gate.WaitAsync(0)) return;
        try { if (_process is not null && DateTimeOffset.UtcNow - _lastUse >= TimeSpan.FromSeconds(60)) await CloseCoreAsync(); }
        finally { _gate.Release(); }
    }
    public async Task CloseAsync()
    {
        await _gate.WaitAsync();
        try { await CloseCoreAsync(); } finally { _gate.Release(); }
    }
    private async Task CloseCoreAsync(bool relinquish = true)
    {
        if (_process is null) return;
        var releaseConfirmed = false;
        try
        {
            if (!_process.HasExited)
            {
                _process.StandardInput.Close();
                var line = await _process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
                if (line is not null)
                {
                    var acknowledgement = AtomicJsonStore.Deserialize<ExportResult>(line);
                    releaseConfirmed = acknowledgement?.ErrorCode == "SESSION_RELEASING";
                    if (relinquish && acknowledgement?.ErrorCode == "USER_TAKEOVER") _session.Relinquish();
                }
                await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
        catch
        {
            if (relinquish && releaseConfirmed) await RecoverCoreAsync();
            else StopWorkerOnly();
        }
        finally
        {
            _process?.Dispose(); _process = null;
            // Idle shutdown cannot claim an Illustrator the user may now have taken over.
            if (relinquish) _session.Relinquish();
        }
    }

    private void StopWorkerOnly()
    {
        try { if (_process is { HasExited: false }) _process.Kill(false); } catch { }
        _process?.Dispose(); _process = null;
    }
    private static string StageName(string stage) => stage switch
    { "Starting" => "启动 / 检查会话", "Opening" => "打开 AI", "Exporting" => "导出文件", "Closing" => "关闭工作副本", _ => "验证结果" };
    public void Dispose() => StopWorkerOnly();
}
