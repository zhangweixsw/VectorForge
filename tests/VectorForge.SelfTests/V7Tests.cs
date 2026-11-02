using VectorForge;
using VectorForge.Services;

static class V7Tests
{
    private static void Check(bool value, string reason) { if (!value) throw new Exception(reason); }
    private static string Root()
    {
        var path = Path.Combine(Path.GetTempPath(), "VectorForgeV7", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path); return path;
    }
    public static void OutputHealth()
    {
        var root = Root();
        try
        {
            foreach (var kind in Enum.GetValues<ExportKind>())
            {
                var path = Path.Combine(root, kind == ExportKind.Pdf ? "test.pdf" : "test.jpg");
                var validator = new OutputValidator();
                ValidFixture.Write(path, kind);
                Check(validator.Check(path, kind) == VectorForge.Services.OutputHealth.Valid, "有效输出未通过");
                File.WriteAllText(path, "broken");
                Check(validator.Check(path, kind) == VectorForge.Services.OutputHealth.Invalid, "损坏未检测");
                ValidFixture.Write(path, kind);
                using (var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                    Check(new OutputValidator().Check(path, kind) == VectorForge.Services.OutputHealth.Deferred, "占用应延后");
            }
        }
        finally { Directory.Delete(root, true); }
    }
    public static void FailureSuspension()
    {
        var root = Root();
        try
        {
            var source = Path.Combine(root, "bad.ai"); File.WriteAllText(source, "bad");
            var manifestPath = Path.Combine(root, "manifest.json");
            var job = new MonitorJobSettings { Enabled = true, Kind = ExportKind.Pdf, RootPaths = [root] };
            var fake = new FailureExporter();
            using (var coordinator = new MonitorCoordinator(new(), new(), fake, new(manifestPath)))
            {
                var now = DateTimeOffset.UtcNow;
                coordinator.Now = () => now;
                coordinator.ExportSingleForTestingAsync(new(source), job).GetAwaiter().GetResult();
                Check(fake.Calls == 1, "首次失败应先让出队列");
                now = now.AddMinutes(5);
                coordinator.DispatchForTestingAsync().GetAwaiter().GetResult();
                Check(fake.Calls == 2, "空闲时应执行第二次尝试");
                now = now.AddMinutes(15);
                coordinator.DispatchForTestingAsync().GetAwaiter().GetResult();
                Check(fake.Calls == 3, "需要三次总尝试");
                Check(!coordinator.NeedsExportForTesting(new(source), job.Kind), "未挂起");
            }
            using var restarted = new MonitorCoordinator(new(), new(), fake, new(manifestPath));
            Check(!restarted.NeedsExportForTesting(new(source), job.Kind), "重启丢失挂起");
            File.AppendAllText(source, "changed");
            Check(restarted.NeedsExportForTesting(new(source), job.Kind), "修改后应恢复");
            // Verify manual retry clears persisted failure without exporting: remove root from scope.
            restarted.RetryFailures(job.Kind);
            job.RootPaths = [];
            restarted.ScanForTestingAsync(job).GetAwaiter().GetResult();
            var state = AtomicJsonStore.Load(manifestPath + ".state-v7.json", () => new MonitorState());
            Check(state.Failures.Count == 0, "手动恢复未清除挂起");
        }
        finally { Directory.Delete(root, true); }
    }
    public static void SourceRecovery()
    {
        var root = Root();
        try
        {
            var source = Path.Combine(root, "restore.ai");
            var output = Path.Combine(root, "restore.pdf"); ValidFixture.Write(output, ExportKind.Pdf);
            var manifest = new ManifestStore(Path.Combine(root, "manifest.json"));
            manifest.Put(new() { SourcePath = source, Kind = ExportKind.Pdf, OutputFiles = [output] });
            using var coordinator = new MonitorCoordinator(new(), new(), new FailureExporter(), manifest);
            var job = new MonitorJobSettings { Kind = ExportKind.Pdf, RootPaths = [root] };
            var time = DateTimeOffset.UtcNow; coordinator.Now = () => time;
            Check(coordinator.ReconcileDeletedSourcesForTesting(job) == 0, "首次缺失不得清理");
            File.WriteAllText(source, "restored");
            time += TimeSpan.FromSeconds(31);
            Check(coordinator.ReconcileDeletedSourcesForTesting(job) == 0 && File.Exists(output), "源恢复仍被删除");
            File.Delete(source);
            Check(coordinator.ReconcileDeletedSourcesForTesting(job) == 0, "恢复后应重新计时");
            time += TimeSpan.FromSeconds(31);
            Check(coordinator.ReconcileDeletedSourcesForTesting(job) == 1, "第二轮缺失应清理");
            Check(!FileSafety.UnderRoot(root + "-other/x.ai", root), "目录前缀边界错误");
            Check(!OutputMatcher.IsRecognizedOutput(source, Path.Combine(root, "protected.ai"), ExportKind.Pdf), "AI被认作输出");
        }
        finally { Directory.Delete(root, true); }
    }
    public static void PathsAndForce()
    {
        var root = Root();
        try
        {
            var source = Path.Combine(root, "source.ai"); File.WriteAllText(source, "source");
            var fake = new CountingExporter();
            var manifest = new ManifestStore(Path.Combine(root, "manifest.json"));
            var job = new MonitorJobSettings { Enabled = true, Kind = ExportKind.Jpg,
                RootPaths = [Path.Combine(root, "offline"), root, root], ScanIntervalSeconds = 120, ModifiedWithinHours = 24 };
            using var coordinator = new MonitorCoordinator(new(), new(), fake, manifest);
            coordinator.DisableStabilityDelayForTesting();
            coordinator.ExportSingleForTestingAsync(new(source), job).GetAwaiter().GetResult();
            var calls = fake.Calls;
            coordinator.ScanForTestingAsync(job).GetAwaiter().GetResult();
            Check(fake.Calls == calls, "无变化扫描不应导出");
            coordinator.Reexport(ExportKind.Jpg);
            job.JpgDpi = 300;
            coordinator.ScanForTestingAsync(job).GetAwaiter().GetResult();
            Check(fake.Calls == calls + 1, "重叠路径或强制重导次数错误");
            Check(manifest.Get(source, job.Kind)!.JpgDpi == 300, "参数未保存");
        }
        finally { Directory.Delete(root, true); }
    }
    public static void Watcher()
    {
        var root = Root();
        try
        {
            using var watcher = new ChangeMonitor(); watcher.Configure([root]);
            var path = Path.Combine(root, "new.ai");
            File.WriteAllText(path, "1"); File.AppendAllText(path, "2");
            Check(SpinWait.SpinUntil(() => watcher.Snapshot().ContainsKey(path), 3000), "未收到新增事件");
            Thread.Sleep(100);
            var snapshot = watcher.Snapshot();
            Check(snapshot.Count == 1, "同源未去重");
            File.AppendAllText(path, "3");
            Check(SpinWait.SpinUntil(() => watcher.Snapshot()[path] != snapshot[path], 3000), "未捕捉后续更新");
            watcher.Acknowledge(path, snapshot[path]);
            Check(watcher.Snapshot().ContainsKey(path), "旧确认吞掉新修改");
        }
        finally { Directory.Delete(root, true); }
    }
    public static void RenderUi()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var app = new App(); app.InitializeComponent();
                var window = new MainWindow();
                var content = (System.Windows.FrameworkElement)window.Content;
                window.Content = null;
                content.Measure(new System.Windows.Size(1080, 1500));
                content.Arrange(new System.Windows.Rect(0, 0, 1080, 1500)); content.UpdateLayout();
                Check(window.Icon is not null, "窗口图标未载入");
                using var stream = typeof(MainWindow).Assembly.GetManifestResourceStream("VectorForge.Assets.VectorForge.ico");
                Check(stream is not null, "托盘图标未嵌入");
                var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(1080, 1500, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                bitmap.Render(content);
                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                var path = Path.Combine(Environment.CurrentDirectory, "artifacts", "v17-ui.png");
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                using var output = File.Create(path); encoder.Save(output);
                // No monitor enabled in isolated test settings.
                app.Shutdown();
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure is not null) throw failure;
    }

    public static void WorkerSession()
    {
        var root = Root();
        try
        {
            using var exporter = new WorkerProcessIllustratorExporter(new(), Environment.ProcessPath!, true);
            ExportRequest Request(string name, int dpi = 150) => new() { Kind = ExportKind.Jpg,
                SourcePath = Path.Combine(root, "fixture.ai"), WorkingPrefix = Path.Combine(root, name),
                OwnershipFile = Path.Combine(root, "owned.json"), JpgDpi = dpi };
            var one = Request("one"); var two = Request("two");
            Check(exporter.ExportAsync(one, TimeSpan.FromSeconds(5), default).GetAwaiter().GetResult().Success, "首次请求失败");
            Check(exporter.ExportAsync(two, TimeSpan.FromSeconds(5), default).GetAwaiter().GetResult().Success, "第二请求失败");
            Check(File.ReadAllText(one.WorkingPrefix + ".pid") == File.ReadAllText(two.WorkingPrefix + ".pid"), "没有复用 Worker");
            var timeout = exporter.ExportAsync(Request("timeout", 499), TimeSpan.FromMilliseconds(200), default).GetAwaiter().GetResult();
            Check(!timeout.Success && timeout.SafeToCleanup && timeout.ErrorCode == "FILE_TIMEOUT", "测试 Worker 超时未隔离回收");
            exporter.CloseAsync().GetAwaiter().GetResult();
            Check(exporter.ExportAsync(Request("restart"), TimeSpan.FromSeconds(5), default).GetAwaiter().GetResult().Success, "超时后不能重建会话");
            exporter.CloseAsync().GetAwaiter().GetResult();
        }
        finally { Directory.Delete(root, true); }
    }

    public static void CurrentNeighborOutputIsAdopted()
    {
        var root = Root();
        try
        {
            foreach (var kind in Enum.GetValues<ExportKind>())
            {
                var source = Path.Combine(root, $"latest-{kind}.ai");
                File.WriteAllText(source, "source");
                File.SetLastWriteTimeUtc(source, DateTime.UtcNow.AddMinutes(-10));
                var output = Path.ChangeExtension(source, kind == ExportKind.Pdf ? ".pdf" : ".jpg");
                ValidFixture.Write(output, kind);
                File.SetLastWriteTimeUtc(output, DateTime.UtcNow.AddMinutes(-5));
                var manifest = new ManifestStore(Path.Combine(root, $"{kind}.manifest.json"));
                manifest.Put(new ExportManifestEntry
                {
                    SourcePath = source,
                    Kind = kind,
                    SourceLength = 1,
                    SourceModifiedUtc = DateTime.UtcNow.AddYears(-1),
                    OutputFiles = [output]
                });
                using var coordinator = new MonitorCoordinator(new(), new(), new NeverCalledExporter(), manifest);
                Check(!coordinator.NeedsExportForTesting(new(source), kind), $"{kind} 最新输出仍被判定为重导");
                var adopted = manifest.Get(source, kind)!;
                var sourceInfo = new FileInfo(source);
                Check(adopted.SourceLength == sourceInfo.Length && adopted.SourceModifiedUtc == sourceInfo.LastWriteTimeUtc,
                    $"{kind} 清单未更新");

                File.SetLastWriteTimeUtc(source, DateTime.UtcNow);
                Check(coordinator.NeedsExportForTesting(new(source), kind), $"{kind} 源更新后未触发导出");
            }

            var suffixSource = Path.Combine(root, "legacy.ai");
            File.WriteAllText(suffixSource, "source");
            File.SetLastWriteTimeUtc(suffixSource, DateTime.UtcNow.AddMinutes(-10));
            var suffixOutput = Path.Combine(root, "legacy-01.jpg");
            ValidFixture.Write(suffixOutput, ExportKind.Jpg);
            File.SetLastWriteTimeUtc(suffixOutput, DateTime.UtcNow.AddMinutes(-5));
            var suffixManifest = new ManifestStore(Path.Combine(root, "suffix.manifest.json"));
            using var suffixCoordinator = new MonitorCoordinator(new(), new(), new NeverCalledExporter(), suffixManifest);
            Check(!suffixCoordinator.NeedsExportForTesting(new(suffixSource), ExportKind.Jpg),
                "合法画板后缀的最新 JPG 未被识别");

            var spacedSource = Path.Combine(root, "legacy with spaces.ai");
            File.WriteAllText(spacedSource, "source");
            File.SetLastWriteTimeUtc(spacedSource, DateTime.UtcNow.AddMinutes(-10));
            var hyphenOutput = Path.Combine(root, "legacy-with-spaces.jpg");
            ValidFixture.Write(hyphenOutput, ExportKind.Jpg);
            File.SetLastWriteTimeUtc(hyphenOutput, DateTime.UtcNow.AddMinutes(-5));
            var spacedManifest = new ManifestStore(Path.Combine(root, "spaced.manifest.json"));
            using var spacedCoordinator = new MonitorCoordinator(new(), new(), new NeverCalledExporter(), spacedManifest);
            Check(!spacedCoordinator.NeedsExportForTesting(new(spacedSource), ExportKind.Jpg),
                "Illustrator 把空格改成连字符后的最新 JPG 未被识别");
            Check(spacedManifest.Get(spacedSource, ExportKind.Jpg)?.OutputFiles.Single() == hyphenOutput,
                "连字符 JPG 没有被写入清单");

            var brokenSource = Path.Combine(root, "broken.ai");
            File.WriteAllText(brokenSource, "source");
            File.SetLastWriteTimeUtc(brokenSource, DateTime.UtcNow.AddMinutes(-10));
            var brokenOutput = Path.Combine(root, "broken.jpg");
            File.WriteAllText(brokenOutput, "not a jpeg");
            File.SetLastWriteTimeUtc(brokenOutput, DateTime.UtcNow.AddMinutes(-5));
            using var brokenCoordinator = new MonitorCoordinator(new(), new(), new NeverCalledExporter(),
                new ManifestStore(Path.Combine(root, "broken.manifest.json")));
            Check(brokenCoordinator.NeedsExportForTesting(new(brokenSource), ExportKind.Jpg),
                "损坏的最新 JPG 被错误跳过");
        }
        finally { Directory.Delete(root, true); }
    }

    public static void FileNameKeywordSkip()
    {
        var root = Root();
        try
        {
            var source = Path.Combine(root, "客户A-转曲打样-最终版.ai");
            File.WriteAllText(source, "source");
            var job = new MonitorJobSettings
            {
                Enabled = true,
                Kind = ExportKind.Pdf,
                RootPaths = [root],
                SkipFileNameKeywords = [" 转曲打样 ", "不用导出", "转曲打样"],
                ScanIntervalSeconds = 120
            };
            job.Normalize();
            Check(job.SkipFileNameKeywords.SequenceEqual(["转曲打样", "不用导出"]), "关键词规范化错误");
            Check(FileScanner.FindSkipKeyword(new(source), job) == "转曲打样", "未匹配文件名关键词");
            Check(FileScanner.FindSkipKeyword(new FileInfo(Path.Combine(root, "普通文件.ai")), job) is null,
                "不相关文件被误匹配");

            var fake = new CountingExporter();
            using var coordinator = new MonitorCoordinator(new(), new(), fake,
                new ManifestStore(Path.Combine(root, "keyword.manifest.json")));
            string? reason = null;
            DateTimeOffset? recordTime = null;
            coordinator.RecordAdded += (_, record) => { reason = record.Reason; recordTime = record.Time; };
            coordinator.ScanForTestingAsync(job).GetAwaiter().GetResult();
            Check(fake.Calls == 0, "命中关键词的 AI 仍被导出");
            Check(reason?.Contains("转曲打样", StringComparison.Ordinal) == true, "处理记录没有跳过原因");
            Check(recordTime.HasValue && recordTime.Value.Offset == TimeZoneInfo.Local.GetUtcOffset(recordTime.Value.DateTime),
                "界面日志时间不是电脑本地时区");
            Check(!File.Exists(Path.ChangeExtension(source, ".pdf")), "跳过后仍生成了 PDF");
        }
        finally { Directory.Delete(root, true); }
    }

    public static void StopAndRemovedPathCancelQueue()
    {
        var root = Root();
        var replacement = Root();
        try
        {
            var source = Path.Combine(root, "等待导出.ai");
            File.WriteAllText(source, "source");
            var job = new MonitorJobSettings
            {
                Enabled = true, Kind = ExportKind.Pdf, RootPaths = [root],
                ScanIntervalSeconds = 120, ModifiedWithinHours = 24
            };
            var settings = new AppSettings { PdfJob = job };
            var fake = new CountingExporter();
            using var coordinator = new MonitorCoordinator(settings, new(), fake,
                new ManifestStore(Path.Combine(root, "stop.manifest.json")));

            var stoppedScan = coordinator.ScanForTestingAsync(job);
            Thread.Sleep(200);
            job.Enabled = false;
            coordinator.StopJob(ExportKind.Pdf);
            Check(stoppedScan.Wait(TimeSpan.FromSeconds(3)), "停止任务没有立即取消稳定等待");
            Check(fake.Calls == 0, "停止后仍继续导出");

            job.Enabled = true;
            job.RootPaths = [root];
            var removedPathScan = coordinator.ScanForTestingAsync(job);
            Thread.Sleep(200);
            job.RootPaths = [replacement];
            coordinator.RefreshJob(ExportKind.Pdf);
            Check(removedPathScan.Wait(TimeSpan.FromSeconds(3)), "删除路径没有立即取消旧扫描");
            Check(fake.Calls == 0, "已删除路径中的排队文件仍被导出");
        }
        finally
        {
            Directory.Delete(root, true);
            Directory.Delete(replacement, true);
        }
    }

    public static void NonBlockingStabilityAndFairness()
    {
        var root = Root();
        try
        {
            var first = Path.Combine(root, "first.ai");
            var second = Path.Combine(root, "second.ai");
            File.WriteAllText(first, "one");
            File.WriteAllText(second, "two");
            var job = new MonitorJobSettings
            {
                Enabled = true, Kind = ExportKind.Pdf, RootPaths = [root],
                ModifiedWithinHours = 24, ScanIntervalSeconds = 120
            };

            var clock = DateTime.UtcNow;
            var scanner = new FileScanner(new()) { UtcNow = () => clock };
            var watch = System.Diagnostics.Stopwatch.StartNew();
            Check(scanner.FindStableCandidatesAsync(job, default, _ => true).Result.Count == 0,
                "首次观察不应立即判定稳定");
            Check(watch.Elapsed < TimeSpan.FromSeconds(2), "稳定观察阻塞了扫描线程");
            clock = clock.AddSeconds(29);
            Check(scanner.FindStableCandidatesAsync(job, default, _ => true).Result.Count == 0,
                "不足30秒被判定稳定");
            clock = clock.AddSeconds(2);
            Check(scanner.FindStableCandidatesAsync(job, default, _ => true).Result.Count == 2,
                "超过30秒仍未进入队列");

            var exporter = new CountingExporter();
            using var coordinator = new MonitorCoordinator(new(), new(), exporter,
                new ManifestStore(Path.Combine(root, "fair.manifest.json")));
            coordinator.DisableStabilityDelayForTesting();
            coordinator.ScanForTestingAsync(job).GetAwaiter().GetResult();
            Check(exporter.Calls == 1, "PDF 单轮占用了多个导出名额");

            var jpg = new MonitorJobSettings
            {
                Enabled = true, Kind = ExportKind.Jpg, RootPaths = [root],
                ModifiedWithinHours = 24, ScanIntervalSeconds = 120
            };
            coordinator.ScanForTestingAsync(jpg).GetAwaiter().GetResult();
            Check(exporter.Calls == 2, "JPG 没有在下一轮获得导出机会");
            coordinator.ScanForTestingAsync(job).GetAwaiter().GetResult();
            Check(exporter.Calls == 3, "PDF 剩余队列没有继续处理");
        }
        finally { Directory.Delete(root, true); }
    }
}
sealed class FailureExporter : IIllustratorExporter
{
    public int Calls { get; private set; }
    public Task<ExportResult> ExportAsync(ExportRequest request, TimeSpan timeout, CancellationToken token)
    { Calls++; return Task.FromResult(ExportResult.Failed(request.JobId, "TEST", "模拟损坏 AI")); }
}
