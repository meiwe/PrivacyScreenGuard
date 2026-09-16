using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using OpenCvSharp;
using PrivacyScreenGuard.Models;
using PrivacyScreenGuard.Services;
// OpenCvSharp.Window 与 WPF Window 同名，显式消歧
using Window = System.Windows.Window;
using Brushes = System.Windows.Media.Brushes;

namespace PrivacyScreenGuard.Windows;

/// <summary>
/// 首次引导向导窗口：采集 3~5 张主人正脸特征，逐维平均并 L2 归一化后保存为加密模板。
/// 画面仅在本地处理，不保存、不上传任何图像帧；最终仅落盘 DPAPI 加密后的 128 维特征。
/// 线程模型：摄像头帧在后台采集线程回调，UI 更新一律经 Dispatcher 切回 UI 线程。
/// </summary>
public partial class SetupWizardWindow : Window
{
    /// <summary>完成注册所需的最少采集张数。</summary>
    private const int MinCaptures = 3;

    /// <summary>允许采集的最大张数。</summary>
    private const int MaxCaptures = 5;

    /// <summary>预览采集帧率（FPS）。</summary>
    private const double PreviewFps = 15;

    /// <summary>预览 UI 更新节流间隔（毫秒），约 15 FPS 的显示频率。</summary>
    private const int PreviewUiIntervalMs = 66;

    /// <summary>自动采集的最小间隔（毫秒）。</summary>
    private const int AutoCaptureIntervalMs = 900;

    /// <summary>预览转换失败提示的节流间隔（毫秒），避免 15FPS 下刷屏。</summary>
    private const int PreviewErrorIntervalMs = 5000;

    private CameraService? _camera;

    /// <summary>人脸检测服务；模型缺失时为 null（采集功能禁用，窗口不崩溃）。</summary>
    private FaceDetectionService? _detector;

    /// <summary>人脸特征服务；模型缺失时为 null（采集功能禁用，窗口不崩溃）。</summary>
    private FaceRecognitionService? _recognizer;

    /// <summary>已采集的特征队列（后台采集线程入队，UI 线程读取计数与清空）。</summary>
    private readonly ConcurrentQueue<float[]> _features = new();

    /// <summary>手动"采集一张"请求标志：1 表示有请求，采集线程用 Interlocked 消费。</summary>
    private int _captureRequested;

    /// <summary>上次预览 UI 更新时刻（后台线程经 Interlocked 读写）。</summary>
    private long _lastUiTick = long.MinValue / 2;

    /// <summary>上次成功采集时刻（后台线程经 Interlocked 读写，用于自动采集节流）。</summary>
    private long _lastCaptureTick = long.MinValue / 2;

    /// <summary>上次预览转换失败提示时刻（后台线程经 Interlocked 读写，用于 5 秒节流）。</summary>
    private long _lastPreviewErrorTick = long.MinValue / 2;

    /// <summary>是否已提示过"摄像头已出帧"（只提示一次，volatile 供后台线程读写）。</summary>
    private volatile bool _previewReadyReported;

    /// <summary>是否允许自动采集（UI 线程事件维护，后台线程只读）。</summary>
    private volatile bool _autoCaptureEnabled = true;

    /// <summary>Loaded 初始化是否已执行（防重复初始化）。</summary>
    private bool _initialized;

    /// <summary>预览叠加的人脸状态类别。</summary>
    private enum FaceStatusKind
    {
        /// <summary>人脸检测服务不可用（模型缺失）。</summary>
        Unavailable,

        /// <summary>未检测到人脸。</summary>
        NoFace,

        /// <summary>恰好检测到一张人脸。</summary>
        SingleFace,

        /// <summary>检测到多张人脸。</summary>
        MultiFace
    }

    /// <summary>摄像头下拉列表项：设备索引 + 显示名称。</summary>
    private sealed record DeviceItem(int Index, string Display);

    /// <summary>构造向导窗口：挂接全部事件（XAML 中不绑定事件处理器，便于 code-behind 独立维护）。</summary>
    public SetupWizardWindow()
    {
        InitializeComponent();

        Loaded += OnLoaded;
        Closed += OnClosed;
        DeviceComboBox.SelectionChanged += OnDeviceSelectionChanged;
        RefreshDevicesButton.Click += OnRefreshDevicesClick;
        CaptureOneButton.Click += OnCaptureOneClick;
        ResetCapturesButton.Click += OnResetCapturesClick;
        FinishButton.Click += OnFinishClick;
        CancelButton.Click += OnCancelClick;
        AutoCaptureCheckBox.Checked += OnAutoCaptureToggled;
        AutoCaptureCheckBox.Unchecked += OnAutoCaptureToggled;
    }

    /// <summary>
    /// 窗口加载：创建三个服务（模型缺失时构造抛异常 → 状态栏提示并禁用采集控件），
    /// 随后异步枚举摄像头并默认选中第一台启动预览。
    /// </summary>
    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_initialized) return;
        _initialized = true;

        // 创建人脸服务：模型缺失/损坏时构造会抛异常，逐个 try/catch 保证窗口不崩溃
        var errors = new List<string>();
        try
        {
            _detector = new FaceDetectionService();
        }
        catch (Exception ex)
        {
            errors.Add(ex.Message);
        }
        try
        {
            _recognizer = new FaceRecognitionService();
        }
        catch (Exception ex)
        {
            errors.Add(ex.Message);
        }
        if (errors.Count > 0)
        {
            DisableCaptureControls();
            FaceStatusText.Text = "人脸检测不可用";
            ShowStatus(string.Join(Environment.NewLine, errors)
                       + Environment.NewLine + "人脸采集功能已禁用，请补齐模型文件后重试。", isError: true);
        }

        // 创建摄像头服务（构造不会抛异常；打开失败通过 Error 事件上报）
        _camera = new CameraService();
        _camera.FrameCaptured += OnFrameCaptured;
        _camera.Error += OnCameraError;

        // 枚举设备会逐个短暂打开摄像头探测（耗时数百毫秒），放后台线程避免卡 UI
        List<(int Index, string Name)> devices;
        try
        {
            devices = await Task.Run(CameraService.EnumerateDevices);
        }
        catch (Exception ex)
        {
            ShowStatus($"枚举摄像头设备失败：{ex.Message}", isError: true);
            return;
        }

        var items = devices.Select(d => new DeviceItem(d.Index, d.Name)).ToList();
        DeviceComboBox.ItemsSource = items;
        RefreshDevicesButton.IsEnabled = true;

        if (items.Count == 0)
        {
            ShowStatus("未检测到可用摄像头，请连接摄像头后点击\"刷新\"。", isError: true);
            return;
        }

        // 默认选中第一台设备；SelectedIndex 变化会触发 SelectionChanged → 启动预览采集
        DeviceComboBox.SelectedIndex = 0;
        if (errors.Count == 0)
        {
            ShowStatus("初始化完成，请正对摄像头进行采集。");
        }
    }

    /// <summary>窗口关闭：先停采集并等待循环退出，再释放三个服务（避免后台线程访问已释放模型）。</summary>
    private void OnClosed(object? sender, EventArgs e)
    {
        if (_camera is not null)
        {
            _camera.Stop();
            _camera.Dispose();
            _camera = null;
        }
        _detector?.Dispose();
        _detector = null;
        _recognizer?.Dispose();
        _recognizer = null;
    }

    /// <summary>切换下拉框设备：先停旧采集循环，再以 15 FPS 启动新设备。</summary>
    private void OnDeviceSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_camera is null) return;
        if (DeviceComboBox.SelectedItem is not DeviceItem item) return;

        _camera.Stop();
        _camera.Start(item.Index, PreviewFps);
        ShowStatus($"已切换到摄像头：{item.Display}");
    }

    /// <summary>刷新按钮：重新枚举设备并回填下拉框（枚举期间禁用按钮防重复点击）。</summary>
    private async void OnRefreshDevicesClick(object sender, RoutedEventArgs e)
    {
        RefreshDevicesButton.IsEnabled = false;
        List<(int Index, string Name)> devices;
        try
        {
            devices = await Task.Run(CameraService.EnumerateDevices);
        }
        catch (Exception ex)
        {
            ShowStatus($"枚举摄像头设备失败：{ex.Message}", isError: true);
            return;
        }
        finally
        {
            // async 延续会回到 UI 线程，此处可安全操作控件
            RefreshDevicesButton.IsEnabled = true;
        }

        var items = devices.Select(d => new DeviceItem(d.Index, d.Name)).ToList();
        DeviceComboBox.ItemsSource = items;

        if (items.Count == 0)
        {
            ShowStatus("未检测到可用摄像头，请连接摄像头后重试。", isError: true);
            return;
        }

        DeviceComboBox.SelectedIndex = 0; // 触发 SelectionChanged → 启动采集
    }

    /// <summary>
    /// 帧回调（后台采集线程触发；帧所有权归本方法，用完立即释放）。
    /// 距上次 UI 更新 ≥66ms 时克隆一帧做预览；原帧继续做人脸检测与采集判定。
    /// </summary>
    private void OnFrameCaptured(Mat frame)
    {
        try
        {
            // 首帧到达时提示一次，便于用户区分"摄像头没打开"与"画面为黑帧"
            if (!_previewReadyReported)
            {
                _previewReadyReported = true;
                Dispatcher.BeginInvoke(() => ShowStatus("摄像头已就绪，画面已开始输出。"));
            }

            long now = Environment.TickCount64;
            bool isUiFrame = now - Interlocked.Read(ref _lastUiTick) >= PreviewUiIntervalMs;

            if (isUiFrame)
            {
                Interlocked.Exchange(ref _lastUiTick, now);

                // 预览修复说明：后台线程完成像素拷贝并构造【已冻结】的 BitmapSource。
                // 之前用 ToWriteableBitmap 在后台线程创建 WriteableBitmap——该对象
                // 未冻结、带创建线程亲和性，跨线程赋给 UI 的 Image.Source 会抛
                // InvalidOperationException，且被下方 catch 静默吞掉 → 预览黑屏。
                // Freeze 后的 BitmapSource 为只读、无线程亲和性，可安全跨线程显示。
                try
                {
                    var bitmap = CreateFrozenPreviewBitmap(frame);
                    Dispatcher.BeginInvoke(() => PreviewImage.Source = bitmap);
                }
                catch (Exception ex)
                {
                    // 预览转换失败不再静默：5 秒节流提示到状态栏
                    if (now - Interlocked.Read(ref _lastPreviewErrorTick) >= PreviewErrorIntervalMs)
                    {
                        Interlocked.Exchange(ref _lastPreviewErrorTick, now);
                        Dispatcher.BeginInvoke(() =>
                            ShowStatus($"预览画面转换失败：{ex.Message}", isError: true));
                    }
                }
            }

            // 原帧继续：人脸检测（采集循环单线程同步调用本回调，检测器无并发风险）
            FaceStatusKind statusKind;
            if (_detector is not null)
            {
                List<FaceInfo> faces = _detector.Detect(frame);
                if (faces.Count == 0)
                {
                    statusKind = FaceStatusKind.NoFace;
                }
                else if (faces.Count == 1)
                {
                    statusKind = FaceStatusKind.SingleFace;
                    TryCaptureFace(frame, faces[0], now);
                }
                else
                {
                    statusKind = FaceStatusKind.MultiFace;
                }
            }
            else
            {
                statusKind = FaceStatusKind.Unavailable;
            }

            if (isUiFrame)
            {
                Dispatcher.BeginInvoke(() => UpdateFaceStatus(statusKind));
            }
        }
        catch (ObjectDisposedException)
        {
            // 窗口关闭瞬间服务可能已被释放，忽略即可
        }
        catch (Exception)
        {
            // 单帧处理失败（如对齐/推理异常）不影响采集循环继续运行；
            // 模型损坏等问题已在服务构造阶段提前暴露并提示
        }
        finally
        {
            frame.Dispose();
        }
    }

    /// <summary>
    /// 把 BGR 帧转换为【已冻结】的 BitmapSource（可跨线程交给 UI 显示）。
    /// Freeze 后对象变为只读、无线程亲和性，这是 WPF 跨线程显示位图的标准做法。
    /// </summary>
    private static BitmapSource CreateFrozenPreviewBitmap(Mat frame)
    {
        int width = frame.Width;
        int height = frame.Height;
        int stride = (int)frame.Step(); // Mat 行字节数（8UC3 帧通常等于 width*3）
        var pixels = new byte[stride * height];
        Marshal.Copy(frame.Data, pixels, 0, pixels.Length);

        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr24, null, pixels, stride);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>
    /// 尝试从当前帧采集一张人脸特征（后台采集线程调用）：
    /// 消费手动请求标志，或满足"自动采集开启 + 距上次采集 ≥900ms"时，
    /// 对齐裁剪 → 提取 128 维特征 → 入队，并切回 UI 线程更新进度。
    /// </summary>
    private void TryCaptureFace(Mat frame, FaceInfo face, long now)
    {
        if (_recognizer is null) return;

        // 无论是否真的采集，先消费手动标志，避免残留到后续帧误触发
        bool manualRequested = Interlocked.Exchange(ref _captureRequested, 0) == 1;
        if (_features.Count >= MaxCaptures) return;

        bool autoEligible = !manualRequested
            && _autoCaptureEnabled
            && now - Interlocked.Read(ref _lastCaptureTick) >= AutoCaptureIntervalMs;
        if (!manualRequested && !autoEligible) return;

        try
        {
            // 对齐裁剪到 112×112 → 提取 128 维特征 → 入队
            using Mat aligned = _recognizer.AlignCrop(frame, face);
            float[] feature = _recognizer.ExtractFeature(aligned);
            _features.Enqueue(feature);
            Interlocked.Exchange(ref _lastCaptureTick, now);

            int count = _features.Count;
            Dispatcher.BeginInvoke(() => OnFeatureCaptured(count));
        }
        catch (Exception ex)
        {
            Dispatcher.BeginInvoke(() => ShowStatus($"采集失败：{ex.Message}", isError: true));
        }
    }

    /// <summary>采集成功后的 UI 更新（UI 线程）：进度、按钮可用性与状态栏提示。</summary>
    private void OnFeatureCaptured(int count)
    {
        ProgressText.Text = BuildProgressText(count);
        FinishButton.IsEnabled = count >= MinCaptures;
        ResetCapturesButton.IsEnabled = true;
        ShowStatus($"第 {count} 张采集成功（相似度校验将在后台完成）");
    }

    /// <summary>"采集一张"：置手动采集标志，由采集线程在下一帧单人正脸时消费。</summary>
    private void OnCaptureOneClick(object sender, RoutedEventArgs e)
    {
        Interlocked.Exchange(ref _captureRequested, 1);
    }

    /// <summary>"重新采集"：清空已采特征列表，进度归零。</summary>
    private void OnResetCapturesClick(object sender, RoutedEventArgs e)
    {
        while (_features.TryDequeue(out _))
        {
            // 逐个取出即视为释放（float[] 由 GC 回收）
        }
        ProgressText.Text = BuildProgressText(0);
        FinishButton.IsEnabled = false;
        ShowStatus("已清空已采集的人脸特征，请重新采集。");
    }

    /// <summary>
    /// "完成注册"：逐维平均所有特征 → L2 归一化 → 加密保存模板 → 关闭窗口并返回成功。
    /// </summary>
    private void OnFinishClick(object sender, RoutedEventArgs e)
    {
        if (_features.Count < MinCaptures)
        {
            ShowStatus($"采集数量不足，请至少采集 {MinCaptures} 张正脸。", isError: true);
            return;
        }

        try
        {
            float[] averaged = AverageAndNormalize(_features.ToArray());
            TemplateStore.Save(averaged);
        }
        catch (Exception ex)
        {
            ShowStatus($"保存主人特征失败：{ex.Message}", isError: true);
            return;
        }

        DialogResult = true;
        Close();
    }

    /// <summary>"取消"：关闭窗口并返回失败（不保存任何数据）。</summary>
    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    /// <summary>自动采集复选框切换：缓存勾选状态，供后台线程无锁读取。</summary>
    private void OnAutoCaptureToggled(object sender, RoutedEventArgs e)
    {
        _autoCaptureEnabled = AutoCaptureCheckBox.IsChecked == true;
    }

    /// <summary>摄像头错误回调（后台线程触发）：Disconnected 使用固定文案，其余直接显示中文消息。</summary>
    private void OnCameraError(CameraError error, string message)
    {
        string text = error == CameraError.Disconnected ? "摄像头已断开，正在重连…" : message;
        ShowStatus(text, isError: true);
    }

    /// <summary>更新预览叠加的人脸状态提示（UI 线程）。</summary>
    private void UpdateFaceStatus(FaceStatusKind kind)
    {
        switch (kind)
        {
            case FaceStatusKind.Unavailable:
                FaceStatusText.Text = "人脸检测不可用";
                FaceStatusText.Foreground = Brushes.LightGray;
                break;
            case FaceStatusKind.NoFace:
                FaceStatusText.Text = "未检测到人脸";
                FaceStatusText.Foreground = Brushes.LightGray;
                break;
            case FaceStatusKind.SingleFace:
                FaceStatusText.Text = "检测到人脸";
                FaceStatusText.Foreground = Brushes.LightGreen;
                break;
            default: // FaceStatusKind.MultiFace
                FaceStatusText.Text = "多人出镜，请单独采集";
                FaceStatusText.Foreground = Brushes.Orange;
                break;
        }
    }

    /// <summary>在 UI 线程更新底部状态栏（后台线程调用时自动切换到 UI 线程）。</summary>
    private void ShowStatus(string message, bool isError = false)
    {
        void Apply()
        {
            StatusText.Text = message;
            StatusText.Foreground = isError ? Brushes.IndianRed : Brushes.DimGray;
        }

        if (Dispatcher.CheckAccess())
        {
            Apply();
        }
        else
        {
            Dispatcher.BeginInvoke(Apply);
        }
    }

    /// <summary>模型缺失时禁用采集相关控件（预览不受影响，仍可查看画面）。</summary>
    private void DisableCaptureControls()
    {
        CaptureOneButton.IsEnabled = false;
        ResetCapturesButton.IsEnabled = false;
        FinishButton.IsEnabled = false;
        AutoCaptureCheckBox.IsEnabled = false;
    }

    /// <summary>生成采集进度文本。</summary>
    private static string BuildProgressText(int count)
    {
        return $"已采集 {count} / {MinCaptures}（最多 {MaxCaptures} 张）";
    }

    /// <summary>
    /// 逐维平均所有特征并做 L2 归一化（平均后模长过小时保持原值，理论上不会发生）。
    /// </summary>
    private static float[] AverageAndNormalize(IReadOnlyList<float[]> features)
    {
        int length = TemplateStore.FeatureLength;
        var avg = new float[length];
        foreach (float[] feature in features)
        {
            for (int i = 0; i < length; i++)
            {
                avg[i] += feature[i];
            }
        }
        for (int i = 0; i < length; i++)
        {
            avg[i] /= features.Count;
        }

        // L2 归一化：使最终模板模长为 1，便于后续余弦相似度比较
        double sumSq = 0;
        for (int i = 0; i < length; i++)
        {
            sumSq += (double)avg[i] * avg[i];
        }
        double norm = Math.Sqrt(sumSq);
        if (norm > 1e-12)
        {
            for (int i = 0; i < length; i++)
            {
                avg[i] = (float)(avg[i] / norm);
            }
        }
        return avg;
    }
}
