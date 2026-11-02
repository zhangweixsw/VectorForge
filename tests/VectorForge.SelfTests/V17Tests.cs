using System.Diagnostics;
using System.Security.Cryptography;
using VectorForge;
using VectorForge.Services;

static class V17Tests
{
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static string Root()
    {
        var path = Path.Combine(Path.GetTempPath(), "VectorForgeV17", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path); return path;
    }
    private static MonitorJobSettings Job(string root, ExportKind kind = ExportKind.Pdf)
    {
        var job = new MonitorJobSettings { Enabled = true, RootPaths = [root], Kind = kind, ModifiedWithinHours = 24, ScanIntervalSeconds = 1 };
        job.Normalize(); return job;
    }
    private static AppSettings Settings(MonitorJobSettings job) => job.Kind == ExportKind.Pdf
        ? new() { PdfJob = job } : new() { JpgJob = job };

    public static void RetriesAndNotes()
    {
        var root = Root();
        try
        {
            var path = Path.Combine(root, "中文设计 稿.ai"); File.WriteAllText(path, "bad");
            var before = SHA256.HashData(File.ReadAllBytes(path)); var timestamp = File.GetLastWriteTimeUtc(path);
            var manifest = Path.Combine(root, "manifest.json"); var job = Job(root); var settings = Settings(job);
            var now = DateTimeOffset.UtcNow; var fake = new ControlledExporter { Fail = true, Stage = "Exporting" };
            using (var first = new MonitorCoordinator(settings, new(), fake, new(manifest)))
            {
                first.Now = () => now; first.DisableStabilityDelayForTesting();
                first.ScanForTestingAsync(job).GetAwaiter().GetResult(); Check(fake.Calls == 1, "首次失败应只有一次调用");
                now += TimeSpan.FromMinutes(4); first.DispatchForTestingAsync().GetAwaiter().GetResult(); Check(fake.Calls == 1, "5分钟前错误重试");
            }
            using var restarted = new MonitorCoordinator(settings, new(), fake, new(manifest));
            restarted.Now = () => now; restarted.DisableStabilityDelayForTesting();
            restarted.ScanForTestingAsync(job).GetAwaiter().GetResult(); Check(fake.Calls == 1, "重启跳过了冷却时间");
            now += TimeSpan.FromMinutes(1); restarted.DispatchForTestingAsync().GetAwaiter().GetResult(); Check(fake.Calls == 2, "第二次没有在空闲时恢复");
            now += TimeSpan.FromMinutes(14); restarted.DispatchForTestingAsync().GetAwaiter().GetResult(); Check(fake.Calls == 2, "第三次间隔不足15分钟");
            now += TimeSpan.FromMinutes(1); restarted.DispatchForTestingAsync().GetAwaiter().GetResult(); Check(fake.Calls == 3, "缺少第三次尝试");
            var notes = Directory.GetFiles(root, "*无法导出*.txt"); Check(notes.Length == 1, "应生成一个对应类型的失败TXT");
            var contents = File.ReadAllText(notes[0]); Check(contents.Contains(path) && contents.Contains("尝试次数：3") && contents.Contains("模拟错误") && contents.Contains("已暂停自动重试"), "TXT信息不完整");
            now += TimeSpan.FromMinutes(30); restarted.ScanForTestingAsync(job).GetAwaiter().GetResult(); Check(fake.Calls == 3, "挂起后仍自动重试");
            fake.Fail = false; restarted.RetryFailures(ExportKind.Pdf); restarted.ScanForTestingAsync(job).GetAwaiter().GetResult();
            Check(fake.Calls == 4 && File.Exists(Path.ChangeExtension(path, ".pdf")), "手动重试未恢复导出");
            Check(!File.Exists(notes[0]), "成功后没有清理自己的失败说明");
            Check(before.SequenceEqual(SHA256.HashData(File.ReadAllBytes(path))) && timestamp == File.GetLastWriteTimeUtc(path), "原始AI发生变化");
        }
        finally { Directory.Delete(root, true); }
    }

    public static void OpenCooldownAndPriority()
    {
        var root = Root();
        try
        {
            File.WriteAllText(Path.Combine(root, "bad.ai"), "bad");
            var pdf = Job(root); var jpg = Job(root, ExportKind.Jpg); var settings = new AppSettings { PdfJob = pdf, JpgJob = jpg };
            var fake = new ControlledExporter { Fail = true, Stage = "Opening", BadOnly = true };
            using var coordinator = new MonitorCoordinator(settings, new(), fake, new(Path.Combine(root, "state.json")));
            var now = DateTimeOffset.UtcNow; coordinator.Now = () => now; coordinator.DisableStabilityDelayForTesting();
            coordinator.ScanForTestingAsync(pdf).GetAwaiter().GetResult();
            coordinator.ScanForTestingAsync(jpg).GetAwaiter().GetResult(); Check(fake.Calls == 1, "打开失败后马上换JPG重试同一AI");
            File.WriteAllText(Path.Combine(root, "good.ai"), "good");
            coordinator.ScanForTestingAsync(jpg).GetAwaiter().GetResult(); Check(fake.Calls == 2 && fake.Payloads[^1] == "good", "普通JPG被问题AI阻塞");
            now += TimeSpan.FromMinutes(5);
            coordinator.ScanForTestingAsync(pdf).GetAwaiter().GetResult(); Check(fake.Payloads[^1] == "good", "延后任务抢在普通任务之前");
            coordinator.DispatchForTestingAsync().GetAwaiter().GetResult(); Check(fake.Calls == 4 && fake.Payloads[^1] == "bad", "空闲时未重试问题AI");
            fake.Fail = false; now += TimeSpan.FromMinutes(15); coordinator.DispatchForTestingAsync().GetAwaiter().GetResult();
            coordinator.ScanForTestingAsync(jpg).GetAwaiter().GetResult(); Check(File.Exists(Path.Combine(root, "bad.jpg")), "成功打开后没有解除两种格式的共同冷却");
        }
        finally { Directory.Delete(root, true); }
    }

    public static void HiddenPaths()
    {
        var root = Root();
        try
        {
            var hidden = Directory.CreateDirectory(Path.Combine(root, "隐藏目录")).FullName;
            File.WriteAllText(Path.Combine(hidden, "inside.ai"), "x"); File.SetAttributes(hidden, FileAttributes.Directory | FileAttributes.Hidden);
            var file = Path.Combine(root, "hidden.ai"); File.WriteAllText(file, "x"); File.SetAttributes(file, FileAttributes.Hidden);
            var visible = Path.Combine(root, "visible.ai"); File.WriteAllText(visible, "good");
            var job = Job(root); var scanner = new FileScanner(new()) { StabilityWindow = TimeSpan.Zero };
            var found = scanner.FindStableCandidatesAsync(job, default).Result;
            Check(found.Count == 1 && found[0].FullName == visible, "隐藏文件或子目录被扫描");
            var fake = new ControlledExporter(); using var coordinator = new MonitorCoordinator(Settings(job), new(), fake, new(Path.Combine(root, "state.json")));
            coordinator.DisableStabilityDelayForTesting(); coordinator.ScanOnlyForTesting(job);
            File.SetAttributes(visible, FileAttributes.Hidden);
            coordinator.DispatchForTestingAsync().GetAwaiter().GetResult(); Check(fake.Calls == 0, "排队后隐藏仍导出");
            File.SetAttributes(visible, FileAttributes.Normal);
            var output = Path.ChangeExtension(visible, ".pdf"); ValidFixture.Write(output, ExportKind.Pdf); File.SetAttributes(output, FileAttributes.Hidden);
            coordinator.Reexport(ExportKind.Pdf); coordinator.ScanForTestingAsync(job).GetAwaiter().GetResult(); Check(fake.Calls == 0, "隐藏输出被强制覆盖");
            File.SetAttributes(root, FileAttributes.Directory | FileAttributes.Hidden);
            Check(scanner.FindStableCandidatesAsync(job, default).Result.Count == 0, "隐藏监控根目录仍被扫描");
            File.SetAttributes(root, FileAttributes.Directory);
            using var watcher = new ChangeMonitor(); watcher.Configure([root]);
            File.SetAttributes(file, FileAttributes.Normal); File.AppendAllText(file, "saved");
            Check(SpinWait.SpinUntil(() => watcher.Snapshot().ContainsKey(file), 3000), "恢复可见后未发现变化");
            File.SetAttributes(file, FileAttributes.Hidden);
            Check(SpinWait.SpinUntil(() => !watcher.Snapshot().ContainsKey(file), 3000), "隐藏事件没有从变更记录移除");
        }
        finally { if (Directory.Exists(root)) { File.SetAttributes(root, FileAttributes.Directory); Directory.Delete(root, true); } }
    }

    public static void HiddenCleanup()
    {
        var root = Root();
        try
        {
            var folder = Directory.CreateDirectory(Path.Combine(root, "folder")).FullName;
            var source = Path.Combine(folder, "missing.ai"); var output = Path.ChangeExtension(source, ".pdf"); ValidFixture.Write(output, ExportKind.Pdf);
            var manifest = new ManifestStore(Path.Combine(root, "state.json")); manifest.Put(new() { SourcePath = source, Kind = ExportKind.Pdf, OutputFiles = [output] });
            using var coordinator = new MonitorCoordinator(new(), new(), new ControlledExporter(), manifest); var job = Job(root);
            var now = DateTimeOffset.UtcNow; coordinator.Now = () => now;
            coordinator.ReconcileDeletedSourcesForTesting(job); File.SetAttributes(folder, FileAttributes.Directory | FileAttributes.Hidden);
            now += TimeSpan.FromMinutes(1); Check(coordinator.ReconcileDeletedSourcesForTesting(job) == 0 && File.Exists(output), "隐藏目录中的输出被误删除");
            File.SetAttributes(folder, FileAttributes.Directory);
            Check(coordinator.ReconcileDeletedSourcesForTesting(job) == 0, "隐藏恢复后没有重新计时");
            now += TimeSpan.FromSeconds(31); Check(coordinator.ReconcileDeletedSourcesForTesting(job) == 1, "可见且确认缺失后没有正常清理");
        }
        finally { Directory.Delete(root, true); }
    }

    public static void NoteSafety()
    {
        var root = Root();
        try
        {
            var source = Path.Combine(root, new string('中', 150) + ".ai"); File.WriteAllText(source, "source");
            var failure = new FailureState { SourcePath = source, Attempts = 3, Stage = "Exporting", Errors = ["错误一", "错误二", "错误三"] };
            var note = FailureNote.Write(source, ExportKind.Pdf, failure, DateTimeOffset.Now);
            Check(Path.GetFileName(note).Length < 200 && File.Exists(note), "长文件名失败说明不可用");
            failure.Errors[2] = "新错误"; FailureNote.Write(source, ExportKind.Pdf, failure, DateTimeOffset.Now);
            Check(File.ReadAllText(note).Contains("新错误"), "说明未覆盖更新");
            var shortSource = Path.Combine(root, "稿.ai"); File.WriteAllText(shortSource, "source");
            var unrelated = Path.Combine(root, "稿_无法导出稍后重试_PDF.txt"); File.WriteAllText(unrelated, "用户文档");
            var alternative = FailureNote.Write(shortSource, ExportKind.Pdf, failure, DateTimeOffset.Now);
            Check(alternative != unrelated && File.ReadAllText(unrelated) == "用户文档", "覆盖了用户同名TXT");
            FailureNote.Remove(shortSource, ExportKind.Pdf, unrelated); Check(File.Exists(unrelated), "删除了不属于软件的TXT");
            FailureNote.Remove(shortSource, ExportKind.Pdf, alternative); Check(!File.Exists(alternative), "自己的TXT未删除");
            FailureNote.Remove(shortSource, ExportKind.Pdf, source); Check(File.ReadAllText(source) == "source", "危险报告路径触及AI");
            File.WriteAllText(alternative, "用户备用文档");
            var job = Job(root); var fake = new ControlledExporter { Fail = true }; var now = DateTimeOffset.UtcNow;
            using var coordinator = new MonitorCoordinator(Settings(job), new(), fake, new(Path.Combine(root, "state.json")));
            coordinator.Now = () => now;
            coordinator.ExportSingleForTestingAsync(new(shortSource), job).GetAwaiter().GetResult();
            now += TimeSpan.FromMinutes(5); coordinator.DispatchForTestingAsync().GetAwaiter().GetResult();
            now += TimeSpan.FromMinutes(15); coordinator.DispatchForTestingAsync().GetAwaiter().GetResult();
            var desktop = Path.Combine(AppPaths.DataDirectory, $"VectorForge_导出失败_{DateTime.Now:yyyy-MM-dd}.txt");
            Check(File.ReadAllText(desktop).Contains("同目录失败说明未写入"), "TXT无法写入时桌面汇总没有原因");
            Check(File.ReadAllText(alternative) == "用户备用文档" && File.ReadAllText(unrelated) == "用户文档", "失败TXT冲突时覆盖了用户文件");
        }
        finally { Directory.Delete(root, true); }
    }

    public static void SessionSettings()
    {
        var service = new SettingsService();
        service.Commit(new AppSettings { SessionMode = IllustratorSessionMode.Shared });
        Check(new SettingsService().Settings.SessionMode == IllustratorSessionMode.Shared, "共享模式未持久化");
        service.Commit(new AppSettings { SessionMode = IllustratorSessionMode.Unattended,
            JpgJob = new() { JpgSizing = JpgSizingMode.PixelWidth, JpgPixelWidth = 5000 } });
        var saved = new SettingsService().Settings;
        Check(saved.SessionMode == IllustratorSessionMode.Unattended && saved.JpgJob.JpgSizing == JpgSizingMode.PixelWidth && saved.JpgJob.JpgPixelWidth == 5000,
            "v18 显式工作模式及像素参数未持久化");
        File.WriteAllText(AppPaths.SettingsFile, "{\"PdfJob\":{\"ModifiedWithinHours\":72},\"JpgJob\":{\"JpgDpi\":300}}");
        var migrated = new SettingsService().Settings;
        Check(migrated.SessionMode == IllustratorSessionMode.Shared && migrated.PdfJob.ModifiedWithinHours == 72 && migrated.JpgJob.JpgDpi == 300,
            "旧设置迁移丢失参数或默认模式错误");
        Check(File.Exists(AppPaths.SettingsFile + ".pre-v17.bak"), "缺少升级前设置备份");
        service.Commit(new AppSettings());
    }

    public static void BackgroundScan()
    {
        var root = Root();
        try
        {
            File.WriteAllText(Path.Combine(root, "first.ai"), "one");
            var pdf = Job(root); var jpg = Job(root, ExportKind.Jpg); var fake = new BlockingExporter();
            using var coordinator = new MonitorCoordinator(new() { PdfJob = pdf, JpgJob = jpg }, new(), fake, new(Path.Combine(root, "state.json")));
            coordinator.DisableStabilityDelayForTesting();
            var later = Path.Combine(root, "later.ai"); var detected = 0;
            coordinator.RecordAdded += (_, record) => { if (record.Path == later) Interlocked.Exchange(ref detected, 1); };
            coordinator.Start(); Check(fake.Entered.Task.Wait(4000), "没有开始导出");
            File.WriteAllText(later, "two");
            Check(SpinWait.SpinUntil(() => Volatile.Read(ref detected) == 1, 5000), "卡住导出时扫描也停止了");
            Check(fake.Calls == 1, "产生并行Illustrator任务");
            var pause = coordinator.PauseAsync(); Check(pause.Wait(4000), "暂停没有取消卡住任务");
            Check(fake.Calls == 1, "暂停后仍继续处理排队任务");
        }
        finally { Directory.Delete(root, true); }
    }

    public static void RetryScope()
    {
        var root = Root();
        try
        {
            var path = Path.Combine(root, "bad.ai"); File.WriteAllText(path, "bad"); var job = Job(root);
            var fake = new ControlledExporter { Fail = true }; var now = DateTimeOffset.UtcNow;
            using var coordinator = new MonitorCoordinator(Settings(job), new(), fake, new(Path.Combine(root, "state.json")));
            coordinator.Now = () => now; coordinator.DisableStabilityDelayForTesting(); coordinator.ScanForTestingAsync(job).GetAwaiter().GetResult();
            now += TimeSpan.FromMinutes(5); job.RootPaths.Clear(); coordinator.DispatchForTestingAsync().GetAwaiter().GetResult(); Check(fake.Calls == 1, "删除路径后仍重试");
            job.RootPaths = [root]; File.SetAttributes(path, FileAttributes.Hidden); coordinator.DispatchForTestingAsync().GetAwaiter().GetResult(); Check(fake.Calls == 1, "隐藏文件仍重试");
            File.SetAttributes(path, FileAttributes.Normal); job.SkipFileNameKeywords = ["bad"]; coordinator.DispatchForTestingAsync().GetAwaiter().GetResult(); Check(fake.Calls == 1, "关键词跳过仍重试");
            job.SkipFileNameKeywords.Clear(); job.ModifiedWithinHours = 1; now += TimeSpan.FromHours(2);
            coordinator.DispatchForTestingAsync().GetAwaiter().GetResult(); Check(fake.Calls == 1, "重试绕过小时范围");
            job.ModifiedWithinHours = 24; job.Enabled = false; coordinator.StopJob(job.Kind); coordinator.DispatchForTestingAsync().GetAwaiter().GetResult(); Check(fake.Calls == 1, "停止后仍重试");
        }
        finally { Directory.Delete(root, true); }
    }

    public static void SessionOwnership()
    {
        var adobeRoot = @"C:\Program Files\Adobe\Adobe Illustrator 2026";
        var adobeExe = Path.Combine(adobeRoot, @"Support Files\Contents\Windows\Illustrator.exe");
        Check(IllustratorSessionManager.MatchesApplicationDirectory(adobeRoot, adobeExe), "不兼容Adobe安装根目录");
        Check(!IllustratorSessionManager.MatchesApplicationDirectory(@"C:\", adobeExe), "宽泛祖先路径被认作安装目录");
        Process? fixture = null;
        try
        {
            IllustratorSessionManager Manager() => new(new())
            {
                ResolveExecutable = () => Environment.ProcessPath!,
                EnumerateProcesses = () => fixture is { HasExited: false } ? [IllustratorIdentity.Read(fixture)] : [],
                Launch = executable =>
                {
                    var launched = Process.Start(new ProcessStartInfo(executable, "--illustrator-fixture") { UseShellExecute = false, CreateNoWindow = true });
                    fixture = Process.GetProcessById(launched!.Id);
                    return launched;
                }
            };
            ExportRequest Request(IllustratorSessionMode mode = IllustratorSessionMode.Unattended) => new()
            { SessionMode = mode, Kind = ExportKind.Pdf, SourcePath = "", WorkingPrefix = "", OwnershipFile = "" };
            var owned = Manager(); owned.Prepare(Request()); Check(owned.CanTerminate && fixture is not null, "专用进程未登记");
            Check(owned.RecoverAsync().Result && fixture!.HasExited, "不能回收自己启动的专用进程"); fixture!.Dispose(); fixture = null;
            var shared = Manager(); shared.Prepare(Request(IllustratorSessionMode.Shared));
            Check(!shared.CanTerminate && !shared.RecoverAsync().Result && !fixture!.HasExited, "结束了共享实例");
            var newManager = Manager();
            try { newManager.Prepare(Request()); throw new Exception("已存在实例时仍接管"); } catch (SessionUnavailableException ex) { Check(ex.Code == "WAITING_USER", "没有等待用户实例退出"); }
            fixture!.Kill(false); fixture.WaitForExit(); fixture.Dispose(); fixture = null;
            var stale = Manager(); stale.Prepare(Request()); stale.Owned!.StartedUtc = stale.Owned.StartedUtc.AddSeconds(-1);
            stale.RecoverAsync().GetAwaiter().GetResult(); Check(!fixture!.HasExited, "PID时间身份不匹配仍强制结束进程");
            fixture.Kill(false); fixture.WaitForExit(); fixture.Dispose(); fixture = null;
            var takeover = Manager(); takeover.Prepare(Request()); takeover.Relinquish(); takeover.RecoverAsync().GetAwaiter().GetResult();
            Check(!fixture!.HasExited, "用户接管后仍结束实例");
        }
        finally { if (fixture is not null) { if (!fixture.HasExited) { fixture.Kill(false); fixture.WaitForExit(); } fixture.Dispose(); } }
    }

    public static void WorkerDeadlines()
    {
        var root = Root();
        try
        {
            using var exporter = new WorkerProcessIllustratorExporter(new(), Environment.ProcessPath!, true) { StartupTimeout = TimeSpan.FromSeconds(2) };
            ExportRequest Request(int dpi) => new() { Kind = ExportKind.Jpg, SourcePath = "", WorkingPrefix = Path.Combine(root, "probe-" + dpi), OwnershipFile = "", JpgDpi = dpi };
            foreach (var (dpi, stage) in new[] { (498, "Opening"), (497, "Exporting"), (496, "Closing") })
            {
                var result = exporter.ExportAsync(Request(dpi), TimeSpan.FromMilliseconds(300), default).Result;
                Check(!result.Success && result.Stage == stage && result.SafeToCleanup && result.ErrorCode == "FILE_TIMEOUT", "阶段超时恢复错误：" + stage);
            }
            var watch = Stopwatch.StartNew(); var changing = exporter.ExportAsync(Request(492), TimeSpan.FromMilliseconds(300), default).Result;
            Check(!changing.Success && watch.Elapsed < TimeSpan.FromSeconds(2), "阶段更新无限延长了单文件截止时间");
            Check(exporter.ExportAsync(Request(493), TimeSpan.FromMilliseconds(300), default).Result.Success, "启动时间错误消耗了文件处理时间");
            exporter.StartupTimeout = TimeSpan.FromMilliseconds(200);
            var startup = exporter.ExportAsync(Request(490), TimeSpan.FromSeconds(10), default).Result;
            Check(!startup.Success, "启动阶段没有独立超时上限");
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class ControlledExporter : IIllustratorExporter
    {
        public int Calls; public bool Fail; public string Stage = "Exporting"; public bool BadOnly;
        public List<string> Payloads = [];
        public Task<ExportResult> ExportAsync(ExportRequest request, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Calls++; var payload = File.ReadAllText(request.SourcePath); Payloads.Add(payload);
            if (Fail && (!BadOnly || payload == "bad"))
            { var failed = ExportResult.Failed(request.JobId, "FILE_TIMEOUT", "模拟错误"); failed.Stage = Stage; return Task.FromResult(failed); }
            var path = request.WorkingPrefix + (request.Kind == ExportKind.Pdf ? ".pdf" : ".jpg"); ValidFixture.Write(path, request.Kind);
            return Task.FromResult(new ExportResult { JobId = request.JobId, Success = true, Stage = "Completed", ArtboardCount = 1, TemporaryOutputs = [path] });
        }
    }
    private sealed class BlockingExporter : IIllustratorExporter
    {
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls;
        public async Task<ExportResult> ExportAsync(ExportRequest request, TimeSpan timeout, CancellationToken cancellationToken)
        { Interlocked.Increment(ref Calls); Entered.TrySetResult(); await Task.Delay(Timeout.Infinite, cancellationToken); return ExportResult.Failed(request.JobId, "UNREACHABLE", ""); }
    }
}
