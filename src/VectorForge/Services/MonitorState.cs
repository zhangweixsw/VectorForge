namespace VectorForge.Services;

public sealed class FailureState
{
    public string SourcePath { get; set; } = "";
    public ExportKind Kind { get; set; }
    // Old v7-v16 records were written only after all three attempts failed.
    public int Attempts { get; set; } = 3;
    public DateTimeOffset NextAttemptUtc { get; set; }
    public string Stage { get; set; } = "";
    public string NotePath { get; set; } = "";
    public long Length { get; set; }
    public DateTime ModifiedUtc { get; set; }
    public List<string> Errors { get; set; } = [];
}
public sealed class MonitorState
{
    public Dictionary<string, FailureState> OpenFailures { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<AbandonedWork> Abandoned { get; set; } = [];
    public Dictionary<string, string> FailureNotes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, OutputStamp> OutputChecks { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, FailureState> Failures { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, DateTimeOffset> MissingSince { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, List<string>> Cleanup { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
public sealed class AbandonedWork
{
    public List<string> Paths { get; set; } = [];
    public List<IllustratorIdentity> Blockers { get; set; } = [];
}
public sealed record ProcessingRecord(DateTimeOffset Time, ExportKind Kind, string Path, string Reason);
public sealed class ScanStatistics
{
    public int Scanned { get; set; }
    public int Skipped { get; set; }
    public int Succeeded { get; set; }
    public int Failed { get; set; }
    public int Deleted { get; set; }
    public override string ToString() => $"扫描 {Scanned} · 跳过 {Skipped} · 成功 {Succeeded} · 失败 {Failed} · 删除 {Deleted}";
}
