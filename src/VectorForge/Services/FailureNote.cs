using System.Security.Cryptography;
using System.Text;

namespace VectorForge.Services;

public static class FailureNote
{
    public static string StageLabel(string stage) => stage switch
    {
        "Opening" => "打开 AI", "Exporting" => "导出 PDF/JPG", "Closing" => "关闭工作副本",
        "Preparing" => "准备只读工作副本", "Validating" or "Completed" => "验证导出结果", _ => stage
    };
    private const string Header = "VectorForge v17 导出失败说明";
    private static string Marker(string source, ExportKind kind) => "任务标识：" +
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(source).ToUpperInvariant() + "|" + kind)));

    public static string Write(string source, ExportKind kind, FailureState failure, DateTimeOffset now)
    {
        if (!FileSafety.IsPlainPath(source) || !FileSafety.IsVisibleOutput(source)) throw new IOException("源目录隐藏或不可访问，无法写入失败说明");
        var directory = Path.GetDirectoryName(source)!;
        var stem = Path.GetFileNameWithoutExtension(source);
        // Keep long Illustrator filenames usable within the usual Windows component limit.
        if (stem.Length > 120) stem = stem[..100] + "_" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(stem)))[..12];
        var path = Path.Combine(directory, $"{stem}_无法导出稍后重试_{kind.ToString().ToUpperInvariant()}.txt");
        var marker = Marker(source, kind);
        if (File.Exists(path) && !IsOwned(path, marker))
            path = Path.Combine(directory, $"{stem}_无法导出稍后重试_{kind.ToString().ToUpperInvariant()}_VectorForge.txt");
        if (!FileSafety.IsPlainPath(path) || !FileSafety.IsVisibleOutput(path) ||
            File.Exists(path) && !IsOwned(path, marker)) throw new IOException("失败说明文件已存在且不属于本软件，保留原文件");
        var text = new StringBuilder().AppendLine(Header).AppendLine(marker)
            .AppendLine($"时间：{now.ToLocalTime():yyyy-MM-dd HH:mm:ss zzz}")
            .AppendLine("AI 文件：" + source).AppendLine("导出类型：" + kind.ToString().ToUpperInvariant())
            .AppendLine("失败阶段：" + StageLabel(failure.Stage)).AppendLine("尝试次数：" + failure.Attempts);
        for (var i = 0; i < failure.Errors.Count; i++) text.AppendLine($"第 {i + 1} 次原因：{failure.Errors[i]}");
        text.AppendLine("当前状态：已暂停自动重试，其他文件继续处理。")
            .AppendLine("处理方法：检查 Illustrator、缺失链接、字体或文件是否损坏后，在 VectorForge 中点击‘重试失败项 / 恢复’。")
            .AppendLine("源 AI 更新后也会重新检查；原始 AI 未被本软件修改或删除。");
        // CreateNew avoids racing an unrelated newly-created user file. Owned reports use replace.
        var temp = Path.Combine(directory, "vectorforge_temp_report_" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(true))) writer.Write(text.ToString());
            if (File.Exists(path))
            {
                if (!IsOwned(path, marker)) throw new IOException("失败说明归属已变化");
                File.Replace(temp, path, null);
            }
            else File.Move(temp, path);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
        return path;
    }

    public static void Remove(string source, ExportKind kind, string path)
    {
        if (string.IsNullOrEmpty(path) || !string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)),
            Path.GetDirectoryName(Path.GetFullPath(source)), StringComparison.OrdinalIgnoreCase) ||
            !path.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) || !FileSafety.IsPlainPath(path) ||
            !FileSafety.IsVisibleOutput(path) || !IsOwned(path, Marker(source, kind))) return;
        File.Delete(path);
    }

    private static bool IsOwned(string path, string marker)
    {
        if (!FileSafety.IsPlainPath(path)) return false;
        using var reader = new StreamReader(path);
        return reader.ReadLine() == Header && reader.ReadLine() == marker;
    }
}
