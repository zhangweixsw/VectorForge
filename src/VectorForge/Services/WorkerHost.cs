using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Text;

namespace VectorForge.Services;

public static class WorkerHost
{
    public static int Run()
    {
        object? app = null;
        ExportRequest? lastRequest = null;
        var shared = true;
        using var reader = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        try
        {
            string? input;
            while ((input = reader.ReadLine()) is not null)
            {
                ExportRequest? request = null;
                try
                {
                    request = AtomicJsonStore.Deserialize<ExportRequest>(input) ?? throw new IOException("无效请求");
                    lastRequest = request;
                    if (request.Shutdown) break;
                    if (app is null)
                    {
                        app = ConnectToRunningIllustrator();
                    }
                    dynamic illustrator = app;
                    shared = request.OwnedProcessId == 0 || request.SessionMode == IllustratorSessionMode.Shared;
                    var documentCount = Convert.ToInt32(illustrator.Documents.Count);
                    if (request.SessionMode == IllustratorSessionMode.Unattended && request.OwnedProcessId > 0)
                    {
                        var identities = IllustratorSessionManager.CurrentIdentities();
                        var appDirectory = Convert.ToString(illustrator.DoJavaScript("app.path.fsName")) ?? "";
                        if (identities.Count != 1 || identities[0].ProcessId != request.OwnedProcessId ||
                            !IllustratorSessionManager.MatchesApplicationDirectory(appDirectory, request.OwnedExecutable))
                        {
                            shared = true;
                            var ambiguous = ExportResult.Failed(request.JobId, "OWNERSHIP_AMBIGUOUS",
                                "无法确认 COM 会话属于专用进程，已停止接管。" +
                                $"登记 PID={request.OwnedProcessId}；当前 PID={string.Join(",", identities.Select(p => p.ProcessId))}；脚本路径={appDirectory}；程序={request.OwnedExecutable}");
                            ambiguous.SharedInstance = true; WriteResult(ambiguous); break;
                        }
                        if (documentCount > 0)
                        {
                            shared = true;
                            var takeover = ExportResult.Failed(request.JobId, "USER_TAKEOVER", "检测到非任务文档，已视为用户接管；等待 Illustrator 关闭");
                            takeover.SharedInstance = true; WriteResult(takeover); break;
                        }
                    }
                    else if (documentCount > 0) shared = true;
                    WriteResult(new ExportResult { JobId = request.JobId, Success = true, IsHandshake = true, SharedInstance = shared, Stage = "Ready" });
                    var response = Convert.ToString(illustrator.DoJavaScript(request.ReadPdfPresets
                        ? ExtendScriptBuilder.BuildReadPdfPresets()
                        : ExtendScriptBuilder.Build(request))) ?? "";
                    var result = response.StartsWith("SCREEN|", StringComparison.Ordinal)
                        ? CollectScreenOutput(request, response)
                        : request.ReadPdfPresets
                        ? ParsePresetsResponse(request.JobId, response)
                        : ParseResponse(request.JobId, response);
                    result.SharedInstance = shared;
                    result.Stage = ReadStage(request.ProgressPath);
                    WriteResult(result);
                    if (!result.Success) break;
                }
                catch (Exception ex)
                {
                    var result = ExportResult.Failed(request?.JobId ?? Guid.Empty, "WORKER_EXCEPTION", ex.Message);
                    result.SharedInstance = shared;
                    WriteResult(result);
                    break;
                }
            }
        }
        finally
        {
            if (app is not null)
            {
                try
                {
                    dynamic illustrator = app;
                    var currentProcesses = IllustratorSessionManager.CurrentIdentities();
                    if (!shared && lastRequest is { OwnedProcessId: > 0 } &&
                        currentProcesses.Count == 1 && currentProcesses[0].ProcessId == lastRequest.OwnedProcessId)
                    {
                        if (Convert.ToInt32(illustrator.Documents.Count) == 0)
                        {
                            WriteResult(new ExportResult { Success = true, ErrorCode = "SESSION_RELEASING" });
                            illustrator.Quit();
                        }
                        else
                        {
                            shared = true;
                            WriteResult(new ExportResult { SharedInstance = true, ErrorCode = "USER_TAKEOVER" });
                        }
                    }
                }
                catch { }
                if (Marshal.IsComObject(app)) Marshal.FinalReleaseComObject(app);
            }
        }
        return 0;
    }
    internal static string ReadStage(string path)
    {
        try { var stage = File.ReadAllText(path).Trim(); return stage is "Opening" or "Exporting" or "Closing" or "Completed" ? stage : "Opening"; }
        catch { return "Opening"; }
    }

    private static ExportResult CollectScreenOutput(ExportRequest request, string response)
    {
        var directory = Path.GetFullPath(Uri.UnescapeDataString(response[7..]));
        var expected = Path.GetFullPath(request.WorkingPrefix + "_pixels");
        if (request.Kind != ExportKind.Jpg || request.JpgSizing != JpgSizingMode.PixelWidth ||
            !string.Equals(directory, expected, StringComparison.OrdinalIgnoreCase) || !FileSafety.IsPlainPath(directory))
            return ExportResult.Failed(request.JobId, "SCREEN_PATH", "像素导出目录不符合任务约定");
        var files = Directory.EnumerateFiles(directory, "*.jpg", new EnumerationOptions
            { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }).ToArray();
        if (files.Length != 1 || !FileSafety.IsPlainPath(files[0]))
            return ExportResult.Failed(request.JobId, "SCREEN_OUTPUT", "像素导出未生成唯一画板 JPG");
        using (var stream = File.OpenRead(files[0]))
        {
            var decoder = new System.Windows.Media.Imaging.JpegBitmapDecoder(stream,
                System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
            if (decoder.Frames[0].PixelWidth != request.JpgPixelWidth)
                return ExportResult.Failed(request.JobId, "PIXEL_WIDTH", "导出宽度不符合像素设置");
        }
        var target = request.WorkingPrefix + ".jpg";
        File.Move(files[0], target);
        // Remove empty export folders only. Never recursively delete unknown files.
        foreach (var sub in Directory.EnumerateDirectories(directory, "*", new EnumerationOptions
            { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }).OrderByDescending(p => p.Length))
            try { if (FileSafety.IsPlainPath(sub)) Directory.Delete(sub, false); } catch { }
        try { Directory.Delete(directory, false); } catch { }
        return new ExportResult { JobId = request.JobId, Success = true, ArtboardCount = 1, TemporaryOutputs = [target] };
    }

    internal static object ConnectToRunningIllustrator()
    {
        // CreateInstance may launch a second Illustrator while the explicitly launched
        // process is still registering COM. Bind to the running object instead.
        CLSIDFromProgID("Illustrator.Application", out var clsid);
        var watch = Stopwatch.StartNew();
        while (true)
        {
            try { GetActiveObject(ref clsid, IntPtr.Zero, out var application); return application; }
            catch (COMException) when (watch.Elapsed < TimeSpan.FromSeconds(115)) { Thread.Sleep(250); }
        }
    }
    [DllImport("ole32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void CLSIDFromProgID(string progId, out Guid clsid);
    [DllImport("oleaut32.dll", PreserveSig = false)]
    private static extern void GetActiveObject(ref Guid clsid, IntPtr reserved, [MarshalAs(UnmanagedType.IUnknown)] out object application);
    public static void WriteEmergencyFailure(Exception ex) =>
        WriteResult(ExportResult.Failed(Guid.Empty, "WORKER_FATAL", ex.Message));
    private static void WriteResult(ExportResult result)
    {
        using var writer = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false), 4096, true) { AutoFlush = true };
        writer.WriteLine(AtomicJsonStore.Serialize(result).ReplaceLineEndings(""));
    }
    public static ExportResult ParseResponse(Guid jobId, string response)
    {
        var parts = response.Split('|');
        if (parts.Length < 3 || parts[0] != "OK" || !int.TryParse(parts[1], out var count) || count < 1)
            return ExportResult.Failed(jobId, "ILLUSTRATOR_SCRIPT",
                parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : response);
        return new ExportResult { JobId = jobId, Success = true, ArtboardCount = count,
            TemporaryOutputs = parts.Skip(2).Where(p => p.Length > 0).Select(Uri.UnescapeDataString).ToList() };
    }

    private static ExportResult ParsePresetsResponse(Guid jobId, string response)
    {
        if (!response.StartsWith("PRESETS|", StringComparison.Ordinal))
            return ExportResult.Failed(jobId, "ILLUSTRATOR_PRESETS", "无法读取 Illustrator PDF 预设：" + response);
        var presets = response[8..].Split('|', StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.UnescapeDataString).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return new ExportResult { JobId = jobId, Success = true, PdfPresets = presets };
    }
}
public sealed class OwnedIllustrator
{
    public int ProcessId { get; set; }
    public DateTime StartedUtc { get; set; }
    public string Executable { get; set; } = "";
    public Guid SessionId { get; set; }
}
