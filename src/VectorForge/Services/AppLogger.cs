using System.Text;

namespace VectorForge.Services;

public sealed class AppLogger
{
    private readonly object _gate = new();
    private DateTime _lastCleanup;

    public void Info(string message) => Write("INFO", message);
    public void Error(string message) => Write("ERROR", message);

    private void Write(string level, string message)
    {
        try
        {
            AppPaths.EnsureCreated();
            var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz} [{level}] {message}{Environment.NewLine}";
            lock (_gate)
            {
                if (_lastCleanup.Date != DateTime.Today)
                {
                    _lastCleanup = DateTime.Today;
                    foreach (var path in Directory.EnumerateFiles(AppPaths.LogDirectory, "VectorForge_*.log"))
                    {
                        var name = Path.GetFileNameWithoutExtension(path)["VectorForge_".Length..];
                        if (DateTime.TryParseExact(name, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                            System.Globalization.DateTimeStyles.None, out var day) && day < DateTime.Today.AddDays(-30) &&
                            FileSafety.IsPlainPath(path))
                            try { File.Delete(path); } catch { }
                    }
                }
                File.AppendAllText(
                    Path.Combine(AppPaths.LogDirectory, $"VectorForge_{DateTime.Now:yyyy-MM-dd}.log"),
                    line,
                    Encoding.UTF8);
            }
        }
        catch
        {
            // Logging must never stop the monitor.
        }
    }

    public void WriteFailure(string sourcePath, ExportKind kind, IReadOnlyList<string> errors, IReadOnlyList<string>? notices = null)
    {
        try
        {
            var desktop = Environment.GetEnvironmentVariable("VECTORFORGE_DATA_DIR") ??
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            var target = Path.Combine(desktop, $"VectorForge_导出失败_{DateTime.Now:yyyy-MM-dd}.txt");
            var block = new StringBuilder()
                .AppendLine(new string('=', 72))
                .AppendLine($"时间：{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}")
                .AppendLine($"类型：{kind.ToString().ToUpperInvariant()}")
                .AppendLine($"源文件：{sourcePath}");
            for (var i = 0; i < errors.Count; i++)
                block.AppendLine($"第 {i + 1} 次：{errors[i]}");
            foreach (var notice in notices ?? []) block.AppendLine("记录说明：" + notice);
            block.AppendLine("最终状态：三次尝试均失败，已继续处理下一个文件。");
            lock (_gate) File.AppendAllText(target, block.ToString(), Encoding.UTF8);
        }
        catch (Exception ex)
        {
            Error($"无法写入桌面失败记录：{ex.Message}");
        }
    }
}
