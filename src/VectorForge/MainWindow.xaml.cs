using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using VectorForge.Services;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;
using System.Collections.ObjectModel;
using System.Windows.Threading;

namespace VectorForge;

public partial class MainWindow : Window
{
    private void Website_RequestNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); e.Handled = true;
    }

    private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        UpdateButton.IsEnabled = false;
        try
        {
            var update = await SoftwareUpdate.CheckAsync();
            if (update is null) { System.Windows.MessageBox.Show(this, "当前已是最新版本。", "VectorForge"); return; }
            if (System.Windows.MessageBox.Show(this, $"发现新版本 v{update.Version}\nWindows x64 · 需要 Illustrator\n下载 {update.Bytes / 1048576.0:F1} MB\n安装前将停止监控并退出当前程序。", "VectorForge", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
            var installer = await SoftwareUpdate.DownloadAsync(update);
            await _coordinator.PauseAsync();
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(installer) { UseShellExecute = true });
            await ExitApplicationAsync();
        }
        catch (Exception ex) { System.Windows.MessageBox.Show(this, "更新未完成：" + ex.Message + "\n当前程序保留，可重试或访问 mootop.top 手动下载。", "VectorForge"); }
        finally { UpdateButton.IsEnabled = true; }
    }
    private readonly SettingsService _settingsService = new();
    private readonly AppLogger _logger = new();
    private readonly MonitorCoordinator _coordinator;
    private readonly Forms.NotifyIcon _trayIcon;
    private bool _reallyExit;
    private readonly ObservableCollection<ProcessingRecord> _records = [];
    private readonly DispatcherTimer _settingsAutosaveTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    public MainWindow()
    {
        InitializeComponent();
        _coordinator = new MonitorCoordinator(_settingsService.Settings, _logger);
        _coordinator.StatusChanged += Coordinator_StatusChanged;
        _coordinator.RecordAdded += (_, record) => Dispatcher.BeginInvoke(() =>
        {
            _records.Insert(0, record);
            while (_records.Count > 1000) _records.RemoveAt(_records.Count - 1);
        });
        RecordsGrid.ItemsSource = _records;
        LoadSettingsToUi();
        _settingsAutosaveTimer.Tick += (_, _) =>
        {
            _settingsAutosaveTimer.Stop();
            SaveUiToSettings(false);
        };
        AddHandler(System.Windows.Controls.TextBox.TextChangedEvent,
            new TextChangedEventHandler((_, _) => ScheduleSettingsSave()), true);
        AddHandler(System.Windows.Controls.Primitives.ToggleButton.CheckedEvent,
            new RoutedEventHandler((_, _) => ScheduleSettingsSave()), true);
        AddHandler(System.Windows.Controls.Primitives.ToggleButton.UncheckedEvent,
            new RoutedEventHandler((_, _) => ScheduleSettingsSave()), true);
        AddHandler(System.Windows.Controls.Primitives.Selector.SelectionChangedEvent,
            new SelectionChangedEventHandler((_, _) => { UpdateJpgSizingUi(); ScheduleSettingsSave(); }), true);

        _trayIcon = new Forms.NotifyIcon
        {
            Icon = LoadTrayIcon(),
            Text = "VectorForge Illustrator Monitor",
            Visible = true,
            ContextMenuStrip = BuildTrayMenu()
        };
        _trayIcon.DoubleClick += (_, _) => ShowFromTray();
        Closing += MainWindow_Closing;
        StateChanged += (_, _) => { if (WindowState == WindowState.Minimized) Hide(); };

        if (_settingsService.Settings.PdfJob.Enabled || _settingsService.Settings.JpgJob.Enabled)
            _coordinator.Start();
        UpdateButtons();
    }

    private Forms.ContextMenuStrip BuildTrayMenu()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("显示主窗口", null, (_, _) => ShowFromTray());
        menu.Items.Add("开始监控", null, (_, _) => Dispatcher.Invoke(StartMonitoring));
        menu.Items.Add("暂停监控", null, async (_, _) => await Dispatcher.InvokeAsync(PauseMonitoringAsync));
        menu.Items.Add("立即扫描全部", null, (_, _) => Dispatcher.Invoke(() =>
        {
            if (!SaveUiToSettings()) return;
            _coordinator.Start();
            _coordinator.RunNow(ExportKind.Pdf);
            _coordinator.RunNow(ExportKind.Jpg);
            UpdateButtons();
        }));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出", null, async (_, _) => await Dispatcher.InvokeAsync(ExitApplicationAsync));
        return menu;
    }

    private void LoadSettingsToUi()
    {
        var settings = _settingsService.Settings;
        SessionModeComboBox.SelectedIndex = settings.SessionMode == IllustratorSessionMode.Shared ? 1 : 0;
        PdfEnabledCheckBox.IsChecked = settings.PdfJob.Enabled;
        LoadPaths(PdfPathsListBox, settings.PdfJob.RootPaths);
        PdfHoursTextBox.Text = settings.PdfJob.ModifiedWithinHours.ToString();
        PdfIntervalTextBox.Text = settings.PdfJob.ScanIntervalSeconds.ToString();
        PdfTimeoutTextBox.Text = settings.PdfJob.TimeoutMinutes.ToString();
        PdfSkipKeywordsTextBox.Text = string.Join("；", settings.PdfJob.SkipFileNameKeywords);
        SelectPdfPreset(settings.PdfJob.PdfPreset);

        JpgEnabledCheckBox.IsChecked = settings.JpgJob.Enabled;
        LoadPaths(JpgPathsListBox, settings.JpgJob.RootPaths);
        JpgHoursTextBox.Text = settings.JpgJob.ModifiedWithinHours.ToString();
        JpgIntervalTextBox.Text = settings.JpgJob.ScanIntervalSeconds.ToString();
        JpgTimeoutTextBox.Text = settings.JpgJob.TimeoutMinutes.ToString();
        JpgDpiTextBox.Text = settings.JpgJob.JpgDpi.ToString();
        JpgSizingComboBox.SelectedIndex = settings.JpgJob.JpgSizing == JpgSizingMode.PixelWidth ? 1 : 0;
        JpgPixelWidthTextBox.Text = settings.JpgJob.JpgPixelWidth.ToString();
        UpdateJpgSizingUi();
        JpgQualityTextBox.Text = settings.JpgJob.JpgQuality.ToString();
        JpgSkipKeywordsTextBox.Text = string.Join("；", settings.JpgJob.SkipFileNameKeywords);
        StartupCheckBox.IsChecked = settings.StartWithWindows;
    }

    private bool SaveUiToSettings(bool showErrors = true)
    {
        try
        {
            var settings = AtomicJsonStore.Deserialize<AppSettings>(AtomicJsonStore.Serialize(_settingsService.Settings))!;
            settings.SessionMode = SessionModeComboBox.SelectedIndex == 1 ? IllustratorSessionMode.Shared : IllustratorSessionMode.Unattended;
            settings.PdfJob.Enabled = PdfEnabledCheckBox.IsChecked == true;
            settings.PdfJob.RootPaths = ReadPaths(PdfPathsListBox);
            settings.PdfJob.ModifiedWithinHours = ParseRange(PdfHoursTextBox, "PDF 修改时间范围（小时）", 1, 87600);
            settings.PdfJob.ScanIntervalSeconds = ParseRange(PdfIntervalTextBox, "PDF 扫描间隔（秒）", 1, 86400);
            settings.PdfJob.TimeoutMinutes = ParseRange(PdfTimeoutTextBox, "PDF 超时", 1, 60);
            settings.PdfJob.SkipFileNameKeywords = ParseKeywords(PdfSkipKeywordsTextBox.Text);
            settings.PdfJob.PdfPreset = SelectedPdfPreset();

            settings.JpgJob.Enabled = JpgEnabledCheckBox.IsChecked == true;
            settings.JpgJob.RootPaths = ReadPaths(JpgPathsListBox);
            settings.JpgJob.ModifiedWithinHours = ParseRange(JpgHoursTextBox, "JPG 修改时间范围（小时）", 1, 87600);
            settings.JpgJob.ScanIntervalSeconds = ParseRange(JpgIntervalTextBox, "JPG 扫描间隔（秒）", 1, 86400);
            settings.JpgJob.TimeoutMinutes = ParseRange(JpgTimeoutTextBox, "JPG 超时", 1, 60);
            settings.JpgJob.JpgDpi = ParseRange(JpgDpiTextBox, "JPG 分辨率", 72, 500);
            settings.JpgJob.JpgSizing = JpgSizingComboBox.SelectedIndex == 1 ? JpgSizingMode.PixelWidth : JpgSizingMode.Dpi;
            settings.JpgJob.JpgPixelWidth = ParseRange(JpgPixelWidthTextBox, "JPG 宽度（像素）", 1, 30000);
            settings.JpgJob.JpgQuality = ParseRange(JpgQualityTextBox, "JPG 质量", 0, 100);
            settings.JpgJob.SkipFileNameKeywords = ParseKeywords(JpgSkipKeywordsTextBox.Text);
            settings.StartWithWindows = StartupCheckBox.IsChecked == true;

            ValidateEnabledPath(settings.PdfJob, "PDF");
            ValidateEnabledPath(settings.JpgJob, "JPG");
            _settingsService.Commit(settings);
            return true;
        }
        catch (Exception ex)
        {
            if (showErrors) System.Windows.MessageBox.Show(this, ex.Message, "设置有误", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
    }

    private static void ValidateEnabledPath(MonitorJobSettings job, string name)
    {
        if (!job.Enabled) return;
        if (job.RootPaths.Count == 0) throw new InvalidOperationException($"请为 {name} 任务至少添加一个监控文件夹。");
        foreach (var rootPath in job.RootPaths)
        {
            _ = Path.GetFullPath(rootPath);
            if (!Directory.Exists(rootPath)) continue;
            var probe = Path.Combine(rootPath, $".vectorforge-write-test-{Guid.NewGuid():N}.tmp");
            try
            {
                using var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1,
                    FileOptions.DeleteOnClose);
                stream.WriteByte(0);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"{name} 路径稍后重试：{rootPath}（{ex.Message}）");
            }
            finally
            {
                try { if (File.Exists(probe)) File.Delete(probe); } catch { }
            }
        }
    }

    private void StartButton_Click(object sender, RoutedEventArgs e) => StartMonitoring();

    private void StartMonitoring()
    {
        if (!SaveUiToSettings()) return;
        _coordinator.Start();
        GlobalStatusText.Text = "监控正在运行";
        UpdateButtons();
    }

    private async void PauseButton_Click(object sender, RoutedEventArgs e) => await PauseMonitoringAsync();

    private async Task PauseMonitoringAsync()
    {
        await _coordinator.PauseAsync();
        GlobalStatusText.Text = "监控已暂停";
        UpdateButtons();
    }

    private void RunPdfNow_Click(object sender, RoutedEventArgs e)
    {
        if (!SaveUiToSettings()) return;
        _coordinator.Start();
        _coordinator.RunNow(ExportKind.Pdf);
        UpdateButtons();
    }

    private void RunJpgNow_Click(object sender, RoutedEventArgs e)
    {
        if (!SaveUiToSettings()) return;
        _coordinator.Start();
        _coordinator.RunNow(ExportKind.Jpg);
        UpdateButtons();
    }

    private async void ReadPdfPresets_Click(object sender, RoutedEventArgs e)
    {
        var selected = SelectedPdfPreset();
        ReadPdfPresetsButton.IsEnabled = false;
        ReadPdfPresetsButton.Content = "读取中…";
        try
        {
            var presets = await _coordinator.ReadPdfPresetsAsync();
            var resolved = presets.FirstOrDefault(p => string.Equals(p, selected, StringComparison.OrdinalIgnoreCase));
            PdfPresetComboBox.Items.Clear();
            PdfPresetComboBox.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = "自动：最小文件大小", Tag = "" });
            foreach (var preset in presets)
                PdfPresetComboBox.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = preset, Tag = preset });
            if (!string.IsNullOrEmpty(selected) && resolved is null)
            {
                PdfPresetComboBox.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = selected + "（本机未找到）", Tag = selected });
                SelectPdfPreset(selected);
                System.Windows.MessageBox.Show(this, "本机当前没有找到已保存的预设“" + selected + "”。请重新选择可用预设。", "预设已变更", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else SelectPdfPreset(resolved ?? "");
            if (presets.Count == 0)
                System.Windows.MessageBox.Show(this, "Illustrator 没有返回 PDF 预设。请确认 Illustrator 已安装并可正常启动。", "没有可用预设", MessageBoxButton.OK, MessageBoxImage.Information);
            else GlobalStatusText.Text = $"已读取本机 Illustrator 的 {presets.Count} 个 PDF 预设";
            ScheduleSettingsSave();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, "读取 Illustrator PDF 预设失败：\n" + ex.Message, "读取失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            ReadPdfPresetsButton.Content = "读取预设";
            ReadPdfPresetsButton.IsEnabled = true;
        }
    }

    private string SelectedPdfPreset() => PdfPresetComboBox.SelectedItem is System.Windows.Controls.ComboBoxItem item
        ? item.Tag?.ToString() ?? ""
        : "";

    private void SelectPdfPreset(string value)
    {
        var found = PdfPresetComboBox.Items.Cast<object>().OfType<System.Windows.Controls.ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag?.ToString() ?? "", value, StringComparison.OrdinalIgnoreCase));
        if (found is null && !string.IsNullOrEmpty(value))
        {
            found = new System.Windows.Controls.ComboBoxItem { Content = value, Tag = value };
            PdfPresetComboBox.Items.Add(found);
        }
        PdfPresetComboBox.SelectedItem = found ?? PdfPresetComboBox.Items.Cast<object>().FirstOrDefault();
    }

    private void AddPdfPath_Click(object sender, RoutedEventArgs e) => BrowseInto(PdfPathsListBox, ExportKind.Pdf);
    private void AddJpgPath_Click(object sender, RoutedEventArgs e) => BrowseInto(JpgPathsListBox, ExportKind.Jpg);
    private void RemovePdfPath_Click(object sender, RoutedEventArgs e) => RemoveSelectedPath(PdfPathsListBox, PdfEnabledCheckBox, ExportKind.Pdf);
    private void RemoveJpgPath_Click(object sender, RoutedEventArgs e) => RemoveSelectedPath(JpgPathsListBox, JpgEnabledCheckBox, ExportKind.Jpg);

    private void BrowseInto(System.Windows.Controls.ListBox target, ExportKind kind)
    {
        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = "选择包含 Illustrator 文件的监控文件夹",
            UseDescriptionForTitle = true,
            SelectedPath = target.SelectedItem is string selected && Directory.Exists(selected) ? selected : ""
        };
        if (dialog.ShowDialog() == Forms.DialogResult.OK &&
            !target.Items.Cast<string>().Contains(dialog.SelectedPath, StringComparer.OrdinalIgnoreCase))
        {
            target.Items.Add(dialog.SelectedPath);
            if (SaveUiToSettings()) _coordinator.RefreshJob(kind);
        }
    }

    private void RemoveSelectedPath(System.Windows.Controls.ListBox target, System.Windows.Controls.CheckBox enabled, ExportKind kind)
    {
        if (target.SelectedItem is null) return;
        target.Items.Remove(target.SelectedItem);
        if (target.Items.Count == 0) enabled.IsChecked = false;
        if (SaveUiToSettings()) _coordinator.RefreshJob(kind);
    }

    private void StopJob_Click(object sender, RoutedEventArgs e)
    {
        var kind = ((System.Windows.Controls.Button)sender).Tag?.ToString() == "Pdf" ? ExportKind.Pdf : ExportKind.Jpg;
        if (kind == ExportKind.Pdf) PdfEnabledCheckBox.IsChecked = false;
        else JpgEnabledCheckBox.IsChecked = false;
        if (!SaveUiToSettings()) return;
        _coordinator.StopJob(kind);
        UpdateButtons();
    }

    private void Coordinator_StatusChanged(object? sender, JobStatus status)
    {
        Dispatcher.BeginInvoke(() =>
        {
            var state = $"{status.State} · 待处理 {status.Pending}";
            if (status.Kind == ExportKind.Pdf)
            {
                PdfStateText.Text = state;
                PdfResultText.Text = status.LastResult;
            }
            else
            {
                JpgStateText.Text = state;
                JpgResultText.Text = status.LastResult;
            }
            GlobalStatusText.Text = status.LastResult;
        });
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_reallyExit) return;
        e.Cancel = true;
        SaveUiToSettings(false);
        Hide();
        _trayIcon.ShowBalloonTip(2500, "VectorForge", "监控仍在后台运行。可从系统托盘退出。", Forms.ToolTipIcon.Info);
    }

    private void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private async Task ExitApplicationAsync()
    {
        _reallyExit = true;
        _settingsAutosaveTimer.Stop();
        SaveUiToSettings(false);
        await _coordinator.PauseAsync();
        _coordinator.Dispose();
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        Close();
        System.Windows.Application.Current.Shutdown();
    }

    private void ScheduleSettingsSave()
    {
        _settingsAutosaveTimer.Stop();
        _settingsAutosaveTimer.Start();
    }

    internal void SaveSettingsForSystemShutdown()
    {
        _settingsAutosaveTimer.Stop();
        SaveUiToSettings(false);
    }

    private void UpdateJpgSizingUi()
    {
        JpgDpiTextBox.IsEnabled = JpgSizingComboBox.SelectedIndex != 1;
        JpgPixelWidthTextBox.IsEnabled = JpgSizingComboBox.SelectedIndex == 1;
    }

    private void UpdateButtons()
    {
        StartButton.IsEnabled = !_coordinator.IsRunning;
        PauseButton.IsEnabled = _coordinator.IsRunning;
        SessionModeComboBox.IsEnabled = !_coordinator.IsRunning;
    }

    private static int ParseRange(System.Windows.Controls.TextBox box, string label, int minimum, int maximum)
    {
        if (!int.TryParse(box.Text, out var value) || value < minimum || value > maximum)
            throw new InvalidOperationException($"{label} 必须是 {minimum}–{maximum} 之间的整数。");
        return value;
    }

    private static void LoadPaths(System.Windows.Controls.ListBox list, IEnumerable<string> paths)
    {
        list.Items.Clear();
        foreach (var path in paths) list.Items.Add(path);
    }

    private static List<string> ReadPaths(System.Windows.Controls.ListBox list) =>
        list.Items.Cast<string>()
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static List<string> ParseKeywords(string text) => text
        .Split([';', '；', ',', '，', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(keyword => keyword.Length > 0)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    private static Drawing.Icon LoadTrayIcon()
    {
        using var stream = typeof(MainWindow).Assembly.GetManifestResourceStream("VectorForge.Assets.VectorForge.ico")!;
        using var icon = new Drawing.Icon(stream, 32, 32);
        return (Drawing.Icon)icon.Clone();
    }
    private void Retry_Click(object sender, RoutedEventArgs e) => RunCommand(sender, false);
    private void Reexport_Click(object sender, RoutedEventArgs e) => RunCommand(sender, true);
    private void RunCommand(object sender, bool force)
    {
        if (!SaveUiToSettings()) return;
        var kind = ((System.Windows.Controls.Button)sender).Tag?.ToString() == "Pdf" ? ExportKind.Pdf : ExportKind.Jpg;
        if (force)
        {
            var label = kind == ExportKind.Pdf ? "PDF" : "JPG";
            var answer = System.Windows.MessageBox.Show(
                $"这会忽略旁边已有的最新 {label}，把当前日期范围内的全部 AI 再导出一遍，并覆盖对应输出。\n\n确定继续吗？",
                "确认强制覆盖导出",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes) return;
            _coordinator.Reexport(kind);
        }
        else _coordinator.RetryFailures(kind);
        _coordinator.Start(); UpdateButtons();
    }
    private void Filter_Changed(object sender, TextChangedEventArgs e)
    {
        if (RecordsGrid is null) return;
        var text = RecordFilter.Text;
        System.Windows.Data.CollectionViewSource.GetDefaultView(_records).Filter = item =>
            item is ProcessingRecord record && (string.IsNullOrEmpty(text) ||
            (record.Path + record.Reason + record.Kind).Contains(text, StringComparison.OrdinalIgnoreCase));
    }
}
