using System.ComponentModel;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using PrivacyScreenGuard.Models;
using PrivacyScreenGuard.Services;
// WinForms 与 WPF 存在大量同名类型（Application/MessageBox/Brushes 等），显式消歧
using Application = System.Windows.Application;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;
using MessageBoxResult = System.Windows.MessageBoxResult;

namespace PrivacyScreenGuard.Windows;

/// <summary>
/// 主设置窗口：守护状态 / 检测参数 / 设备选择 / 快捷与启动 / 隐私与数据。
/// 控件变更 → 写回 App.Settings 并保存 → engine.Configure；
/// 显示器相关变更额外通知遮罩管理器重建。
/// 引擎事件在后台线程触发，UI 更新一律经 Dispatcher 封送回 UI 线程。
/// </summary>
public partial class MainWindow : Window
{
    // ==================== 运行时引用与状态 ====================

    private AppSettings _settings = new();
    private GuardEngine? _engine;
    private MaskWindowManager? _masks;

    /// <summary>摄像头下拉列表项缓存。</summary>
    private List<CameraItem> _cameraDevices = new();

    /// <summary>Loaded 初始化只执行一次。</summary>
    private bool _initialized;

    /// <summary>程序性赋值控件时置 true，避免把初始化当作用户修改回写。</summary>
    private bool _suppressEvents;

    /// <summary>热键主键过滤的防重入标志（TextChanged 内回写文本会再次触发）。</summary>
    private bool _filteringHotkeyKey;

    /// <summary>最近一次合法的热键主键（非法输入时回退用）。</summary>
    private string _lastValidHotkeyKey = "P";

    /// <summary>"最小化到托盘"气泡提示只弹一次。</summary>
    private bool _trayHintShown;

    /// <summary>热键主键合法格式：A-Z、0-9 或 F1-F12。</summary>
    private static readonly Regex HotkeyKeyRegex =
        new("^(?:[A-Z]|[0-9]|F(?:[1-9]|1[0-2]))$", RegexOptions.Compiled);

    /// <summary>检测帧率可选项（与 XAML 中 CmbFps 条目顺序一致）。</summary>
    private static readonly double[] FpsOptions = { 5, 6, 8, 10 };

    public MainWindow()
    {
        InitializeComponent();

        Loaded += OnLoaded;
        Closed += OnWindowClosed;
        Closing += OnWindowClosing;
        SourceInitialized += OnSourceInitialized;

        // 全部事件在 code-behind 挂接（XAML 不绑定事件，便于独立维护）
        BtnPauseResume.Click += OnPauseResumeClick;
        SldThreshold.ValueChanged += OnThresholdChanged;
        SldTriggerDelay.ValueChanged += OnTriggerDelayChanged;
        SldRecoverDelay.ValueChanged += OnRecoverDelayChanged;
        SldBlur.ValueChanged += OnBlurChanged;
        ChkStrictPose.Checked += OnStrictPoseToggled;
        ChkStrictPose.Unchecked += OnStrictPoseToggled;
        CmbFps.SelectionChanged += OnFpsChanged;
        CmbNoFacePolicy.SelectionChanged += OnNoFacePolicyChanged;
        CmbMultiPerson.SelectionChanged += OnMultiPersonChanged;
        CmbCamera.SelectionChanged += OnCameraChanged;
        BtnRefreshCameras.Click += OnRefreshCamerasClick;
        RbAllMonitors.Checked += OnMonitorModeChanged;
        RbPrimaryMonitor.Checked += OnMonitorModeChanged;
        RbSelectedMonitors.Checked += OnMonitorModeChanged;
        LstMonitors.SelectionChanged += OnMonitorSelectionChanged;
        CmbHotkeyMods.SelectionChanged += OnHotkeyModsChanged;
        TxtHotkeyKey.TextChanged += OnHotkeyKeyTextChanged;
        ChkAutostart.Checked += OnAutostartToggled;
        ChkAutostart.Unchecked += OnAutostartToggled;
        ChkCameraEnabled.Checked += OnCameraEnabledToggled;
        ChkCameraEnabled.Unchecked += OnCameraEnabledToggled;
        // 健康提醒：五个开关与五个滑块统一走 OnHealthSettingChanged
        // （ValueChanged 的参数类型 RoutedPropertyChangedEventArgs<double> 派生自 RoutedEventArgs，
        //   方法组参数逆变允许与 Checked/Unchecked 共用同一处理器）
        ChkSedentary.Checked += OnHealthSettingChanged;
        ChkSedentary.Unchecked += OnHealthSettingChanged;
        SldSedentary.ValueChanged += OnHealthSettingChanged;
        ChkNear.Checked += OnHealthSettingChanged;
        ChkNear.Unchecked += OnHealthSettingChanged;
        SldNear.ValueChanged += OnHealthSettingChanged;
        ChkSlouch.Checked += OnHealthSettingChanged;
        ChkSlouch.Unchecked += OnHealthSettingChanged;
        SldSlouch.ValueChanged += OnHealthSettingChanged;
        ChkWater.Checked += OnHealthSettingChanged;
        ChkWater.Unchecked += OnHealthSettingChanged;
        SldWater.ValueChanged += OnHealthSettingChanged;
        ChkBreak.Checked += OnHealthSettingChanged;
        ChkBreak.Unchecked += OnHealthSettingChanged;
        SldBreak.ValueChanged += OnHealthSettingChanged;
        // 更新检查
        BtnCheckUpdates.Click += OnCheckUpdatesClick;
        ChkCheckUpdates.Checked += OnCheckUpdatesToggled;
        ChkCheckUpdates.Unchecked += OnCheckUpdatesToggled;
        BtnClearTemplate.Click += OnClearTemplateClick;
        BtnTheme.Click += OnThemeToggleClick;
    }

    // ==================== 初始化 ====================

    /// <summary>窗口加载：取 App 单例引用 → 订阅引擎事件 → 初始化控件值 → 异步枚举摄像头。</summary>
    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_initialized)
        {
            return;
        }
        _initialized = true;

        _settings = App.Settings;
        _engine = App.Engine;
        _masks = App.Masks;

        // 引擎事件在后台线程触发 → 处理器内统一封送 UI 线程
        if (_engine is not null)
        {
            _engine.StatusChanged += OnEngineStatusChanged;
            _engine.EngineError += OnEngineError;
        }
        App.EngineRestarted += OnEngineRestarted;

        InitializeControls();
        await RefreshCamerasAsync(_settings.CameraIndex);

        // 启动 30 秒后静默检查更新（失败与无更新均静默，发现新版才提示）
        if (_settings.CheckUpdatesOnStartup)
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(30_000);
                await Dispatcher.BeginInvoke(() => CheckUpdatesAsync(silent: true));
            });
        }
    }

    /// <summary>按当前设置初始化各控件的显示值（程序性赋值，不触发回写）。</summary>
    private void InitializeControls()
    {
        _suppressEvents = true;
        try
        {
            // 守护状态
            ApplyEngineStatus(_engine is { IsRunning: true } ? "监控中" : "守护已停止");

            // 检测参数
            SldThreshold.Value = _settings.OwnerThreshold;
            TxtThresholdValue.Text = _settings.OwnerThreshold.ToString("0.00");
            SldTriggerDelay.Value = _settings.TriggerDelayMs;
            TxtTriggerDelayValue.Text = $"{_settings.TriggerDelayMs} ms";
            SldRecoverDelay.Value = _settings.RecoverDelayMs;
            TxtRecoverDelayValue.Text = $"{_settings.RecoverDelayMs} ms";
            SldBlur.Value = _settings.BlurStrength;
            TxtBlurValue.Text = _settings.BlurStrength == 0
                ? "纯黑"
                : $"{_settings.BlurStrength}";
            ChkStrictPose.IsChecked = _settings.StrictPoseMode;
            CmbFps.SelectedIndex = NearestFpsIndex(_settings.CaptureFps);
            CmbNoFacePolicy.SelectedIndex = _settings.NoFacePolicy == NoFacePolicy.Lock ? 1 : 0;
            CmbMultiPerson.SelectedIndex = _settings.MultiPersonPolicy == MultiPersonPolicy.StrangerTriggersMask ? 1 : 0;

            // 设备选择（摄像头下拉由 RefreshCamerasAsync 异步填充）
            switch (_settings.MonitorMode)
            {
                case MonitorMode.Primary:
                    RbPrimaryMonitor.IsChecked = true;
                    break;
                case MonitorMode.Selected:
                    RbSelectedMonitors.IsChecked = true;
                    break;
                default:
                    RbAllMonitors.IsChecked = true;
                    break;
            }
            LstMonitors.IsEnabled = _settings.MonitorMode == MonitorMode.Selected;
            PopulateMonitors();

            // 快捷与启动
            CmbHotkeyMods.SelectedIndex = ModsTextToIndex(_settings.HotkeyModifiers);
            _lastValidHotkeyKey = FilterHotkeyKey(_settings.HotkeyKey);
            if (!HotkeyKeyRegex.IsMatch(_lastValidHotkeyKey))
            {
                _lastValidHotkeyKey = "P"; // 设置文件里残留非法主键时回退默认值
            }
            TxtHotkeyKey.Text = _lastValidHotkeyKey;
            ChkAutostart.IsChecked = _settings.Autostart;
            ChkCameraEnabled.IsChecked = _settings.CameraEnabled;

            // 健康提醒（值已经过 Sanitize 夹取，滑块不会越界；IsEnabled 跟随开关保持视觉一致）
            ChkSedentary.IsChecked = _settings.Health.SedentaryEnabled;
            SldSedentary.Value = _settings.Health.SedentaryThresholdMinutes;
            TxtSedentary.Text = $"{_settings.Health.SedentaryThresholdMinutes}";
            SldSedentary.IsEnabled = _settings.Health.SedentaryEnabled;
            ChkNear.IsChecked = _settings.Health.NearEnabled;
            SldNear.Value = _settings.Health.NearThreshold;
            TxtNear.Text = _settings.Health.NearThreshold.ToString("0.00");
            SldNear.IsEnabled = _settings.Health.NearEnabled;
            ChkSlouch.IsChecked = _settings.Health.SlouchEnabled;
            SldSlouch.Value = _settings.Health.SlouchThreshold;
            TxtSlouch.Text = _settings.Health.SlouchThreshold.ToString("0.00");
            SldSlouch.IsEnabled = _settings.Health.SlouchEnabled;
            ChkWater.IsChecked = _settings.Health.WaterEnabled;
            SldWater.Value = _settings.Health.WaterIntervalMinutes;
            TxtWater.Text = $"{_settings.Health.WaterIntervalMinutes}";
            SldWater.IsEnabled = _settings.Health.WaterEnabled;
            ChkBreak.IsChecked = _settings.Health.BreakEnabled;
            SldBreak.Value = _settings.Health.BreakIntervalMinutes;
            TxtBreak.Text = $"{_settings.Health.BreakIntervalMinutes}";
            SldBreak.IsEnabled = _settings.Health.BreakEnabled;

            // 更新检查
            TxtAppVersion.Text = $"v{UpdateCheckService.CurrentVersion.ToString(3)}";
            ChkCheckUpdates.IsChecked = _settings.CheckUpdatesOnStartup;

            // 主题按钮图标：当前深色 → 显示 ☀️（点击切明亮）；当前明亮 → 显示 🌙（点击切深色）
            TxtThemeIcon.Text = _settings.Theme == "dark" ? "☀" : "☾";

            // 版本号
            Version? version = Assembly.GetExecutingAssembly().GetName().Version;
            TxtVersion.Text = $"隐私屏 v{version?.ToString(3) ?? "1.0"}";
        }
        finally
        {
            _suppressEvents = false;
        }
    }

    /// <summary>把显示器列表填充到 ListBox（序号 + 设备名 + 分辨率），并选中已保存的选择。</summary>
    private void PopulateMonitors()
    {
        List<DisplayItem> items = MaskWindowManager.ListDisplays()
            .Select(d => new DisplayItem(d.Index, d.DeviceName, d.PhysicalBounds.Width, d.PhysicalBounds.Height))
            .ToList();
        LstMonitors.ItemsSource = items;
        foreach (DisplayItem item in items)
        {
            if (_settings.SelectedMonitors.Contains(item.Index))
            {
                LstMonitors.SelectedItems.Add(item);
            }
        }
    }

    // ==================== WndProc hook（全局热键入口） ====================

    /// <summary>窗口句柄创建后挂消息 hook：把 WM_HOTKEY 转发给 HotkeyService。</summary>
    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        if (PresentationSource.FromVisual(this) is HwndSource source)
        {
            source.AddHook(WndProcHook);
        }
    }

    /// <summary>消息钩子：仅处理 WM_HOTKEY，wParam 即注册时传入的热键 ID。</summary>
    private IntPtr WndProcHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == HotkeyService.WM_HOTKEY)
        {
            HotkeyService.RaiseFromWndProc((uint)wParam.ToInt64());
            handled = true;
        }
        return IntPtr.Zero;
    }

    // ==================== 引擎状态显示 ====================

    /// <summary>按资源键取当前主题画刷（跟随明暗主题自动变化）。</summary>
    private Brush ThemeBrush(string key) => (Brush)TryFindResource(key) ?? Brushes.Gray;

    /// <summary>设置状态指示灯颜色，并同步呼吸灯的外发光颜色（柔和外发光）。</summary>
    private void SetStatusLight(string brushKey)
    {
        var brush = (System.Windows.Media.SolidColorBrush)ThemeBrush(brushKey);
        StatusLight.Fill = brush;
        StatusGlow.Color = brush.Color;
    }

    private void OnEngineStatusChanged(string status)
    {
        // 引擎事件在后台线程触发 → 封送 UI 线程
        Dispatcher.BeginInvoke(() => ApplyEngineStatus(status));
    }

    private void OnEngineError(CameraError error, string message)
    {
        Dispatcher.BeginInvoke(() =>
        {
            SetStatusLight("Danger"); // 错误：指示灯变红
            ShowMessage($"{DescribeError(error)}{message}", isError: true);
        });
    }

    private void OnEngineRestarted()
    {
        Dispatcher.BeginInvoke(() =>
        {
            ApplyEngineStatus(_engine is { IsRunning: true } ? "监控中" : "守护已停止");
        });
    }

    /// <summary>应用状态文本到界面，并按文本语义刷新指示灯（错误文本不改色，保持红色）。</summary>
    private void ApplyEngineStatus(string status)
    {
        StatusText.Text = status;
        if (status.Contains("监控中") || status.Contains("遮罩"))
        {
            SetStatusLight("StatusOK"); // 运行中（含遮罩触发/解除，均属正常守护）
        }
        else if (status.Contains("已暂停") || status.Contains("守护已停止"))
        {
            SetStatusLight("StatusWarn"); // 暂停 / 停止（灰色）
        }
        UpdateEngineUi();
    }

    /// <summary>按引擎运行状态刷新"摄像头使用中"指示与暂停/恢复按钮文案。</summary>
    private void UpdateEngineUi()
    {
        bool running = _engine is not null && _engine.IsRunning;
        BtnPauseResume.Content = running ? "暂停守护" : "恢复守护";
        CameraInUseText.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string DescribeError(CameraError error) => error switch
    {
        CameraError.NoCamera => "摄像头不可用：",
        CameraError.Busy => "摄像头被占用：",
        CameraError.Disconnected => "摄像头已断开：",
        CameraError.TooDark => "光线过暗：",
        CameraError.ModelMissing => "模型缺失：",
        _ => "守护异常："
    };

    /// <summary>底部状态栏显示提示消息（红色表示异常）。</summary>
    private void ShowMessage(string message, bool isError = false)
    {
        TxtStatusMessage.Text = message;
        TxtStatusMessage.Foreground = ThemeBrush(isError ? "Danger" : "Fg.Secondary");
    }

    // ==================== 暂停 / 恢复 ====================

    private void OnPauseResumeClick(object sender, RoutedEventArgs e)
    {
        if (_engine is null)
        {
            return;
        }
        if (_engine.IsRunning)
        {
            _engine.Pause();
        }
        else
        {
            _engine.Resume();
        }
        UpdateEngineUi(); // 状态文本与托盘由引擎 StatusChanged 事件统一同步
    }

    // ==================== 检测参数 ====================

    private void OnThresholdChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEvents)
        {
            return;
        }
        TxtThresholdValue.Text = SldThreshold.Value.ToString("0.00");
        _settings.OwnerThreshold = SldThreshold.Value;
        SaveAndConfigure();
    }

    private void OnTriggerDelayChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEvents)
        {
            return;
        }
        TxtTriggerDelayValue.Text = $"{(int)SldTriggerDelay.Value} ms";
        _settings.TriggerDelayMs = (int)SldTriggerDelay.Value;
        SaveAndConfigure();
    }

    private void OnRecoverDelayChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEvents)
        {
            return;
        }
        TxtRecoverDelayValue.Text = $"{(int)SldRecoverDelay.Value} ms";
        _settings.RecoverDelayMs = (int)SldRecoverDelay.Value;
        SaveAndConfigure();
    }

    /// <summary>模糊强度变更：写回设置。下次遮罩显示时生效（0 = 纯黑遮罩，&gt;0 = 截屏高斯模糊）。</summary>
    private void OnBlurChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEvents)
        {
            return;
        }
        int value = (int)SldBlur.Value;
        TxtBlurValue.Text = value == 0 ? "纯黑" : $"{value}";
        _settings.BlurStrength = value;
        SaveAndConfigure();
    }

    /// <summary>
    /// "识别侧脸样本"开关：开启后侧脸/贴边脸也参与身份比对——配合注册的左/右转头模板，
    /// 能认出侧脸的主人，同时拦截侧脸的陌生人；关闭时侧脸不参与判定（防护空档）。
    /// </summary>
    private void OnStrictPoseToggled(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents)
        {
            return;
        }
        _settings.StrictPoseMode = ChkStrictPose.IsChecked == true;
        SaveAndConfigure();
        ShowMessage(_settings.StrictPoseMode
            ? "已开启侧脸识别：侧脸也会与多姿态模板比对（建议已完成左/右转头采集）"
            : "已关闭侧脸识别：侧脸不参与判定（扭头看侧屏不触发，但侧身陌生人也会被放过）");
    }

    private void OnFpsChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents)
        {
            return;
        }
        int index = CmbFps.SelectedIndex;
        if (index < 0 || index >= FpsOptions.Length)
        {
            return;
        }
        _settings.CaptureFps = FpsOptions[index];
        SaveAndConfigure();
    }

    private void OnNoFacePolicyChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents)
        {
            return;
        }
        _settings.NoFacePolicy = CmbNoFacePolicy.SelectedIndex == 1
            ? NoFacePolicy.Lock
            : NoFacePolicy.KeepNormal;
        SaveAndConfigure();
    }

    /// <summary>
    /// 多人在场策略变更：
    /// 放行模式 = 主人在场即不遮罩（给别人看屏幕方便）；严格模式 = 出现陌生人脸即遮罩（即使主人也在场）。
    /// </summary>
    private void OnMultiPersonChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents)
        {
            return;
        }
        _settings.MultiPersonPolicy = CmbMultiPerson.SelectedIndex == 1
            ? MultiPersonPolicy.StrangerTriggersMask
            : MultiPersonPolicy.OwnerPresenceOpens;
        SaveAndConfigure();
        ShowMessage(_settings.MultiPersonPolicy == MultiPersonPolicy.StrangerTriggersMask
            ? "多人在场：有陌生人即遮罩（即使你也在画面里）"
            : "多人在场：主人在场即放行（给别人看屏幕不用暂停守护）");
    }

    /// <summary>
    /// 明暗主题切换：整体替换画刷字典（DynamicResource 自动刷新全部窗口控件），
    /// 持久化到设置并更新按钮图标。
    /// </summary>
    private void OnThemeToggleClick(object sender, RoutedEventArgs e)
    {
        string next = ThemeManager.Toggle();
        _settings.Theme = next;
        SettingsService.Save(_settings);
        TxtThemeIcon.Text = next == "dark" ? "☀" : "☾";
        ShowMessage(next == "dark" ? "已切换到深色主题" : "已切换到明亮主题");
    }

    /// <summary>保存设置并让引擎按新参数重建状态机（帧率变化时引擎内部会自动重启采集）。</summary>
    private void SaveAndConfigure()
    {
        SettingsService.Save(_settings);
        _engine?.Configure(_settings);
    }

    // ==================== 设备选择 ====================

    private async void OnRefreshCamerasClick(object sender, RoutedEventArgs e)
    {
        await RefreshCamerasAsync(_settings.CameraIndex);
    }

    /// <summary>后台枚举摄像头并回填下拉框（枚举会逐个探测设备，放后台线程避免卡 UI）。</summary>
    private async Task RefreshCamerasAsync(int preferredIndex)
    {
        BtnRefreshCameras.IsEnabled = false;
        try
        {
            List<(int Index, string Name)> devices = await Task.Run(CameraService.EnumerateDevices);
            _cameraDevices = devices.Select(d => new CameraItem(d.Index, d.Name)).ToList();

            _suppressEvents = true;
            try
            {
                CmbCamera.ItemsSource = _cameraDevices;
                CameraItem? target = _cameraDevices.FirstOrDefault(c => c.Index == preferredIndex)
                                     ?? _cameraDevices.FirstOrDefault();
                CmbCamera.SelectedItem = target; // 找不到已保存索引时默认第一台
            }
            finally
            {
                _suppressEvents = false;
            }

            if (_cameraDevices.Count == 0)
            {
                ShowMessage("未检测到可用摄像头，请连接后点击\"刷新\"。", isError: true);
            }
        }
        catch (Exception ex)
        {
            ShowMessage($"枚举摄像头失败：{ex.Message}", isError: true);
        }
        finally
        {
            BtnRefreshCameras.IsEnabled = true;
        }
    }

    private void OnCameraChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents)
        {
            return;
        }
        if (CmbCamera.SelectedItem is not CameraItem item)
        {
            return;
        }

        _settings.CameraIndex = item.Index;
        SaveAndConfigure();
        // 摄像头索引需重启采集才能生效（引擎 Configure 不会自动切换设备）
        if (_engine is not null && _engine.IsRunning)
        {
            _engine.Stop();
            _engine.Start();
        }
        ShowMessage($"已切换到 {item.Display}");
    }

    private void OnMonitorModeChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents)
        {
            return;
        }
        ApplyMonitorSettings();
    }

    private void OnMonitorSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents)
        {
            return;
        }
        // 多选列表仅在"自选"模式下参与配置
        if (RbSelectedMonitors.IsChecked == true)
        {
            ApplyMonitorSettings();
        }
    }

    /// <summary>
    /// 应用显示器守护配置：写回设置 → 保存 → Configure → 遮罩管理器按新范围重建。
    /// UpdateConfig 内部会隐藏并清空全部遮罩窗口；随后按引擎状态机的真实状态补发
    /// Show/Hide 恢复一致（此前无条件 ShowAll 会让遮罩强制弹出且永不消失）。
    /// </summary>
    private void ApplyMonitorSettings()
    {
        MonitorMode mode = RbAllMonitors.IsChecked == true ? MonitorMode.All
            : RbPrimaryMonitor.IsChecked == true ? MonitorMode.Primary
            : MonitorMode.Selected;
        _settings.MonitorMode = mode;
        _settings.SelectedMonitors = GetSelectedMonitorIndexes();
        LstMonitors.IsEnabled = mode == MonitorMode.Selected;

        SaveAndConfigure();
        if (_masks is not null)
        {
            _masks.UpdateConfig(mode, _settings.SelectedMonitors);
        }
        if (_engine is { IsRunning: true })
        {
            // 仅当陌生人正触发（状态机 MaskActive）时才重新 Show，否则保持隐藏；
            // 此后遮罩的显示/隐藏完全由引擎状态机控制
            _engine.ResyncMaskAction();
        }
        ShowMessage($"守护显示器：{DescribeMonitorMode(mode)}");
    }

    private List<int> GetSelectedMonitorIndexes()
    {
        return LstMonitors.SelectedItems.OfType<DisplayItem>().Select(d => d.Index).ToList();
    }

    private static string DescribeMonitorMode(MonitorMode mode) => mode switch
    {
        MonitorMode.Primary => "仅主屏",
        MonitorMode.Selected => "自选显示器",
        _ => "所有显示器"
    };

    // ==================== 快捷与启动 ====================

    private void OnHotkeyModsChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents)
        {
            return;
        }
        ApplyHotkeySetting();
    }

    private void OnHotkeyKeyTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressEvents || _filteringHotkeyKey)
        {
            return;
        }

        string filtered = FilterHotkeyKey(TxtHotkeyKey.Text);
        if (filtered.Length > 0 && !HotkeyKeyRegex.IsMatch(filtered))
        {
            // 非法内容（如 "F13"、"2F"）→ 回退到上一个合法主键
            _filteringHotkeyKey = true;
            TxtHotkeyKey.Text = _lastValidHotkeyKey;
            TxtHotkeyKey.CaretIndex = _lastValidHotkeyKey.Length;
            _filteringHotkeyKey = false;
            return;
        }

        if (TxtHotkeyKey.Text != filtered)
        {
            // 统一转大写 / 截断后回写（会再次触发 TextChanged，用 _filteringHotkeyKey 防重入）
            _filteringHotkeyKey = true;
            TxtHotkeyKey.Text = filtered;
            TxtHotkeyKey.CaretIndex = filtered.Length;
            _filteringHotkeyKey = false;
        }

        if (filtered.Length == 0)
        {
            return; // 清空的中间态：不应用，保留原设置
        }

        _lastValidHotkeyKey = filtered;
        ApplyHotkeySetting();
    }

    /// <summary>保存热键设置并重新注册；失败（常见为热键被占用）给出状态栏与托盘提示。</summary>
    private void ApplyHotkeySetting()
    {
        if (Application.Current is not App app)
        {
            return;
        }

        _settings.HotkeyModifiers = ModsIndexToText(CmbHotkeyMods.SelectedIndex);
        _settings.HotkeyKey = _lastValidHotkeyKey;
        SettingsService.Save(_settings);

        if (app.RegisterMainWindowHotkey())
        {
            ShowMessage($"快捷键已更新：{_settings.HotkeyModifiers} + {_settings.HotkeyKey}");
        }
        else
        {
            ShowMessage("快捷键注册失败：热键可能被占用，请更换组合键。", isError: true);
            app.ShowTrayBubble("快捷键注册失败", "热键可能被占用，请在主窗口更换组合键。");
        }
    }

    /// <summary>过滤热键主键输入：只保留字母数字、统一大写、最长 3 个字符（F12）。</summary>
    private static string FilterHotkeyKey(string raw)
    {
        var builder = new StringBuilder();
        foreach (char c in raw.ToUpperInvariant())
        {
            if (char.IsAsciiLetterOrDigit(c))
            {
                builder.Append(c);
            }
        }
        string text = builder.ToString();
        return text.Length > 3 ? text[..3] : text;
    }

    private static string ModsIndexToText(int index) => index switch
    {
        1 => "Ctrl+Shift",
        2 => "Alt+Shift",
        _ => "Ctrl+Alt"
    };

    private static int ModsTextToIndex(string text) => text switch
    {
        "Ctrl+Shift" => 1,
        "Alt+Shift" => 2,
        _ => 0
    };

    private static int NearestFpsIndex(double fps)
    {
        int best = 0;
        double bestDiff = double.MaxValue;
        for (int i = 0; i < FpsOptions.Length; i++)
        {
            double diff = Math.Abs(FpsOptions[i] - fps);
            if (diff < bestDiff)
            {
                best = i;
                bestDiff = diff;
            }
        }
        return best;
    }

    private void OnAutostartToggled(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents)
        {
            return;
        }
        bool on = ChkAutostart.IsChecked == true;
        _settings.Autostart = on;
        SettingsService.Save(_settings);
        AutostartService.SetEnabled(on);
        ShowMessage(on ? "已开启开机自动启动" : "已关闭开机自动启动");
    }

    private void OnCameraEnabledToggled(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents || _engine is null)
        {
            return;
        }
        bool on = ChkCameraEnabled.IsChecked == true;
        _settings.CameraEnabled = on;
        SettingsService.Save(_settings);
        if (on)
        {
            if (!_engine.IsRunning)
            {
                _engine.Start(); // 无主人模板时引擎会经 EngineError 提示
            }
        }
        else if (_engine.IsRunning)
        {
            _engine.Stop();
        }
        UpdateEngineUi(); // 状态文本与托盘由引擎 StatusChanged 事件统一同步
    }

    // ==================== 健康提醒 ====================

    /// <summary>健康设置保存防抖：拖动滑块期间 ValueChanged 连续触发，停手 400ms 后才真正落盘。</summary>
    private DispatcherTimer? _healthSaveDebounce;

    /// <summary>
    /// 健康提醒任一控件变更：先做轻量 UI 反馈（数值文本联动、滑块可用性），
    /// 再经 400ms 防抖写入设置并热更新健康状态机（避免拖动过程中每帧写盘、刷屏提示）。
    /// </summary>
    private void OnHealthSettingChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents)
        {
            return;
        }

        // 1. 数值实时联动（等宽字体保证拖动时数字无位移）
        TxtSedentary.Text = SldSedentary.Value.ToString("0");
        TxtNear.Text = SldNear.Value.ToString("0.00");
        TxtSlouch.Text = SldSlouch.Value.ToString("0.00");
        TxtWater.Text = SldWater.Value.ToString("0");
        TxtBreak.Text = SldBreak.Value.ToString("0");

        // 2. 开关关闭时对应滑块禁用（视觉与逻辑一致）
        SldSedentary.IsEnabled = ChkSedentary.IsChecked == true;
        SldNear.IsEnabled = ChkNear.IsChecked == true;
        SldSlouch.IsEnabled = ChkSlouch.IsChecked == true;
        SldWater.IsEnabled = ChkWater.IsChecked == true;
        SldBreak.IsEnabled = ChkBreak.IsChecked == true;

        // 3. 防抖保存：最后一次变更 400ms 后执行
        if (_healthSaveDebounce is null)
        {
            _healthSaveDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            _healthSaveDebounce.Tick += SaveHealthSettingsNow;
        }
        _healthSaveDebounce.Stop();
        _healthSaveDebounce.Start();
    }

    /// <summary>防抖到期：从控件读回全部值写入设置 → 保存 → 热更新健康状态机
    /// （<see cref="HealthMonitor.UpdateSettings"/> 会按新值清零已关闭功能的计时）。</summary>
    private void SaveHealthSettingsNow(object? sender, EventArgs e)
    {
        _healthSaveDebounce?.Stop();

        _settings.Health.SedentaryEnabled = ChkSedentary.IsChecked == true;
        _settings.Health.SedentaryThresholdMinutes = (int)SldSedentary.Value;
        _settings.Health.NearEnabled = ChkNear.IsChecked == true;
        _settings.Health.NearThreshold = SldNear.Value;
        _settings.Health.SlouchEnabled = ChkSlouch.IsChecked == true;
        _settings.Health.SlouchThreshold = SldSlouch.Value;
        _settings.Health.WaterEnabled = ChkWater.IsChecked == true;
        _settings.Health.WaterIntervalMinutes = (int)SldWater.Value;
        _settings.Health.BreakEnabled = ChkBreak.IsChecked == true;
        _settings.Health.BreakIntervalMinutes = (int)SldBreak.Value;

        SettingsService.Save(_settings);
        App.Health?.UpdateSettings(_settings.Health);
        ShowMessage("健康提醒设置已保存");
    }

    /// <summary>健康提醒通知：写入底部消息栏（App 在 UI 线程经此转发健康提醒文案）。</summary>
    public void ShowHealthNotice(string message)
    {
        ShowMessage(message);
    }

    // ==================== 更新检查 ====================

    /// <summary>检查更新按钮：手动检查并给出明确文案反馈。</summary>
    private async void OnCheckUpdatesClick(object sender, RoutedEventArgs e)
    {
        await CheckUpdatesAsync(silent: false);
    }

    /// <summary>启动时自动检查开关切换：保存设置。</summary>
    private void OnCheckUpdatesToggled(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents)
        {
            return;
        }
        _settings.CheckUpdatesOnStartup = ChkCheckUpdates.IsChecked == true;
        SettingsService.Save(_settings);
        ShowMessage("更新检查设置已保存");
    }

    /// <summary>
    /// 检查更新。silent=true（启动自动检查）：失败与无更新静默，发现新版弹窗询问；
    /// silent=false（手动点击）：所有结果均有文案反馈。
    /// </summary>
    private async Task CheckUpdatesAsync(bool silent)
    {
        BtnCheckUpdates.IsEnabled = false;
        if (!silent)
        {
            ShowMessage("正在检查更新…");
        }
        try
        {
            var result = await new UpdateCheckService().CheckAsync();
            if (result is null)
            {
                if (!silent)
                {
                    ShowMessage("检查更新失败：暂无网络或仓库暂不可访问", isError: true);
                }
                return;
            }
            if (!result.HasUpdate)
            {
                if (!silent)
                {
                    ShowMessage($"当前已是最新版本（v{UpdateCheckService.CurrentVersion.ToString(3)}）");
                }
                return;
            }

            ShowMessage($"发现新版本 v{result.LatestVersion}！");
            var choice = MessageBox.Show(this,
                $"发现新版本 v{result.LatestVersion}，是否前往下载页获取？",
                "软件更新", MessageBoxButton.YesNo, MessageBoxImage.Information);
            if (choice == MessageBoxResult.Yes)
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(result.ReleaseUrl)
                {
                    UseShellExecute = true,
                });
            }
        }
        finally
        {
            BtnCheckUpdates.IsEnabled = true;
        }
    }

    // ==================== 隐私与数据 ====================

    private void OnClearTemplateClick(object sender, RoutedEventArgs e)
    {
        MessageBoxResult result = MessageBox.Show(
            this,
            "将删除加密的主人特征，需要重新注册才能继续守护。\n\n是否继续？",
            "清除主人人脸数据",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        _engine?.Stop();        // 先停止守护
        TemplateStore.Delete(); // 删除加密模板文件

        // 重新打开注册向导
        var wizard = new SetupWizardWindow { Owner = this };
        if (wizard.ShowDialog() == true)
        {
            if (_engine is not null && TemplateStore.TryLoad(out float[] feature))
            {
                _engine.SetOwnerTemplate(feature);
                if (_settings.CameraEnabled)
                {
                    _engine.Start();
                }
                ShowMessage("主人数据已重新注册，守护继续。");
                (Application.Current as App)?.RaiseEngineRestarted();
            }
            else
            {
                ShowMessage("重新加载主人特征失败，守护保持停止，可重试或退出。", isError: true);
            }
        }
        else
        {
            // 用户取消重新注册 → 无法继续守护 → 退出应用
            ShowMessage("未重新注册主人数据，应用即将退出。", isError: true);
            (Application.Current as App)?.ShutdownForExit();
        }
    }

    // ==================== 窗口关闭（最小化到托盘） ====================

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        // 真正退出（托盘"退出"、向导取消）时放行；否则取消关闭并隐藏到托盘
        if (Application.Current is App app && app.ForceExit)
        {
            return;
        }

        e.Cancel = true;
        Hide();
        if (!_trayHintShown)
        {
            _trayHintShown = true;
            (Application.Current as App)?.ShowTrayBubble(
                "仍在守护中",
                "应用将最小化到托盘继续守护，右键托盘图标可退出。");
        }
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        // 退订事件，避免窗口重新打开时重复订阅累积
        if (_engine is not null)
        {
            _engine.StatusChanged -= OnEngineStatusChanged;
            _engine.EngineError -= OnEngineError;
        }
        App.EngineRestarted -= OnEngineRestarted;
    }

    // ==================== 列表项模型 ====================

    /// <summary>摄像头下拉列表项。</summary>
    private sealed record CameraItem(int Index, string Name)
    {
        public string Display => string.IsNullOrWhiteSpace(Name) ? $"摄像头 {Index}" : Name;
    }

    /// <summary>显示器多选列表项。</summary>
    private sealed record DisplayItem(int Index, string DeviceName, int WidthPx, int HeightPx)
    {
        public string Display => $"显示器 {Index + 1}（{DeviceName}，{WidthPx}×{HeightPx}）";
    }
}
