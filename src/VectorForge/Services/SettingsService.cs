using Microsoft.Win32;

namespace VectorForge.Services;

public sealed class SettingsService
{
    private const string StartupValueName = "VectorForgeIllustratorMonitor";
    private static string BackupFile => AppPaths.SettingsFile + ".last-good.json";
    public AppSettings Settings { get; }

    public SettingsService()
    {
        AppPaths.EnsureCreated();
        if (File.Exists(AppPaths.SettingsFile) && !File.Exists(AppPaths.SettingsFile + ".pre-v18.bak"))
            File.Copy(AppPaths.SettingsFile, AppPaths.SettingsFile + ".pre-v18.bak");
        if (File.Exists(AppPaths.SettingsFile) && !File.Exists(AppPaths.SettingsFile + ".pre-v17.bak"))
            File.Copy(AppPaths.SettingsFile, AppPaths.SettingsFile + ".pre-v17.bak");
        if (File.Exists(AppPaths.SettingsFile) && !File.Exists(AppPaths.SettingsFile + ".pre-v7.bak"))
            File.Copy(AppPaths.SettingsFile, AppPaths.SettingsFile + ".pre-v7.bak");
        if (!AtomicJsonStore.TryLoad<AppSettings>(AppPaths.SettingsFile, out var loaded))
        {
            if (File.Exists(AppPaths.SettingsFile))
            {
                var corrupt = AppPaths.SettingsFile + ".corrupt-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
                try { File.Copy(AppPaths.SettingsFile, corrupt, true); } catch { }
            }
            AtomicJsonStore.TryLoad<AppSettings>(BackupFile, out loaded);
        }
        Settings = loaded ?? new AppSettings();
        if (Settings.SettingsRevision < 18)
        {
            Settings.SessionMode = IllustratorSessionMode.Shared;
            Settings.SettingsRevision = 18;
        }
        Settings.Normalize();
        SaveFiles(Settings);
    }

    public void Save()
    {
        Settings.Normalize();
        SaveFiles(Settings);
        ApplyStartupSetting(Settings.StartWithWindows);
    }

    public void Commit(AppSettings updated)
    {
        updated.SettingsRevision = 18;
        updated.Normalize();
        SaveFiles(updated);
        ApplyStartupSetting(updated.StartWithWindows);
        lock (Settings)
        {
            Settings.PdfJob = updated.PdfJob;
            Settings.JpgJob = updated.JpgJob;
            Settings.StartWithWindows = updated.StartWithWindows;
            Settings.SessionMode = updated.SessionMode;
            Settings.SettingsRevision = 18;
        }
    }

    private static void SaveFiles(AppSettings settings)
    {
        AtomicJsonStore.Save(AppPaths.SettingsFile, settings);
        AtomicJsonStore.Save(BackupFile, settings);
    }

    private static void ApplyStartupSetting(bool enabled)
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("VECTORFORGE_DATA_DIR"))) return;
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        if (enabled)
        {
            var exe = Environment.ProcessPath ?? throw new InvalidOperationException("无法获得 EXE 路径。");
            key.SetValue(StartupValueName, $"\"{exe}\"");
        }
        else
        {
            key.DeleteValue(StartupValueName, false);
        }
    }
}
