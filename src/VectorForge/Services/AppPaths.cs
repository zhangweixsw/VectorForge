namespace VectorForge.Services;

public static class AppPaths
{
    public static readonly string DataDirectory = ResolveDataDirectory();

    public static readonly string SettingsFile = Path.Combine(DataDirectory, "settings.json");
    public static readonly string ManifestFile = Path.Combine(DataDirectory, "manifest.json");
    public static readonly string LogDirectory = Path.Combine(DataDirectory, "logs");
    public static readonly string WorkerDirectory = Path.Combine(DataDirectory, "worker");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(LogDirectory);
        Directory.CreateDirectory(WorkerDirectory);
    }

    private static string ResolveDataDirectory()
    {
        var overridden = Environment.GetEnvironmentVariable("VECTORFORGE_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(overridden)) return Path.GetFullPath(overridden);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolderOption.DoNotVerify);
        if (string.IsNullOrWhiteSpace(local)) local = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        if (string.IsNullOrWhiteSpace(local))
            local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "Local");
        if (string.IsNullOrWhiteSpace(local) || !Path.IsPathRooted(local))
            throw new InvalidOperationException("无法确定当前 Windows 用户的本地设置目录。");
        return Path.Combine(local, "VectorForge");
    }
}
