using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace VectorForge.Services;

public sealed class IllustratorIdentity
{
    public int ProcessId { get; set; }
    public DateTime StartedUtc { get; set; }
    public string Executable { get; set; } = "";
    public int WindowsSession { get; set; }

    public static IllustratorIdentity Read(Process process) => new()
    {
        ProcessId = process.Id, StartedUtc = process.StartTime.ToUniversalTime(),
        Executable = ExecutablePath(process.Id), WindowsSession = process.SessionId
    };

    public bool MatchesLiveProcess()
    {
        try
        {
            using var process = Process.GetProcessById(ProcessId);
            return !process.HasExited && process.StartTime.ToUniversalTime() == StartedUtc &&
                process.SessionId == WindowsSession && string.Equals(ExecutablePath(process.Id),
                    Executable, StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch { return true; } // Unknown/access denied must protect the process and its open files.
    }

    // MainModule can briefly be empty during startup. Query the process image directly,
    // with read-only permissions, rather than guessing ownership from a process name.
    private static string ExecutablePath(int pid)
    {
        var handle = OpenProcess(0x1000, false, pid);
        if (handle == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var path = new StringBuilder(32768); var size = path.Capacity;
            if (!QueryFullProcessImageName(handle, 0, path, ref size))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            return path.ToString();
        }
        finally { CloseHandle(handle); }
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);
    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}

// Only an identity captured from Process.Start in this live parent can be terminated.
// On-disk ownership files are diagnostics, never authority after a restart.
public sealed class IllustratorSessionManager(AppLogger logger)
{
    internal Func<string> ResolveExecutable { get; set; } = RegisteredExecutable;
    internal Func<List<IllustratorIdentity>> EnumerateProcesses { get; set; } = CurrentIdentities;
    internal Func<string, Process?> Launch { get; set; } = executable => Process.Start(new ProcessStartInfo(executable)
        { UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden });
    private IllustratorIdentity? _owned;
    private bool _exclusive;
    public Guid SessionId { get; private set; } = Guid.NewGuid();
    public string OwnershipPath => Path.Combine(AppPaths.WorkerDirectory, SessionId.ToString("N") + ".session.json");
    public bool CanTerminate => _exclusive && _owned is not null;
    public IllustratorIdentity? Owned => _owned;
    public bool RecoveryBlocked { get; private set; }

    public static List<IllustratorIdentity> CurrentIdentities()
    {
        var list = new List<IllustratorIdentity>();
        foreach (var process in Process.GetProcessesByName("Illustrator"))
            using (process)
                try
                {
                    if (!process.HasExited && process.SessionId == Process.GetCurrentProcess().SessionId) list.Add(IllustratorIdentity.Read(process));
                }
                catch (InvalidOperationException) { }
                catch (ArgumentException) { }
                catch
                {
                    try { if (!process.HasExited) list.Add(new IllustratorIdentity { ProcessId = process.Id }); }
                    catch { try { list.Add(new IllustratorIdentity { ProcessId = process.Id }); } catch { } }
                }
        return list;
    }

    public void Prepare(ExportRequest request)
    {
        if (RecoveryBlocked) throw new SessionUnavailableException("RECOVERY_BLOCKED", "专用 Illustrator 未能退出，请处理后点击重试失败项 / 恢复");
        if (_owned is not null && !_owned.MatchesLiveProcess()) { _owned = null; _exclusive = false; }
        if (_owned is not null && (request.SessionMode == IllustratorSessionMode.Unattended && !_exclusive && !request.ReadPdfPresets ||
            request.SessionMode == IllustratorSessionMode.Shared && _exclusive))
            Relinquish();
        var existing = EnumerateProcesses();
        if (_owned is null && existing.Count > 0)
        {
            if (request.SessionMode == IllustratorSessionMode.Unattended && !request.ReadPdfPresets)
                throw new SessionUnavailableException("WAITING_USER", "等待用户的 Illustrator 关闭；目录扫描继续");
        }
        else if (_owned is null)
        {
            var executable = ResolveExecutable();
            using var process = Launch(executable) ?? throw new IOException("无法启动 Illustrator");
            _owned = IllustratorIdentity.Read(process);
            if (!string.Equals(_owned.Executable, executable, StringComparison.OrdinalIgnoreCase))
            { var actual = _owned.Executable; Relinquish(); throw new IOException($"Illustrator 启动进程路径不匹配，禁止接管：预期 {executable}，实际 {actual}"); }
            _exclusive = request.SessionMode == IllustratorSessionMode.Unattended;
            SessionId = Guid.NewGuid();
            AtomicJsonStore.Save(OwnershipPath, _owned);
            logger.Info($"启动 Illustrator 专用会话 {SessionId} PID={_owned.ProcessId} 独占={_exclusive}");
        }
        request.SessionId = SessionId;
        request.OwnedProcessId = _owned?.ProcessId ?? 0;
        request.OwnedExecutable = _owned?.Executable ?? "";
        request.OwnershipFile = OwnershipPath;
    }

    public void Relinquish()
    {
        if (_owned is not null) logger.Info("不再接管 Illustrator PID=" + _owned.ProcessId);
        _owned = null; _exclusive = false; SessionId = Guid.NewGuid();
    }

    public void AllowRecoveryCheck() => RecoveryBlocked = false;

    internal static bool MatchesApplicationDirectory(string directory, string executable)
    {
        try
        {
            var expected = Path.GetFullPath(executable);
            directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
            return string.Equals(Path.GetDirectoryName(expected), directory, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(Path.Combine(directory, "Support Files", "Contents", "Windows", "Illustrator.exe"), expected, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    public async Task<bool> RecoverAsync()
    {
        if (!CanTerminate) return EnumerateProcesses().Count == 0;
        var identity = _owned!;
        if (!identity.MatchesLiveProcess()) { Relinquish(); return true; }
        try
        {
            using var process = Process.GetProcessById(identity.ProcessId);
            // Repeat the positive identity check immediately before the destructive operation.
            var actual = IllustratorIdentity.Read(process);
            if (actual.StartedUtc != identity.StartedUtc || actual.WindowsSession != identity.WindowsSession ||
                !string.Equals(actual.Executable, identity.Executable, StringComparison.OrdinalIgnoreCase))
            { Relinquish(); return false; }
            logger.Info("超时/异常恢复：结束本软件专用 Illustrator PID=" + identity.ProcessId);
            process.Kill(false);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            Relinquish(); return true;
        }
        catch (Exception ex)
        {
            RecoveryBlocked = true;
            logger.Error("专用 Illustrator 回收失败：" + ex.Message);
            return false;
        }
    }

    private static string RegisteredExecutable()
    {
        using var clsidKey = Registry.ClassesRoot.OpenSubKey(@"Illustrator.Application\CLSID");
        var clsid = clsidKey?.GetValue(null)?.ToString();
        using var server = Registry.ClassesRoot.OpenSubKey(@"CLSID\" + clsid + @"\LocalServer32");
        var command = server?.GetValue(null)?.ToString()?.Trim() ?? "";
        var end = command.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        var executable = command.StartsWith('"') ? command.Split('"')[1] : end < 0 ? "" : command[..(end + 4)];
        if (!File.Exists(executable) || !Path.GetFileName(executable).Equals("Illustrator.exe", StringComparison.OrdinalIgnoreCase))
            throw new IOException("未找到已注册的 Illustrator，请确认已安装并能正常启动");
        return executable;
    }
}

public sealed class SessionUnavailableException(string code, string message) : IOException(message)
{
    public string Code { get; } = code;
}
