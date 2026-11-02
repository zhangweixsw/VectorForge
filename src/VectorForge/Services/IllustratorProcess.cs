using System.Diagnostics;

namespace VectorForge.Services;

public static class IllustratorProcess
{
    public static int[] GetProcessIds()
    {
        var processes = Process.GetProcessesByName("Illustrator");
        try { return processes.Select(p => p.Id).ToArray(); }
        finally { foreach (var process in processes) process.Dispose(); }
    }
    public static bool IsRunning() => GetProcessIds().Length > 0;
}
