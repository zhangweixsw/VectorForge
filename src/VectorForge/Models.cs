using System.Text.Json.Serialization;

namespace VectorForge;

public enum ExportKind
{
    Pdf,
    Jpg
}

public enum IllustratorSessionMode { Unattended, Shared }
public enum JpgSizingMode { Dpi, PixelWidth }

public sealed class MonitorJobSettings
{
    public ExportKind Kind { get; set; }
    public bool Enabled { get; set; }
    public List<string> RootPaths { get; set; } = [];
    public List<string> SkipFileNameKeywords { get; set; } = [];

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string RootPath { get; set; } = "";
    public int ModifiedWithinHours { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int ModifiedWithinDays { get; set; } = 1;
    public int ScanIntervalSeconds { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int ScanIntervalMinutes { get; set; }
    public int TimeoutMinutes { get; set; } = 5;
    public int JpgDpi { get; set; } = 150;
    public JpgSizingMode JpgSizing { get; set; }
    public int JpgPixelWidth { get; set; } = 5000;
    public int JpgQuality { get; set; } = 80;
    public string PdfPreset { get; set; } = "";

    [JsonIgnore]
    public DateTimeOffset LastScanUtc { get; set; } = DateTimeOffset.MinValue;

    public void Normalize()
    {
        RootPaths ??= [];
        if (RootPaths.Count == 0 && !string.IsNullOrWhiteSpace(RootPath)) RootPaths.Add(RootPath);
        RootPaths = RootPaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        RootPath = "";
        SkipFileNameKeywords ??= [];
        SkipFileNameKeywords = SkipFileNameKeywords
            .Where(keyword => !string.IsNullOrWhiteSpace(keyword))
            .Select(keyword => keyword.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (ModifiedWithinHours <= 0)
            ModifiedWithinHours = ModifiedWithinDays is 1 or 3 or 7 or 30
                ? ModifiedWithinDays * 24
                : 24;
        ModifiedWithinHours = Math.Clamp(ModifiedWithinHours, 1, 87600);
        ModifiedWithinDays = 0;
        if (ScanIntervalSeconds <= 0)
            ScanIntervalSeconds = ScanIntervalMinutes > 0 ? ScanIntervalMinutes * 60 : 120;
        ScanIntervalSeconds = Math.Clamp(ScanIntervalSeconds, 1, 86400);
        ScanIntervalMinutes = 0;
        TimeoutMinutes = Math.Clamp(TimeoutMinutes, 1, 60);
        JpgDpi = Math.Clamp(JpgDpi, 72, 500);
        if (!Enum.IsDefined(JpgSizing)) JpgSizing = JpgSizingMode.Dpi;
        JpgPixelWidth = Math.Clamp(JpgPixelWidth, 1, 30000);
        JpgQuality = Math.Clamp(JpgQuality, 0, 100);
        PdfPreset ??= "";
    }
}

public sealed class AppSettings
{
    public IllustratorSessionMode SessionMode { get; set; } = IllustratorSessionMode.Shared;
    public int SettingsRevision { get; set; }
    public MonitorJobSettings PdfJob { get; set; } = new() { Kind = ExportKind.Pdf };
    public MonitorJobSettings JpgJob { get; set; } = new() { Kind = ExportKind.Jpg };
    public bool StartWithWindows { get; set; }

    public void Normalize()
    {
        if (!Enum.IsDefined(SessionMode)) SessionMode = IllustratorSessionMode.Shared;
        PdfJob.Kind = ExportKind.Pdf;
        JpgJob.Kind = ExportKind.Jpg;
        PdfJob.Normalize();
        JpgJob.Normalize();
    }
}

public sealed class ExportManifestEntry
{
    public JpgSizingMode JpgSizing { get; set; }
    public int JpgPixelWidth { get; set; }
    public int JpgDpi { get; set; }
    public int JpgQuality { get; set; }
    public string PdfPreset { get; set; } = "";
    public required string SourcePath { get; set; }
    public required ExportKind Kind { get; set; }
    public long SourceLength { get; set; }
    public DateTime SourceModifiedUtc { get; set; }
    public string SourceSha256 { get; set; } = "";
    public int ArtboardCount { get; set; }
    public List<string> OutputFiles { get; set; } = [];
    public DateTimeOffset LastSuccessUtc { get; set; }
}

public sealed class ExportManifest
{
    public Dictionary<string, ExportManifestEntry> Entries { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}

public sealed class ExportRequest
{
    public JpgSizingMode JpgSizing { get; set; }
    public int JpgPixelWidth { get; set; } = 5000;
    public IllustratorSessionMode SessionMode { get; set; }
    public Guid SessionId { get; set; }
    public int OwnedProcessId { get; set; }
    public string OwnedExecutable { get; set; } = "";
    public string ProgressPath { get; set; } = "";
    public bool Shutdown { get; set; }
    public bool ReadPdfPresets { get; set; }
    public Guid JobId { get; set; } = Guid.NewGuid();
    public required ExportKind Kind { get; set; }
    public required string SourcePath { get; set; }
    public required string WorkingPrefix { get; set; }
    public required string OwnershipFile { get; set; }
    public int JpgDpi { get; set; } = 150;
    public int JpgQuality { get; set; } = 80;
    public string PdfPreset { get; set; } = "";
    public int Attempt { get; set; }
}

public sealed class ExportResult
{
    public bool IsHandshake { get; set; }
    public bool SafeToCleanup { get; set; } = true;
    public string Stage { get; set; } = "Opening";
    public bool SharedInstance { get; set; }
    public Guid JobId { get; set; }
    public bool Success { get; set; }
    public int ArtboardCount { get; set; }
    public List<string> TemporaryOutputs { get; set; } = [];
    public List<string> PdfPresets { get; set; } = [];
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }

    public static ExportResult Failed(Guid id, string code, string message) =>
        new() { JobId = id, Success = false, ErrorCode = code, ErrorMessage = message };
}

public sealed record FileSnapshot(long Length, DateTime ModifiedUtc);

public sealed record JobStatus(
    ExportKind Kind,
    string State,
    int Pending,
    string LastResult);
