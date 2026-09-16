using System;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using PrivacyScreenGuard.Models;
using PrivacyScreenGuard.Services;
using PrivacyScreenGuard.Windows;
// WinForms 与 WPF 的多个类型同名（Application/MessageBox 等），显式消歧
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;

namespace PrivacyScreenGuard;

/// <summary>
/// 应用入口与全局接线。
/// 启动流程：单实例互斥 → 加载设置 → 创建引擎（模型缺失则提示退出）→ 模板与配置 →
/// 遮罩管理器 → 引擎事件接线（遮罩/托盘）→ 托盘 → 向导或主窗口 → 全局热键 → 开机启动同步。
/// 引擎事件均在后台线程触发，所有 UI / 托盘操作一律经 Dispatcher 封送回 UI 线程。
/// </summary>
public partial class App : Application
{
    /// <summary>本应用全局热键的注册 ID（同一窗口内唯一即可）。</summary>
    public const uint HotkeyId = 1;

    /// <summary>应用设置（OnStartup 阶段加载，主窗口与各服务共享同一实例）。</summary>
    public static AppSettings Settings { get; private set; } = new();

    /// <summary>守护引擎单例（服务创建失败时为 null）。</summary>
    public static GuardEngine? Engine { get; private set; }

    /// <summary>遮罩窗口管理器（在 UI 线程创建）。</summary>
    public static MaskWindowManager? Masks { get; private set; }

    /// <summary>主人模板重新注册成功后触发（主窗口据此刷新状态显示）。</summary>
    public static event Action? EngineRestarted;

    /// <summary>单实例互斥体名称。</summary>
    private const string SingleInstanceMutexName = @"Local\PrivacyScreenGuard.SingleInstance";

    private Mutex? _singleInstanceMutex;
    private bool _ownsMutex;
    private bool _forceExit;
    private TrayIconService? _tray;
    private HotkeyService? _hotkeys;
    private MainWindow? _mainWindow;

    /// <summary>是否处于强制退出流程（为 true 时主窗口 Closing 不再拦截）。</summary>
    public bool ForceExit => _forceExit;

    protected override void OnStartup(StartupEventArgs e)
    {
        // 0. 全局异常兜底：UI 线程未处理异常弹中文提示，不崩溃
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        // 1. 单实例互斥：已有实例在运行 → 提示并退出
        _singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out bool createdNew);
        _ownsMutex = createdNew;
        if (!createdNew)
        {
            MessageBox.Show("隐私屏守护已在运行，请查看系统托盘图标。", "隐私屏",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        base.OnStartup(e);

        // 2. 加载设置
        Settings = SettingsService.Load();

        // 3. 创建摄像头与推理服务；模型缺失等失败 → 中文提示 + 退出
        GuardEngine? engine = null;
        CameraService? camera = null;
        FaceDetectionService? detector = null;
        FaceRecognitionService? recognizer = null;
        try
        {
            camera = new CameraService(Settings.DarkThreshold);
            detector = new FaceDetectionService();
            recognizer = new FaceRecognitionService();
            engine = new GuardEngine(camera, detector, recognizer);
        }
        catch (Exception ex)
        {
            // 逐个释放已创建成功的对象，避免泄漏
            camera?.Dispose();
            detector?.Dispose();
            recognizer?.Dispose();
            MessageBox.Show(
                "初始化守护引擎失败：\n" + ex.Message +
                "\n\n请运行 tools/download_models.py 下载模型文件后重试。",
                "启动失败", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
            return;
        }
        Engine = engine;

        // 4. 主人模板与引擎配置
        bool ownerLoaded = TemplateStore.TryLoad(out float[] ownerFeature);
        if (ownerLoaded)
        {
            engine.SetOwnerTemplate(ownerFeature);
        }
        engine.Configure(Settings);

        // 5. 遮罩窗口管理器（必须在 UI 线程创建）
        Masks = new MaskWindowManager(Settings.MonitorMode, Settings.SelectedMonitors);

        // 6. 遮罩动作事件（后台线程）→ UI 线程显示/隐藏遮罩（订阅方需幂等处理）
        engine.MaskActionRequested += action => Dispatcher.BeginInvoke(() =>
        {
            if (Masks is null)
            {
                return;
            }
            if (action == MaskAction.Show)
            {
                // 显示遮罩（模糊强度取自设置；>0 时先截屏高斯模糊，失败回退纯黑）
                Masks.ShowAll(Settings.BlurStrength);
            }
            else if (action == MaskAction.Hide)
            {
                Masks.HideAll();
            }
        });

        // 7. 状态事件（后台线程）→ 托盘图标与气泡（仅关键状态弹气泡）
        engine.StatusChanged += status => Dispatcher.BeginInvoke(() => OnEngineStatusForTray(status));

        // 8. 错误事件（后台线程）→ 托盘变红 + 中文前缀气泡
        engine.EngineError += (error, message) => Dispatcher.BeginInvoke(() => OnEngineErrorForTray(error, message));

        // 9. 托盘（向导流程与主窗口流程都需要，先创建）
        _tray = new TrayIconService();
        _tray.ShowMainWindowRequested += () => Dispatcher.BeginInvoke(ShowMainWindow);
        _tray.TogglePauseResumeRequested += () => Dispatcher.BeginInvoke(TogglePauseResume);
        _tray.ExitRequested += () => Dispatcher.BeginInvoke(ShutdownForExit);
        _tray.SetStatus(ownerLoaded && Settings.CameraEnabled ? TrayStatus.Running : TrayStatus.Paused);

        // 10. 未注册主人 → 先走向导；已注册 → 直接主窗口
        if (!ownerLoaded)
        {
            var wizard = new SetupWizardWindow { Owner = null };
            if (wizard.ShowDialog() != true)
            {
                // 用户取消注册 → 无法继续守护 → 退出
                ShutdownForExit();
                return;
            }
            // 注册成功：重新加载模板并装填引擎
            if (TemplateStore.TryLoad(out float[] feature))
            {
                engine.SetOwnerTemplate(feature);
            }
        }

        _mainWindow = new MainWindow();
        _mainWindow.Show();
        if (Settings.CameraEnabled)
        {
            engine.Start(); // StatusChanged 会把托盘同步为"守护中"
        }

        // 11. 全局热键：WM_HOTKEY 由主窗口的 HwndSource hook 转发（见 MainWindow.WndProcHook）
        _hotkeys = new HotkeyService();
        HotkeyService.HotkeyPressed += OnHotkeyPressed;
        if (!RegisterMainWindowHotkey())
        {
            _tray.ShowBubble("快捷键注册失败",
                $"热键 {Settings.HotkeyModifiers}+{Settings.HotkeyKey} 可能被占用，请在主窗口更换。",
                ToolTipIcon.Warning);
        }

        // 12. 开机启动：注册表与设置不一致时，以设置文件为准同步
        if (AutostartService.IsEnabled() != Settings.Autostart)
        {
            AutostartService.SetEnabled(Settings.Autostart);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // 释放顺序：热键退订 → 引擎（停采集、补发 Hide、释放推理服务）→ 遮罩窗口 → 托盘 → 热键 → 互斥体
        HotkeyService.HotkeyPressed -= OnHotkeyPressed;
        Engine?.Dispose();
        Masks?.Dispose();
        _tray?.Dispose();
        _hotkeys?.Dispose();

        if (_ownsMutex)
        {
            _singleInstanceMutex?.ReleaseMutex();
        }
        _singleInstanceMutex?.Dispose();

        base.OnExit(e);
    }

    // ==================== 对外辅助方法（主窗口等调用） ====================

    /// <summary>按当前设置为主窗口注册/重新注册全局热键；失败（如热键被占用）返回 false。</summary>
    public bool RegisterMainWindowHotkey()
    {
        if (_hotkeys is null || _mainWindow is null)
        {
            return false;
        }
        if (!HotkeyService.TryParse(Settings.HotkeyModifiers, Settings.HotkeyKey, out uint mods, out uint vk))
        {
            return false;
        }
        IntPtr hwnd = new WindowInteropHelper(_mainWindow).EnsureHandle();
        return _hotkeys.Register(hwnd, HotkeyId, mods, vk);
    }

    /// <summary>托盘气泡提示（Warning 图标，供主窗口等调用）。</summary>
    public void ShowTrayBubble(string title, string message)
    {
        _tray?.ShowBubble(title, message, ToolTipIcon.Warning);
    }

    /// <summary>主人模板重新注册成功后通知订阅者（主窗口刷新状态显示）。</summary>
    public void RaiseEngineRestarted()
    {
        EngineRestarted?.Invoke();
    }

    /// <summary>强制退出：放行主窗口 Closing 并关闭应用（托盘"退出"、向导取消共用）。</summary>
    public void ShutdownForExit()
    {
        _forceExit = true;
        Shutdown();
    }

    // ==================== 引擎事件 → 托盘 ====================

    /// <summary>按状态文本同步托盘图标；"触发遮罩"这一关键状态弹气泡。</summary>
    private void OnEngineStatusForTray(string status)
    {
        if (_tray is null)
        {
            return;
        }

        if (status.Contains("监控中") || status.Contains("遮罩"))
        {
            _tray.SetStatus(TrayStatus.Running);
            if (status.Contains("触发"))
            {
                // 关键状态：触发遮罩时提醒用户
                _tray.ShowBubble("检测到他人", "内容已隐藏", ToolTipIcon.Info);
            }
        }
        else if (status.Contains("已暂停") || status.Contains("守护已停止"))
        {
            _tray.SetStatus(TrayStatus.Paused);
        }
        // 其余（摄像头错误等文案）不改托盘状态，由 EngineError 处理为红色
    }

    /// <summary>引擎错误：托盘变红 + 按错误类型加中文前缀的气泡（TooDark 等节流由引擎保证）。</summary>
    private void OnEngineErrorForTray(CameraError error, string message)
    {
        if (_tray is null)
        {
            return;
        }

        _tray.SetStatus(TrayStatus.Error);
        string prefix = error switch
        {
            CameraError.NoCamera => "摄像头不可用",
            CameraError.Busy => "摄像头被占用",
            CameraError.Disconnected => "摄像头已断开",
            CameraError.TooDark => "光线过暗",
            CameraError.ModelMissing => "模型文件缺失",
            _ => "守护异常"
        };
        _tray.ShowBubble(prefix, message, ToolTipIcon.Warning);
    }

    // ==================== 全局热键 ====================

    private void OnHotkeyPressed(uint id)
    {
        if (id != HotkeyId)
        {
            return;
        }
        // 事件来自 UI 线程消息循环，仍统一封送，保证与暂停/恢复逻辑同线程执行
        Dispatcher.BeginInvoke(TogglePauseResume);
    }

    /// <summary>热键动作：运行中 → 暂停；已暂停/停止 → 恢复。托盘与主窗口由 StatusChanged 统一同步。</summary>
    private void TogglePauseResume()
    {
        if (Engine is null)
        {
            return;
        }
        if (Engine.IsRunning)
        {
            Engine.Pause();
        }
        else
        {
            Engine.Resume();
        }
    }

    // ==================== 托盘菜单 ====================

    private void ShowMainWindow()
    {
        if (_mainWindow is null)
        {
            return;
        }
        if (!_mainWindow.IsVisible)
        {
            _mainWindow.Show();
        }
        if (_mainWindow.WindowState == WindowState.Minimized)
        {
            _mainWindow.WindowState = WindowState.Normal;
        }
        _mainWindow.Activate();
    }

    // ==================== 全局异常兜底 ====================

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(
            "程序发生未处理的异常：\n" + e.Exception.Message + "\n\n应用将继续运行。",
            "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true; // 不崩溃，继续守护
    }
}
