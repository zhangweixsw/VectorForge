using Microsoft.Win32;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace VectorForgeSetup;

internal static class Program
{
    internal const string Version = "18.0.1";
    internal static readonly string InstallDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "VectorForge");
    internal static readonly string AppPath = Path.Combine(InstallDirectory, "VectorForge.exe");
    internal static readonly string UninstallerPath = Path.Combine(InstallDirectory, "Uninstall.exe");
    private const string StartupValueName = "VectorForgeIllustratorMonitor";
    private const string UninstallKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\VectorForge";

    [STAThread]
    private static int Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        if (args.Contains("--verify", StringComparer.OrdinalIgnoreCase)) return VerifyPayload();
        if (args.Contains("--uninstall", StringComparer.OrdinalIgnoreCase)) return Uninstall();
        Application.Run(new InstallerForm());
        return 0;
    }

    private static int VerifyPayload()
    {
        using var payload = typeof(Program).Assembly.GetManifestResourceStream("VectorForge.Setup.Payload.exe");
        if (payload is null || payload.Length < 1_000_000) return 10;
        return payload.ReadByte() == 'M' && payload.ReadByte() == 'Z' ? 0 : 11;
    }

    internal static void Install(bool desktopShortcut)
    {
        EnsureAppStopped("安装或升级");
        Directory.CreateDirectory(InstallDirectory);
        var temp = Path.Combine(InstallDirectory, "VectorForge.installing-" + Guid.NewGuid().ToString("N") + ".exe");
        try
        {
            using var payload = typeof(Program).Assembly.GetManifestResourceStream("VectorForge.Setup.Payload.exe")
                ?? throw new InvalidOperationException("安装包缺少 VectorForge 程序文件。");
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                payload.CopyTo(output);
            File.Move(temp, AppPath, true);
            File.Copy(Environment.ProcessPath!, UninstallerPath, true);

            var programs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "VectorForge");
            Directory.CreateDirectory(programs);
            CreateShortcut(Path.Combine(programs, "VectorForge.lnk"), AppPath);
            CreateShortcut(Path.Combine(programs, "卸载 VectorForge.lnk"), UninstallerPath, "--uninstall");
            var desktop = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "VectorForge.lnk");
            if (desktopShortcut) CreateShortcut(desktop, AppPath);
            else if (File.Exists(desktop)) File.Delete(desktop);

            using (var startup = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
                if (startup.GetValue(StartupValueName) is not null)
                    startup.SetValue(StartupValueName, $"\"{AppPath}\"");

            using var uninstall = Registry.CurrentUser.CreateSubKey(UninstallKeyPath);
            uninstall.SetValue("DisplayName", "VectorForge Illustrator Monitor");
            uninstall.SetValue("DisplayVersion", Version);
            uninstall.SetValue("Publisher", "VectorForge");
            uninstall.SetValue("InstallLocation", InstallDirectory);
            uninstall.SetValue("DisplayIcon", AppPath);
            uninstall.SetValue("UninstallString", $"\"{UninstallerPath}\" --uninstall");
            uninstall.SetValue("NoModify", 1, RegistryValueKind.DWord);
            uninstall.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            uninstall.SetValue("EstimatedSize", (int)((new FileInfo(AppPath).Length + new FileInfo(UninstallerPath).Length) / 1024), RegistryValueKind.DWord);
        }
        finally { try { if (File.Exists(temp)) File.Delete(temp); } catch { } }
    }

    private static int Uninstall()
    {
        if (MessageBox.Show("确定卸载 VectorForge？\n\n监控设置和导出清单会保留，重新安装后可继续使用。",
            "卸载 VectorForge", MessageBoxButtons.YesNo, MessageBoxIcon.Question,
            MessageBoxDefaultButton.Button2) != DialogResult.Yes) return 1;
        try
        {
            EnsureAppStopped("卸载");
            var programs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "VectorForge");
            DeleteIfExists(Path.Combine(programs, "VectorForge.lnk"));
            DeleteIfExists(Path.Combine(programs, "卸载 VectorForge.lnk"));
            try { if (Directory.Exists(programs) && !Directory.EnumerateFileSystemEntries(programs).Any()) Directory.Delete(programs); } catch { }
            DeleteIfExists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "VectorForge.lnk"));
            DeleteIfExists(AppPath);
            Registry.CurrentUser.DeleteSubKeyTree(UninstallKeyPath, false);
            using (var startup = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
            {
                var value = startup.GetValue(StartupValueName)?.ToString();
                if (value?.Contains(AppPath, StringComparison.OrdinalIgnoreCase) == true)
                    startup.DeleteValue(StartupValueName, false);
            }
            MoveFileEx(Environment.ProcessPath!, null, MoveFileFlags.DelayUntilReboot);
            MoveFileEx(InstallDirectory, null, MoveFileFlags.DelayUntilReboot);
            MessageBox.Show("VectorForge 已卸载。设置和导出记录已保留。", "卸载完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }
        catch (Exception ex)
        {
            MessageBox.Show("卸载未完成：" + ex.Message, "VectorForge", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 2;
        }
    }

    private static void EnsureAppStopped(string action)
    {
        if (Process.GetProcessesByName("VectorForge").Any(process => !process.HasExited))
            throw new InvalidOperationException($"请先从系统托盘完全退出 VectorForge，再进行{action}。");
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }

    private static void CreateShortcut(string shortcutPath, string targetPath, string arguments = "")
    {
        var link = (IShellLinkW)new ShellLink();
        link.SetPath(targetPath);
        link.SetArguments(arguments);
        link.SetWorkingDirectory(Path.GetDirectoryName(targetPath)!);
        link.SetDescription("VectorForge Illustrator 自动监控与导出");
        link.SetIconLocation(targetPath, 0);
        ((System.Runtime.InteropServices.ComTypes.IPersistFile)link).Save(shortcutPath, false);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool MoveFileEx(string existingFileName, string? newFileName, MoveFileFlags flags);
    [Flags] private enum MoveFileFlags : uint { DelayUntilReboot = 0x4 }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink { }
    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder file, int maxPath, IntPtr data, uint flags);
        void GetIDList(out IntPtr pidl); void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder name, int maxName);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder directory, int maxPath);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder arguments, int maxPath);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string arguments);
        void GetHotkey(out short hotkey); void SetHotkey(short hotkey);
        void GetShowCmd(out int showCommand); void SetShowCmd(int showCommand);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder iconPath, int iconPathLength, out int iconIndex);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int iconIndex);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        void Resolve(IntPtr hwnd, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string path);
    }
}

internal sealed class InstallerForm : Form
{
    private readonly CheckBox _desktop = new() { Text = "创建桌面快捷方式", Checked = true, AutoSize = true };
    private readonly CheckBox _launch = new() { Text = "安装完成后启动 VectorForge", Checked = true, AutoSize = true };
    private readonly Button _install = new() { Text = "安装", Width = 120, Height = 38 };
    private readonly ProgressBar _progress = new() { Style = ProgressBarStyle.Marquee, Visible = false };

    public InstallerForm()
    {
        Text = "安装 VectorForge v" + Program.Version;
        ClientSize = new Size(600, 360);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!);
        BackColor = Color.FromArgb(244, 246, 250);

        var header = new Panel { Dock = DockStyle.Top, Height = 92, BackColor = Color.FromArgb(20, 43, 80) };
        header.Controls.Add(new Label { Text = "VectorForge", ForeColor = Color.White, Font = new Font("Segoe UI", 24, FontStyle.Bold), AutoSize = true, Location = new Point(28, 18) });
        header.Controls.Add(new Label { Text = "Illustrator 自动监控与导出", ForeColor = Color.FromArgb(191, 219, 254), Font = new Font("Segoe UI", 10), AutoSize = true, Location = new Point(31, 58) });
        Controls.Add(header);
        Controls.Add(new Label { Text = "安装位置", AutoSize = true, Location = new Point(32, 118), Font = new Font("Segoe UI", 10, FontStyle.Bold) });
        Controls.Add(new TextBox { Text = Program.InstallDirectory, ReadOnly = true, Location = new Point(32, 145), Width = 530 });
        _desktop.Location = new Point(35, 195); _launch.Location = new Point(35, 226);
        Controls.Add(_desktop); Controls.Add(_launch);
        _progress.Location = new Point(32, 272); _progress.Size = new Size(390, 22); Controls.Add(_progress);
        _install.Location = new Point(442, 264); Controls.Add(_install);
        Controls.Add(new Label { Text = "按当前 Windows 用户安装；升级不会删除设置和导出清单。", AutoSize = true, ForeColor = Color.DimGray, Location = new Point(32, 320) });
        _install.Click += Install_Click;
    }

    private async void Install_Click(object? sender, EventArgs e)
    {
        _install.Enabled = false; _progress.Visible = true;
        try
        {
            var createDesktopShortcut = _desktop.Checked;
            await Task.Run(() => Program.Install(createDesktopShortcut));
            MessageBox.Show(this, "VectorForge 安装完成。", "安装完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
            if (_launch.Checked) Process.Start(new ProcessStartInfo(Program.AppPath) { UseShellExecute = true });
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "安装失败：" + ex.Message, "VectorForge", MessageBoxButtons.OK, MessageBoxIcon.Error);
            _install.Enabled = true; _progress.Visible = false;
        }
    }
}
