using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Windows.Media.Imaging;
using VectorForge;
using VectorForge.Services;

static class WidthSmoke
{
    public static int Run(string executable, string root)
    {
        root = Path.GetFullPath(root);
        if (Directory.Exists(root)) return 2;
        Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable("VECTORFORGE_DATA_DIR", Path.Combine(root, "isolated-data"));
        AppPaths.EnsureCreated();
        var source = Path.GetFullPath("tests/Fixtures/双画板测试.ai");
        var hash = SHA256.HashData(File.ReadAllBytes(source));
        var working = Path.Combine(root, "vectorforge_temp_source_" + Guid.NewGuid().ToString("N") + ".ai");
        File.Copy(source, working);
        Exception? error = null;
        var thread = new Thread(() =>
        {
            object? application = null;
            try
            {
                application = WorkerHost.ConnectToRunningIllustrator(); dynamic app = application;
                var before = Convert.ToInt32(app.Documents.Count);
                var previous = Convert.ToString(app.DoJavaScript("app.documents.length ? app.activeDocument.name : ''"));
                using var exporter = new WorkerProcessIllustratorExporter(new(), Path.GetFullPath(executable));
                exporter.Progress += text => Console.WriteLine(text);
                var prefix = Path.Combine(root, "5000px等比导出");
                var result = exporter.ExportAsync(new ExportRequest { Kind = ExportKind.Jpg, SessionMode = IllustratorSessionMode.Shared,
                    SourcePath = working, WorkingPrefix = prefix, OwnershipFile = "", JpgSizing = JpgSizingMode.PixelWidth, JpgPixelWidth = 5000 },
                    TimeSpan.FromMinutes(3), default).GetAwaiter().GetResult();
                if (!result.Success) throw new Exception(result.ErrorCode + ":" + result.ErrorMessage);
                exporter.CloseAsync().GetAwaiter().GetResult();
                using var stream = File.OpenRead(result.TemporaryOutputs.Single());
                var frame = new JpegBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
                var after = Convert.ToInt32(app.Documents.Count);
                var activeAfter = Convert.ToString(app.DoJavaScript("app.documents.length ? app.activeDocument.name : ''"));
                if (frame.PixelWidth != 5000 || Math.Abs(frame.PixelHeight - 5000.0 * 160 / 240) > 1) throw new Exception($"尺寸错误：{frame.PixelWidth}×{frame.PixelHeight}");
                if (before != after || previous != activeAfter) throw new Exception("用户文档数量或活动文档发生变化");
                if (!hash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(source)))) throw new Exception("原始样本被修改");
                AtomicJsonStore.Save(Path.Combine(root, "result.json"), new { Success = true, frame.PixelWidth, frame.PixelHeight,
                    ExistingIllustratorExport = true, BeforeDocuments = before, AfterDocuments = after, ActiveDocumentPreserved = true, SourceUnchanged = true });
                Console.WriteLine($"实机通过：已有Illustrator下导出 {frame.PixelWidth}×{frame.PixelHeight}，用户实例保留");
            }
            catch (Exception ex) { error = ex; }
            finally { if (application is not null) Marshal.FinalReleaseComObject(application); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        if (!thread.Join(TimeSpan.FromMinutes(5))) { Console.Error.WriteLine("共享实例忙，实机检查超时；用户Illustrator未结束"); return 3; }
        if (error is not null) { Console.Error.WriteLine(error); return 1; }
        File.Delete(working); return 0;
    }
}
