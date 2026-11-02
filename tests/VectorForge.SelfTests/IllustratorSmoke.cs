using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Media.Imaging;
using VectorForge;
using VectorForge.Services;

static class IllustratorSmoke
{
    public static int Run(string executable, string root)
    {
        root = Path.GetFullPath(root);
        if (Directory.Exists(root)) { Console.Error.WriteLine("实机测试目录必须是全新路径"); return 2; }
        if (IllustratorSessionManager.CurrentIdentities().Count != 0)
        { Console.Error.WriteLine("已有 Illustrator，实机检查跳过，不接管用户实例"); return 3; }
        Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable("VECTORFORGE_DATA_DIR", Path.Combine(root, "isolated-data"));
        AppPaths.EnsureCreated();
        var logger = new AppLogger(); var report = new Dictionary<string, object>();
        try
        {
            CreateFixtures(root, logger);
            var sources = new[] { Path.Combine(root, "单画板测试.ai"), Path.Combine(root, "双画板测试.ai") };
            var before = sources.ToDictionary(p => p, p => (Hash: Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))),
                Length: new FileInfo(p).Length, Modified: File.GetLastWriteTimeUtc(p)));
            var settings = new AppSettings
            {
                SessionMode = IllustratorSessionMode.Unattended,
                PdfJob = new() { Kind = ExportKind.Pdf, Enabled = true, RootPaths = [root], ModifiedWithinHours = 24 },
                JpgJob = new() { Kind = ExportKind.Jpg, Enabled = true, RootPaths = [root], ModifiedWithinHours = 24, JpgDpi = 72, JpgQuality = 50 }
            };
            settings.Normalize();
            var manifest = new ManifestStore(Path.Combine(root, "manifest.json"));
            using (var exporter = new WorkerProcessIllustratorExporter(logger, Path.GetFullPath(executable)))
            using (var coordinator = new MonitorCoordinator(settings, logger, exporter, manifest))
            {
                try
                {
                exporter.Progress += text => Console.WriteLine(text);
                coordinator.RecordAdded += (_, record) => Console.WriteLine(record.Reason + " " + Path.GetFileName(record.Path));
                coordinator.StatusChanged += (_, status) => Console.WriteLine(status.LastResult);
                var presets = coordinator.ReadPdfPresetsAsync().GetAwaiter().GetResult();
                if (presets.Count == 0) throw new Exception("本机预设列表为空");
                settings.PdfJob.PdfPreset = presets.FirstOrDefault(p => p.Contains("最小") || p.Contains("Smallest", StringComparison.OrdinalIgnoreCase)) ?? presets[0];
                report["PresetCount"] = presets.Count; report["SelectedPreset"] = settings.PdfJob.PdfPreset;
                var cases = new List<object>();
                foreach (var source in sources)
                {
                    foreach (var job in new[] { settings.PdfJob, settings.JpgJob })
                    {
                        coordinator.ExportSingleForTestingAsync(new(source), job).GetAwaiter().GetResult();
                        var entry = manifest.Get(source, job.Kind) ?? throw new Exception("没有导出成功记录：" + source + " " + job.Kind);
                        if (new OutputValidator().Check(entry.OutputFiles.Single(), job.Kind) != OutputHealth.Valid) throw new Exception("输出验证失败");
                        if (coordinator.NeedsExportForTesting(new(source), job.Kind)) throw new Exception("成功后仍需要重复导出");
                        cases.Add(new { Source = source, Kind = job.Kind.ToString(), entry.ArtboardCount, Output = entry.OutputFiles[0], Bytes = new FileInfo(entry.OutputFiles[0]).Length });
                    }
                }
                var jpg = Path.ChangeExtension(sources[1], ".jpg"); var small = ImageInfo(jpg);
                settings.JpgJob.JpgDpi = 300; settings.JpgJob.JpgQuality = 95;
                coordinator.ExportSingleForTestingAsync(new(sources[1]), settings.JpgJob).GetAwaiter().GetResult();
                var large = ImageInfo(jpg);
                if (large.Width <= small.Width || large.Height <= small.Height || large.Bytes <= small.Bytes) throw new Exception("DPI/质量变化未影响尺寸/体积");
                report["Jpg72"] = new { small.Width, small.Height, small.Bytes };
                report["Jpg300"] = new { large.Width, large.Height, large.Bytes }; report["Cases"] = cases;
                settings.JpgJob.JpgSizing = JpgSizingMode.PixelWidth; settings.JpgJob.JpgPixelWidth = 5000;
                coordinator.ExportSingleForTestingAsync(new(sources[1]), settings.JpgJob).GetAwaiter().GetResult();
                var pixels = ImageInfo(jpg);
                if (pixels.Width != 5000 || Math.Abs(pixels.Height - 5000.0 * 160 / 240) > 1)
                    throw new Exception($"像素等比导出尺寸错误：{pixels.Width}×{pixels.Height}");
                report["JpgPixelWidth5000"] = new { pixels.Width, pixels.Height, pixels.Bytes };
                var corrupt = Path.Combine(root, "损坏样本.ai"); File.WriteAllBytes(corrupt, "%PDF-1.7\n%%EOF\n"u8.ToArray());
                coordinator.ExportSingleForTestingAsync(new(corrupt), settings.PdfJob).GetAwaiter().GetResult();
                var state = AtomicJsonStore.Load(manifest.StatePath, () => new MonitorState());
                if (state.OpenFailures.Count + state.Failures.Count == 0) throw new Exception("损坏 AI 未记录失败");
                // Let Windows finish removing the just-terminated COM server before the next startup.
                if (!SpinWait.SpinUntil(() => IllustratorSessionManager.CurrentIdentities().Count == 0, 10000)) throw new Exception("损坏文件后的专用进程未退出");
                var goodPdf = Path.ChangeExtension(sources[0], ".pdf"); File.Delete(goodPdf);
                coordinator.ExportSingleForTestingAsync(new(sources[0]), settings.PdfJob).GetAwaiter().GetResult();
                if (!File.Exists(goodPdf) || new OutputValidator().Check(goodPdf, ExportKind.Pdf) != OutputHealth.Valid) throw new Exception("坏文件后未能继续导出正常文件");
                report["ContinuedAfterCorruptAi"] = true;
                }
                finally { exporter.CloseAsync().GetAwaiter().GetResult(); }
            }
            foreach (var source in sources)
            {
                var current = new FileInfo(source);
                if (before[source].Hash != Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source))) ||
                    before[source].Length != current.Length || before[source].Modified != current.LastWriteTimeUtc) throw new Exception("原始测试 AI 变化：" + source);
            }
            report["SourceHashLengthTimestampUnchanged"] = true;
            if (!SpinWait.SpinUntil(() => IllustratorSessionManager.CurrentIdentities().Count == 0, 15000)) throw new Exception("专用 Illustrator 未正常释放");
            VerifyExistingUserSession(executable, root, logger);
            report["ExistingUserDocumentAndUnsavedEditsPreserved"] = true;
            VerifyBusyRecovery(logger);
            report["OwnedBusyIllustratorTerminated"] = true;
            report["Success"] = true;
            AtomicJsonStore.Save(Path.Combine(root, "result.json"), report);
            Console.WriteLine("实机验收通过：" + root); return 0;
        }
        catch (Exception ex)
        {
            report["Success"] = false; report["Error"] = ex.ToString();
            AtomicJsonStore.Save(Path.Combine(root, "result.json"), report);
            Console.Error.WriteLine(ex); return 1;
        }
    }

    private static void CreateFixtures(string root, AppLogger logger)
    {
        var manager = new IllustratorSessionManager(logger);
        manager.Prepare(new ExportRequest { Kind = ExportKind.Pdf, SessionMode = IllustratorSessionMode.Unattended,
            SourcePath = "", WorkingPrefix = "", OwnershipFile = "" });
        Exception? error = null;
        var thread = new Thread(() =>
        {
            object? application = null;
            try
            {
                application = WorkerHost.ConnectToRunningIllustrator();
                dynamic app = application!;
                if (Convert.ToInt32(app.Documents.Count) != 0) { manager.Relinquish(); throw new Exception("启动后出现用户文档，不创建测试样本"); }
                string Quote(string path) => JsonSerializer.Serialize(path.Replace('\\', '/'));
                var script = $$"""
                    (function(){
                        function make(path, boards) {
                            var d = null;
                            try {
                                d = app.documents.add(DocumentColorSpace.RGB,240,160);
                                if(boards===2) d.artboards.add([260,160,500,0]);
                                var s=d.pathItems.rectangle(140,20,190,100);
                                var c=new RGBColor();c.red=255;c.green=154;c.blue=50;s.fillColor=c;s.stroked=false;
                                var o=new IllustratorSaveOptions();o.pdfCompatible=true;d.saveAs(new File(path),o);
                            } finally { if(d!==null)d.close(SaveOptions.DONOTSAVECHANGES); }
                        }
                        make({{Quote(Path.Combine(root, "单画板测试.ai"))}},1);
                        make({{Quote(Path.Combine(root, "双画板测试.ai"))}},2);
                        return 'OK';
                    }())
                    """;
                if (Convert.ToString(app.DoJavaScript(script)) != "OK") throw new Exception("测试样本创建失败");
                if (Convert.ToInt32(app.Documents.Count) == 0) app.Quit(); else manager.Relinquish();
            }
            catch (Exception ex) { error = ex; }
            finally { if (application is not null && Marshal.IsComObject(application)) Marshal.FinalReleaseComObject(application); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(120)))
        { manager.RecoverAsync().GetAwaiter().GetResult(); throw new TimeoutException("样本创建超过120秒"); }
        if (error is not null) { manager.RecoverAsync().GetAwaiter().GetResult(); throw error; }
        if (!SpinWait.SpinUntil(() => IllustratorSessionManager.CurrentIdentities().Count == 0, 15000))
            if (!manager.RecoverAsync().GetAwaiter().GetResult()) throw new Exception("样本创建会话无法释放");
        Console.WriteLine("已创建并关闭专用测试样本");
    }

    private static void VerifyExistingUserSession(string executable, string root, AppLogger logger)
    {
        var manager = new IllustratorSessionManager(logger);
        manager.Prepare(new ExportRequest { Kind = ExportKind.Pdf, SessionMode = IllustratorSessionMode.Unattended,
            SourcePath = "", WorkingPrefix = "", OwnershipFile = "" });
        Exception? error = null;
        var thread = new Thread(() =>
        {
            object? application = null;
            var source = Path.Combine(root, "人工打开测试样本.ai");
            var quoted = JsonSerializer.Serialize(source.Replace('\\', '/'));
            try
            {
                application = WorkerHost.ConnectToRunningIllustrator(); dynamic app = application;
                if (Convert.ToInt32(app.Documents.Count) > 0) { manager.Relinquish(); throw new Exception("启动时已有文档，跳过用户实例检查"); }
                app.DoJavaScript("(function(){var d=app.documents.add(DocumentColorSpace.RGB,100,100);d.saveAs(new File(" + quoted + "),new IllustratorSaveOptions());d.pathItems.rectangle(80,10,50,50);return d.name;}())");
                var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source)));
                using var exporter = new WorkerProcessIllustratorExporter(logger, Path.GetFullPath(executable));
                var presets = exporter.ReadPdfPresetsAsync().GetAwaiter().GetResult();
                if (presets.Count == 0) throw new Exception("用户实例未读到预设");
                var attempt = exporter.ExportAsync(new ExportRequest { Kind = ExportKind.Pdf,
                    SessionMode = IllustratorSessionMode.Unattended, SourcePath = source, WorkingPrefix = Path.Combine(root, "禁止导出"), OwnershipFile = "" },
                    TimeSpan.FromSeconds(10), default).GetAwaiter().GetResult();
                if (attempt.Success || attempt.ErrorCode != "WAITING_USER") throw new Exception("无人值守接管了已有用户实例");
                var working = Path.Combine(root, "shared-test-copy.ai"); File.Copy(source, working);
                var shared = exporter.ExportAsync(new ExportRequest { Kind = ExportKind.Jpg,
                    SessionMode = IllustratorSessionMode.Shared, SourcePath = working, WorkingPrefix = Path.Combine(root, "用户操作期间导出"), OwnershipFile = "",
                    JpgSizing = JpgSizingMode.PixelWidth, JpgPixelWidth = 5000 }, TimeSpan.FromMinutes(2), default).GetAwaiter().GetResult();
                if (!shared.Success || ImageInfo(shared.TemporaryOutputs.Single()).Width != 5000) throw new Exception("用户文档打开时无法等比导出工作副本");
                exporter.CloseAsync().GetAwaiter().GetResult();
                if (Convert.ToInt32(app.Documents.Count) != 1 || Convert.ToString(app.DoJavaScript("String(app.activeDocument.saved)")) != "false")
                    throw new Exception("用户文档或未保存状态改变");
                if (hash != Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source)))) throw new Exception("用户实例原文件被保存");
                Console.WriteLine("已有用户实例：预设可读取，始终导出模式可生成5000px图片，未保存编辑保留");
            }
            catch (Exception ex) { error = ex; }
            finally
            {
                if (application is not null)
                {
                    try
                    {
                        dynamic app = application;
                        app.DoJavaScript("(function(){for(var i=app.documents.length-1;i>=0;i--){try{if(app.documents[i].fullName.fsName.toLowerCase()===new File(" + quoted + ").fsName.toLowerCase())app.documents[i].close(SaveOptions.DONOTSAVECHANGES);}catch(e){}}}())");
                        if (Convert.ToInt32(app.Documents.Count) == 0) app.Quit(); else manager.Relinquish();
                    }
                    catch { }
                    Marshal.FinalReleaseComObject(application);
                }
            }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(120)))
        { manager.RecoverAsync().GetAwaiter().GetResult(); throw new TimeoutException("用户实例保护实机检查超时"); }
        if (error is not null) { manager.RecoverAsync().GetAwaiter().GetResult(); throw error; }
        if (!SpinWait.SpinUntil(() => IllustratorSessionManager.CurrentIdentities().Count == 0, 15000))
            if (!manager.RecoverAsync().GetAwaiter().GetResult()) throw new Exception("用户实例测试会话未释放");
    }

    private static void VerifyBusyRecovery(AppLogger logger)
    {
        var manager = new IllustratorSessionManager(logger);
        manager.Prepare(new ExportRequest { Kind = ExportKind.Pdf, SessionMode = IllustratorSessionMode.Unattended,
            SourcePath = "", WorkingPrefix = "", OwnershipFile = "" });
        using var entered = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            object? application = null;
            try
            {
                application = WorkerHost.ConnectToRunningIllustrator();
                dynamic app = application!;
                if (Convert.ToInt32(app.Documents.Count) > 0) { manager.Relinquish(); return; }
                app.DoJavaScript("app.name"); entered.Set();
                // Dedicated empty test session only; parent forcibly recovers this deliberate blocked script.
                app.DoJavaScript("(function(){while(true){}}())");
            }
            catch { }
            finally { if (application is not null && Marshal.IsComObject(application)) Marshal.FinalReleaseComObject(application); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        try
        {
            if (!entered.Wait(TimeSpan.FromSeconds(120))) throw new Exception("阻塞恢复测试未进入专用会话");
            Thread.Sleep(1500);
            if (!manager.RecoverAsync().GetAwaiter().GetResult() || !thread.Join(TimeSpan.FromSeconds(15))) throw new Exception("无法结束自己启动的阻塞 Illustrator");
            Console.WriteLine("专用 Illustrator 脚本阻塞后强制回收通过");
        }
        finally { manager.RecoverAsync().GetAwaiter().GetResult(); }
    }

    private static (int Width, int Height, long Bytes) ImageInfo(string path)
    {
        using var stream = File.OpenRead(path);
        var decoder = new JpegBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        return (decoder.Frames[0].PixelWidth, decoder.Frames[0].PixelHeight, stream.Length);
    }
}
