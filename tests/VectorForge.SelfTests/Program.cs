using VectorForge;
using VectorForge.Services;

if (args.Length >= 3 && args[0] == "--width-smoke") return WidthSmoke.Run(args[1], args[2]);

if (args.Length >= 3 && args[0] == "--illustrator-smoke")
    return IllustratorSmoke.Run(args[1], args[2]);

if (args.Contains("--illustrator-fixture"))
{
    Thread.Sleep(60000);
    return 0;
}

if (args.Contains("--worker"))
{
    string? line;
    while ((line = Console.ReadLine()) is not null)
    {
        var request = AtomicJsonStore.Deserialize<ExportRequest>(line)!;
        if (request.JpgDpi == 490) { Thread.Sleep(10000); continue; }
        if (request.JpgDpi == 493) Thread.Sleep(400);
        Console.WriteLine(AtomicJsonStore.Serialize(new ExportResult { JobId = request.JobId, Success = true, IsHandshake = true }).ReplaceLineEndings(""));
        if (request.JpgDpi is 498 or 497 or 496)
        {
            File.WriteAllText(request.ProgressPath, request.JpgDpi == 498 ? "Opening" : request.JpgDpi == 497 ? "Exporting" : "Closing");
            Thread.Sleep(10000);
        }
        if (request.JpgDpi == 492)
            for (var count = 0; count < 30; count++) { File.WriteAllText(request.ProgressPath, count % 2 == 0 ? "Opening" : "Exporting"); Thread.Sleep(50); }
        if (request.JpgDpi == 493) Thread.Sleep(80);
        if (request.JpgDpi == 499) Thread.Sleep(10000);
        File.WriteAllText(request.WorkingPrefix + ".pid", Environment.ProcessId.ToString());
        var response = new ExportResult { JobId = request.JobId, Success = true, ArtboardCount = 1,
            TemporaryOutputs = [request.WorkingPrefix + ".jpg"] };
        Console.WriteLine(AtomicJsonStore.Serialize(response).ReplaceLineEndings(""));
    }
    return 0;
}

Environment.SetEnvironmentVariable("VECTORFORGE_DATA_DIR", Path.Combine(Path.GetTempPath(), "VectorForgeTestsData", Guid.NewGuid().ToString("N")));

var tests = new (string Name, Action Body)[]
{
    ("签名更新清单及下载摘要拒绝篡改", ReleaseTests.SignedUpdates),
    ("v17 延后重试、重启持久化、失败TXT及恢复清理", V17Tests.RetriesAndNotes),
    ("v17 打开失败同时暂缓PDF/JPG，普通任务优先", V17Tests.OpenCooldownAndPriority),
    ("v17 隐藏根目录、子目录、AI、输出及排队后隐藏", V17Tests.HiddenPaths),
    ("v17 隐藏目录不误清理，恢复可见后重新确认缺失", V17Tests.HiddenCleanup),
    ("v17 失败说明归属、长文件名及用户文件保护", V17Tests.NoteSafety),
    ("v17 工作模式设置持久化及旧配置迁移", V17Tests.SessionSettings),
    ("v17 卡住期间继续扫描且导出严格串行", V17Tests.BackgroundScan),
    ("v17 重试前复查路径、小时范围、关键词及隐藏属性", V17Tests.RetryScope),
    ("v17 进程归属核对、专用进程回收和用户实例保护", V17Tests.SessionOwnership),
    ("v17 分阶段超时、绝对截止时间与启动截止时间", V17Tests.WorkerDeadlines),
    ("v7 输出损坏及占用延后", V7Tests.OutputHealth),
    ("三次延后失败挂起、重启、手动恢复", V7Tests.FailureSuspension),
    ("v7 短暂缺失恢复不删除", V7Tests.SourceRecovery),
    ("v7 多路径隔离与参数重导", V7Tests.PathsAndForce),
    ("v7 文件通知去重", V7Tests.Watcher),
    ("v7 UI与嵌入图标", V7Tests.RenderUi),
    ("v7 Worker会话复用与超时隔离", V7Tests.WorkerSession),
    ("v8 旁边已有最新输出时跳过", V7Tests.CurrentNeighborOutputIsAdopted),
    ("v10 文件名关键词跳过导出", V7Tests.FileNameKeywordSkip),
    ("v11 停止任务及删除路径取消旧队列", V7Tests.StopAndRemovedPathCancelQueue),
    ("v14 非阻塞稳定检测与PDF/JPG轮流调度", V7Tests.NonBlockingStabilityAndFairness),
    ("PDF/JPG 连续扫描及重启跳过，源更新及输出缺失补导", TestRepeatedScans),
    ("JPG 精确同名", () => Assert(OutputMatcher.IsRecognizedLegacyJpgName("产品A", "产品A.jpg"))),
    ("JPG 编号后缀", () => Assert(OutputMatcher.IsRecognizedLegacyJpgName("产品A", "产品A-01.jpg"))),
    ("JPG 下划线后缀", () => Assert(OutputMatcher.IsRecognizedLegacyJpgName("产品A", "产品A_02.jpeg"))),
    ("JPG 画板后缀", () => Assert(OutputMatcher.IsRecognizedLegacyJpgName("产品A", "产品A-画板3.jpg"))),
    ("JPG 本软件画板后缀", () => Assert(OutputMatcher.IsRecognizedLegacyJpgName("产品A", "产品A__AB003.jpg"))),
    ("Illustrator 空格转连字符 JPG", () => Assert(OutputMatcher.IsRecognizedLegacyJpgName("产品 A 彩盒", "产品-A-彩盒.jpg"))),
    ("相似文件不得误认", () => Assert(!OutputMatcher.IsRecognizedLegacyJpgName("产品A", "产品AB-01.jpg"))),
    ("百分比相似不得误认", () => Assert(!OutputMatcher.IsRecognizedLegacyJpgName("abcdefghij", "abcdefghiX.jpg"))),
    ("Worker 成功响应", TestWorkerSuccess),
    ("Worker 错误响应", TestWorkerFailure),
    ("Worker 中文路径响应", TestWorkerChinesePath),
    ("设置范围归一化", TestSettingsNormalization),
    ("v15 设置重启持久化及损坏恢复", TestSettingsPersistenceAndRecovery),
    ("旧路径和分钟配置迁移", TestLegacySettingsMigration),
    ("多路径去重", TestMultiplePathNormalization),
    ("旧30天配置迁移为720小时", TestLegacyThirtyDayMigration),
    ("自定义小时严格按最后修改时间筛选", TestStrictLastModifiedWindow),
    ("一对一多余输出识别", TestRedundantOutputRecognition),
    ("删除AI后清理对应输出", TestDeletedSourceCleanup),
    ("删除AI时拒绝危险清单路径", TestDeletedSourceCleanupSafety),
    ("修改后使用AI工作副本重导", TestWorkingCopyReexport),
    ("Manifest 新鲜度", TestManifestFreshness),
    ("未修改AI重启后不重复导出", TestUnchangedSourceIsSkippedAfterRestart),
    ("JSX 永不保存 AI", TestScriptSafety)
};

var failures = new List<string>();
foreach (var test in tests)
{
    try
    {
        test.Body();
        Console.WriteLine($"PASS  {test.Name}");
    }
    catch (Exception ex)
    {
        failures.Add($"FAIL  {test.Name}: {ex.Message}");
        Console.WriteLine(failures[^1]);
    }
}

Console.WriteLine($"完成：{tests.Length - failures.Count}/{tests.Length} 通过");
return failures.Count == 0 ? 0 : 1;

static void TestWorkerSuccess()
{
    var id = Guid.NewGuid();
    var result = WorkerHost.ParseResponse(id, "OK|2|C%3A%2Ftmp%2Fa.jpg|C%3A%2Ftmp%2Fb.jpg");
    Assert(result.Success);
    Assert(result.ArtboardCount == 2);
    Assert(result.TemporaryOutputs.Count == 2);
}

static void TestRepeatedScans()
{
    var root = Path.Combine(Path.GetTempPath(), "VectorForgeRepeat", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
        foreach (var kind in new[] { ExportKind.Pdf, ExportKind.Jpg })
        {
            var sourcePath = Path.Combine(root, kind + ".ai");
            File.WriteAllText(sourcePath, "saved source");
            // Reproduces the old endless-export condition: source time ahead of output.
            File.SetLastWriteTimeUtc(sourcePath, DateTime.UtcNow.AddHours(1));
            var manifestPath = Path.Combine(root, kind + ".json");
            var exporter = new CountingExporter();
            var job = new MonitorJobSettings { Kind = kind, RootPaths = [root] };
            using (var coordinator = new MonitorCoordinator(new AppSettings(), new AppLogger(), exporter,
                       new ManifestStore(manifestPath)))
            {
                for (var scan = 0; scan < 3; scan++)
                    if (coordinator.NeedsExportForTesting(new FileInfo(sourcePath), kind))
                        coordinator.ExportSingleForTestingAsync(new FileInfo(sourcePath), job).GetAwaiter().GetResult();
                Check(exporter.Calls == 1, $"{kind} 未修改却重复导出");
            }

            var output = Path.ChangeExtension(sourcePath, kind == ExportKind.Pdf ? ".pdf" : ".jpg");
            File.SetLastWriteTimeUtc(output, DateTime.UtcNow.AddYears(-1));
            using var restarted = new MonitorCoordinator(new AppSettings(), new AppLogger(), exporter,
                new ManifestStore(manifestPath));
            Check(!restarted.NeedsExportForTesting(new FileInfo(sourcePath), kind), "输出时间变化或重启不应触发重导");

            File.SetLastWriteTimeUtc(sourcePath, DateTime.UtcNow.AddHours(2));
            Check(restarted.NeedsExportForTesting(new FileInfo(sourcePath), kind), "同大小源修改应触发重导");
            restarted.ExportSingleForTestingAsync(new FileInfo(sourcePath), job).GetAwaiter().GetResult();
            Check(!restarted.NeedsExportForTesting(new FileInfo(sourcePath), kind), "更新成功后应跳过");
            File.Delete(output);
            Check(restarted.NeedsExportForTesting(new FileInfo(sourcePath), kind), "输出丢失应补导");
            restarted.ExportSingleForTestingAsync(new FileInfo(sourcePath), job).GetAwaiter().GetResult();
            Check(exporter.Calls == 3, "导出调用数不正确");
            File.WriteAllText(output, "");
            Check(restarted.NeedsExportForTesting(new FileInfo(sourcePath), kind), "空输出应补导");
            Check(File.ReadAllText(sourcePath) == "saved source", "源 AI 被改写");
        }
    }
    finally { Directory.Delete(root, true); }
}

static void TestWorkerFailure()
{
    var result = WorkerHost.ParseResponse(Guid.NewGuid(), "ERR|bad%20file");
    Assert(!result.Success);
    Assert(result.ErrorMessage == "bad file");
}

static void TestWorkerChinesePath()
{
    var id = Guid.NewGuid();
    var encoded = Uri.EscapeDataString(@"F:\监控导图和pdf\产品一.jpg");
    var result = WorkerHost.ParseResponse(id, $"OK|1|{encoded}");
    Assert(result.Success);
    Assert(result.TemporaryOutputs.Single() == @"F:\监控导图和pdf\产品一.jpg");
}

static void TestSettingsNormalization()
{
    var settings = new MonitorJobSettings
    {
        Kind = ExportKind.Jpg,
        ModifiedWithinDays = 99,
        ScanIntervalSeconds = 100000,
        TimeoutMinutes = 100,
        JpgDpi = 999,
        JpgQuality = -1
    };
    settings.Normalize();
    Assert(settings.ModifiedWithinHours == 24);
    Assert(settings.ModifiedWithinDays == 0);
    Assert(settings.ScanIntervalSeconds == 86400);
    Assert(settings.TimeoutMinutes == 60);
    Assert(settings.JpgDpi == 500);
    Assert(settings.JpgQuality == 0);
}

static void TestSettingsPersistenceAndRecovery()
{
    var primary = AppPaths.SettingsFile;
    var backup = primary + ".last-good.json";
    try
    {
        File.Delete(primary);
        File.Delete(backup);
        var service = new SettingsService();
        var updated = new AppSettings
        {
            PdfJob = new MonitorJobSettings
            {
                Kind = ExportKind.Pdf, RootPaths = [@"C:\持久化测试"],
                ModifiedWithinHours = 37, ScanIntervalSeconds = 45,
                SkipFileNameKeywords = ["转曲打样"]
            },
            JpgJob = new MonitorJobSettings { Kind = ExportKind.Jpg, ModifiedWithinHours = 12 }
        };
        service.Commit(updated);
        var restarted = new SettingsService();
        Assert(restarted.Settings.PdfJob.ModifiedWithinHours == 37);
        Assert(restarted.Settings.PdfJob.RootPaths.Single() == @"C:\持久化测试");
        Assert(restarted.Settings.PdfJob.SkipFileNameKeywords.Single() == "转曲打样");
        Assert(File.Exists(backup));

        File.WriteAllText(primary, "{损坏的设置");
        var recovered = new SettingsService();
        Assert(recovered.Settings.PdfJob.ModifiedWithinHours == 37);
        Assert(Directory.EnumerateFiles(Path.GetDirectoryName(primary)!, "settings.json.corrupt-*").Any());
    }
    finally
    {
        File.Delete(primary);
        File.Delete(backup);
        foreach (var path in Directory.EnumerateFiles(Path.GetDirectoryName(primary)!, "settings.json.corrupt-*")) File.Delete(path);
        _ = new SettingsService();
    }
}

static void TestLegacySettingsMigration()
{
    var settings = new MonitorJobSettings
    {
        RootPath = @"C:\旧监控路径",
        ScanIntervalMinutes = 2
    };
    settings.Normalize();
    Assert(settings.RootPaths.SequenceEqual([@"C:\旧监控路径"]));
    Assert(settings.ScanIntervalSeconds == 120);
    Assert(settings.RootPath == "");
    Assert(settings.ScanIntervalMinutes == 0);
}

static void TestLegacyThirtyDayMigration()
{
    var settings = new MonitorJobSettings { ModifiedWithinDays = 30 };
    settings.Normalize();
    Assert(settings.ModifiedWithinHours == 720);
    Assert(settings.ModifiedWithinDays == 0);
    Assert(settings.ScanIntervalSeconds == 120);
}

static void TestMultiplePathNormalization()
{
    var settings = new MonitorJobSettings
    {
        RootPaths = [@"C:\作品", @"c:\作品", @"  D:\素材  "]
    };
    settings.Normalize();
    Assert(settings.RootPaths.Count == 2);
    Assert(settings.RootPaths[1] == @"D:\素材");
}

static void TestStrictLastModifiedWindow()
{
    var root = Path.Combine(Path.GetTempPath(), "VectorForgeNewFileTests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
        var sourcePath = Path.Combine(root, "从旧目录复制.ai");
        File.WriteAllText(sourcePath, "source");
        File.SetLastWriteTimeUtc(sourcePath, DateTime.UtcNow.AddHours(-7));
        var source = new FileInfo(sourcePath);
        Assert(!FileScanner.IsWithinDateWindow(source, DateTime.UtcNow.AddHours(-6)));
        File.SetLastWriteTimeUtc(sourcePath, DateTime.UtcNow.AddHours(-5));
        source.Refresh();
        Assert(FileScanner.IsWithinDateWindow(source, DateTime.UtcNow.AddHours(-6)));

        File.SetLastWriteTimeUtc(sourcePath, DateTime.UtcNow.AddYears(-2));
        var callbackCalled = false;
        var candidates = new FileScanner(new AppLogger()).FindStableCandidatesAsync(
            new MonitorJobSettings { RootPaths = [root], ModifiedWithinHours = 6 },
            CancellationToken.None,
            _ => { callbackCalled = true; return true; },
            new HashSet<string>([sourcePath], StringComparer.OrdinalIgnoreCase)).GetAwaiter().GetResult();
        Assert(candidates.Count == 0);
        Assert(!callbackCalled);
    }
    finally
    {
        Directory.Delete(root, true);
    }
}

static void TestRedundantOutputRecognition()
{
    var root = Path.Combine(Path.GetTempPath(), "VectorForgeOutputTests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
        var sourcePath = Path.Combine(root, "设计.ai");
        File.WriteAllText(sourcePath, "source");
        File.WriteAllText(Path.Combine(root, "设计.pdf"), "pdf");
        File.WriteAllText(Path.Combine(root, "设计__AB001.pdf"), "pdf");
        File.WriteAllText(Path.Combine(root, "设计-01.jpg"), "jpg");
        File.WriteAllText(Path.Combine(root, "设计其它.pdf"), "unrelated");

        var source = new FileInfo(sourcePath);
        var pdfs = OutputMatcher.FindRecognizedOutputs(source, ExportKind.Pdf);
        var jpgs = OutputMatcher.FindRecognizedOutputs(source, ExportKind.Jpg);
        Assert(pdfs.Count == 2);
        Assert(jpgs.Count == 1);
        Assert(!pdfs.Concat(jpgs).Any(path => path.EndsWith(".ai", StringComparison.OrdinalIgnoreCase)));
        Assert(!pdfs.Any(path => path.EndsWith("设计其它.pdf", StringComparison.OrdinalIgnoreCase)));

        var entry = new ExportManifestEntry
        {
            SourcePath = sourcePath,
            Kind = ExportKind.Pdf,
            SourceLength = source.Length,
            SourceModifiedUtc = source.LastWriteTimeUtc,
            OutputFiles = pdfs.ToList()
        };
        Assert(!OutputMatcher.ManifestIsCurrent(entry, source, ExportKind.Pdf));
    }
    finally
    {
        Directory.Delete(root, true);
    }
}

static void TestWorkingCopyReexport()
{
    var root = Path.Combine(Path.GetTempPath(), "VectorForgeWorkingCopyTests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
        var sourcePath = Path.Combine(root, "最新 设计.ai");
        var sourceBytes = Enumerable.Range(0, 1024).Select(i => (byte)(i % 251)).ToArray();
        File.WriteAllBytes(sourcePath, sourceBytes);
        File.WriteAllBytes(Path.Combine(root, "最新设计__AB001.jpg"), [0xFF, 0xD8, 0xFF, 0x01]);
        File.WriteAllBytes(Path.Combine(root, "最新设计__AB002.jpg"), [0xFF, 0xD8, 0xFF, 0x02]);

        var fake = new WorkingCopyExporter(sourcePath, sourceBytes);
        var manifest = new ManifestStore(Path.Combine(root, "manifest.json"));
        using var coordinator = new MonitorCoordinator(new AppSettings(), new AppLogger(), fake, manifest);
        var job = new MonitorJobSettings { Kind = ExportKind.Jpg, JpgDpi = 150, JpgQuality = 80, RootPaths = [root] };
        coordinator.ExportSingleForTestingAsync(new FileInfo(sourcePath), job).GetAwaiter().GetResult();

        Check(fake.UsedWorkingCopy, "导出器没有收到工作副本");
        Check(fake.TempNamesSafe, "临时路径仍可能被 Illustrator 改名");
        Check(File.ReadAllBytes(sourcePath).SequenceEqual(sourceBytes), "源 AI 字节发生变化");
        Check(File.Exists(Path.Combine(root, "最新 设计.jpg")), "最终 JPG 不存在");
        Check(!Directory.EnumerateFiles(root, "vectorforge_temp_*", SearchOption.TopDirectoryOnly).Any(), "临时文件未清理");
    }
    finally
    {
        Directory.Delete(root, true);
    }
}

static void TestDeletedSourceCleanup()
{
    var root = Path.Combine(Path.GetTempPath(), "VectorForgeDeletedSourceTests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
        var sourcePath = Path.Combine(root, "已删除设计.ai");
        var outputPath = Path.Combine(root, "已删除设计.pdf");
        var unrelatedPath = Path.Combine(root, "已删除设计说明.pdf");
        File.WriteAllText(outputPath, "managed");
        File.WriteAllText(unrelatedPath, "unrelated");

        var manifest = new ManifestStore(Path.Combine(root, "manifest.json"));
        manifest.Put(new ExportManifestEntry
        {
            SourcePath = sourcePath,
            Kind = ExportKind.Pdf,
            OutputFiles = [outputPath]
        });
        using var coordinator = new MonitorCoordinator(new AppSettings(), new AppLogger(), new NeverCalledExporter(), manifest);
        var job = new MonitorJobSettings { Kind = ExportKind.Pdf, RootPaths = [root] };

        var now = DateTimeOffset.UtcNow;
        coordinator.Now = () => now;
        Assert(coordinator.ReconcileDeletedSourcesForTesting(job) == 0);
        Assert(File.Exists(outputPath));
        now = now.AddSeconds(31);
        var deleted = coordinator.ReconcileDeletedSourcesForTesting(job);
        Assert(deleted == 1);
        Assert(!File.Exists(outputPath));
        Assert(File.Exists(unrelatedPath));
        Assert(manifest.Get(sourcePath, ExportKind.Pdf) is null);
    }
    finally
    {
        Directory.Delete(root, true);
    }
}

static void TestDeletedSourceCleanupSafety()
{
    var root = Path.Combine(Path.GetTempPath(), "VectorForgeCleanupSafetyTests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
        var sourcePath = Path.Combine(root, "不存在.ai");
        var protectedAi = Path.Combine(root, "绝不删除.ai");
        File.WriteAllText(protectedAi, "protected");

        var manifest = new ManifestStore(Path.Combine(root, "manifest.json"));
        manifest.Put(new ExportManifestEntry
        {
            SourcePath = sourcePath,
            Kind = ExportKind.Pdf,
            OutputFiles = [protectedAi]
        });
        using var coordinator = new MonitorCoordinator(new AppSettings(), new AppLogger(), new NeverCalledExporter(), manifest);
        var job = new MonitorJobSettings { Kind = ExportKind.Pdf, RootPaths = [root] };

        Assert(coordinator.ReconcileDeletedSourcesForTesting(job) == 0);
        Assert(File.Exists(protectedAi));
        coordinator.Now = () => DateTimeOffset.UtcNow.AddMinutes(1);
        Assert(coordinator.ReconcileDeletedSourcesForTesting(job) == 0);
        Assert(File.ReadAllText(protectedAi) == "protected");
        Assert(manifest.Get(sourcePath, ExportKind.Pdf) is not null);
    }
    finally
    {
        Directory.Delete(root, true);
    }
}

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static void TestManifestFreshness()
{
    var root = Path.Combine(Path.GetTempPath(), "VectorForgeSelfTests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
        var sourcePath = Path.Combine(root, "测试.ai");
        var outputPath = Path.Combine(root, "测试.pdf");
        File.WriteAllText(sourcePath, "ai");
        File.WriteAllText(outputPath, "pdf");
        File.SetLastWriteTimeUtc(outputPath, DateTime.UtcNow.AddSeconds(2));
        var source = new FileInfo(sourcePath);
        var entry = new ExportManifestEntry
        {
            SourcePath = sourcePath,
            Kind = ExportKind.Pdf,
            SourceLength = source.Length,
            SourceModifiedUtc = source.LastWriteTimeUtc,
            OutputFiles = [outputPath]
        };
        Assert(OutputMatcher.ManifestIsCurrent(entry, source, ExportKind.Pdf));
        File.AppendAllText(sourcePath, "changed");
        source.Refresh();
        Assert(!OutputMatcher.ManifestIsCurrent(entry, source, ExportKind.Pdf));
    }
    finally
    {
        Directory.Delete(root, true);
    }
}

static void TestUnchangedSourceIsSkippedAfterRestart()
{
    var root = Path.Combine(Path.GetTempPath(), "VectorForgeNoRepeatTests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
        var sourcePath = Path.Combine(root, "未修改设计.ai");
        var outputPath = Path.Combine(root, "未修改设计.pdf");
        var manifestPath = Path.Combine(root, "manifest.json");
        File.WriteAllText(sourcePath, "unchanged source");
        ValidFixture.Write(outputPath, ExportKind.Pdf);
        File.SetLastWriteTimeUtc(outputPath, DateTime.UtcNow.AddSeconds(2));
        var source = new FileInfo(sourcePath);

        new ManifestStore(manifestPath).Put(new ExportManifestEntry
        {
            SourcePath = sourcePath,
            Kind = ExportKind.Pdf,
            SourceLength = source.Length,
            SourceModifiedUtc = source.LastWriteTimeUtc,
            OutputFiles = [outputPath]
        });

        var reloadedManifest = new ManifestStore(manifestPath);
        using var coordinator = new MonitorCoordinator(
            new AppSettings(), new AppLogger(), new NeverCalledExporter(), reloadedManifest);
        Assert(!coordinator.NeedsExportForTesting(new FileInfo(sourcePath), ExportKind.Pdf));
    }
    finally
    {
        Directory.Delete(root, true);
    }
}

static void TestScriptSafety()
{
    var request = new ExportRequest
    {
        Kind = ExportKind.Pdf,
        SourcePath = @"C:\测试\源.ai",
        WorkingPrefix = @"C:\测试\.tmp",
        OwnershipFile = @"C:\测试\owned.json"
    };
    var script = ExtendScriptBuilder.Build(request);
    Assert(script.Contains("SaveOptions.DONOTSAVECHANGES", StringComparison.Ordinal));
    Assert(!script.Contains("doc.save();", StringComparison.Ordinal));
    Assert(script.Contains("SOURCE_OPEN", StringComparison.Ordinal));
    Assert(script.Contains("previousDoc.activate()", StringComparison.Ordinal));

    var jpgRequest = new ExportRequest
    {
        Kind = ExportKind.Jpg,
        SourcePath = @"C:\测试\源.ai",
        WorkingPrefix = @"C:\测试\jpg-temp",
        OwnershipFile = @"C:\测试\owned.json"
    };
    var jpgScript = ExtendScriptBuilder.Build(jpgRequest);
    Assert(jpgScript.Contains("setActiveArtboardIndex(0)", StringComparison.Ordinal));
    Assert(jpgScript.Contains("return \"OK|1|\"", StringComparison.Ordinal));
}

static void Assert(bool condition)
{
    if (!condition) throw new InvalidOperationException("断言失败");
}

sealed class WorkingCopyExporter(string originalPath, byte[] expectedBytes) : IIllustratorExporter
{
    public bool UsedWorkingCopy { get; private set; }
    public bool TempNamesSafe { get; private set; }

    public Task<ExportResult> ExportAsync(ExportRequest request, TimeSpan timeout, CancellationToken cancellationToken)
    {
        UsedWorkingCopy = !string.Equals(request.SourcePath, originalPath, StringComparison.OrdinalIgnoreCase)
            && request.SourcePath.EndsWith(".ai", StringComparison.OrdinalIgnoreCase)
            && File.Exists(request.SourcePath)
            && File.ReadAllBytes(request.SourcePath).SequenceEqual(expectedBytes);
        TempNamesSafe = Path.GetFileName(request.SourcePath).StartsWith("vectorforge_temp_source_", StringComparison.Ordinal)
            && Path.GetFileNameWithoutExtension(request.SourcePath).All(ch => char.IsAsciiLetterOrDigit(ch) || ch == '_')
            && Path.GetFileName(request.WorkingPrefix).StartsWith("vectorforge_temp_output_", StringComparison.Ordinal)
            && Path.GetFileName(request.WorkingPrefix).All(ch => char.IsAsciiLetterOrDigit(ch) || ch == '_');
        var output = request.WorkingPrefix + ".jpg";
        ValidFixture.Write(output, ExportKind.Jpg);
        return Task.FromResult(new ExportResult
        {
            JobId = request.JobId,
            Success = true,
            ArtboardCount = 1,
            TemporaryOutputs = [output]
        });
    }
}

sealed class NeverCalledExporter : IIllustratorExporter
{
    public Task<ExportResult> ExportAsync(ExportRequest request, TimeSpan timeout, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("此测试不应调用导出器");
}

sealed class CountingExporter : IIllustratorExporter
{
    public int Calls { get; private set; }
    public Task<ExportResult> ExportAsync(ExportRequest request, TimeSpan timeout, CancellationToken cancellationToken)
    {
        Calls++;
        var output = request.WorkingPrefix + (request.Kind == ExportKind.Pdf ? ".pdf" : ".jpg");
        ValidFixture.Write(output, request.Kind);
        return Task.FromResult(new ExportResult
        {
            JobId = request.JobId, Success = true, ArtboardCount = 1, TemporaryOutputs = [output]
        });
    }
}
