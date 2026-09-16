using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using PrivacyScreenGuard.Services;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;

namespace PrivacyScreenGuard.Windows;

/// <summary>
/// 模型下载窗口：显示下载进度、失败原因，支持重试与取消。
/// 下载成功返回 DialogResult=true，用户取消或失败后关闭返回 false。
/// </summary>
public partial class ModelDownloadWindow : Window
{
    /// <summary>本次下载的取消令牌源（取消或关闭窗口时触发）。</summary>
    private CancellationTokenSource? _cts;

    /// <summary>下载是否已成功完成（决定 DialogResult 与关闭行为）。</summary>
    private bool _succeeded;

    /// <summary>上一次进度上报的字节数与时间，用于估算下载速度。</summary>
    private long _lastBytes;
    private DateTime _lastTick = DateTime.UtcNow;

    public ModelDownloadWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Closed += OnClosed;
        BtnCancel.Click += OnCancelClick;
        BtnRetry.Click += OnRetryClick;
    }

    /// <summary>窗口加载后立即开始下载。</summary>
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        StartDownload();
    }

    /// <summary>窗口关闭时取消进行中的下载，避免后台任务继续跑。</summary>
    private void OnClosed(object? sender, EventArgs e)
    {
        _cts?.Cancel();
    }

    /// <summary>启动（或重试）下载：重置界面后异步执行。</summary>
    private void StartDownload()
    {
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        _lastBytes = 0;
        _lastTick = DateTime.UtcNow;

        BtnRetry.Visibility = Visibility.Collapsed;
        BtnCancel.IsEnabled = true;
        BtnCancel.Content = "取消";
        DownloadProgress.Value = 0;
        TxtDetail.Foreground = (Brush)TryFindResource("Fg.Secondary") ?? Brushes.DimGray;
        TxtDetail.Text = "正在连接下载源…";
        TxtSpeed.Text = "";

        // 进度回调在后台线程触发，这里封送到 UI 线程刷新界面
        var progress = new Progress<(long Done, long Total)>(OnProgress);

        _ = Task.Run(async () =>
        {
            try
            {
                await ModelDownloadService.DownloadAllAsync(progress, _cts.Token);
                Dispatcher.BeginInvoke(new Action(OnDownloadSucceeded));
            }
            catch (OperationCanceledException)
            {
                // 用户主动取消：静默关闭（DialogResult=false）
                Dispatcher.BeginInvoke(new Action(() => Close()));
            }
            catch (Exception ex)
            {
                Dispatcher.BeginInvoke(new Action(() => OnDownloadFailed(ex.Message)));
            }
        });
    }

    /// <summary>刷新进度条、百分比与速度（UI 线程）。</summary>
    private void OnProgress((long Done, long Total) value)
    {
        long done = value.Done;
        long total = value.Total > 0 ? value.Total : 1;
        double percent = Math.Min(100.0, done * 100.0 / total);

        DownloadProgress.Value = percent;
        TxtDetail.Text = $"已下载 {FormatMb(done)} / 约 {FormatMb(total)}（{percent:0}%）";

        // 速度估算：按两次上报的时间差计算瞬时速率
        DateTime now = DateTime.UtcNow;
        double seconds = (now - _lastTick).TotalSeconds;
        if (seconds >= 0.5 && done > _lastBytes)
        {
            double mbPerSecond = (done - _lastBytes) / 1024.0 / 1024.0 / seconds;
            TxtSpeed.Text = $"速度约 {mbPerSecond:0.0} MB/s";
            _lastBytes = done;
            _lastTick = now;
        }
    }

    /// <summary>下载成功：提示后关闭窗口并返回成功。</summary>
    private void OnDownloadSucceeded()
    {
        _succeeded = true;
        DownloadProgress.Value = 100;
        TxtDetail.Foreground = (Brush)TryFindResource("StatusOK") ?? Brushes.SeaGreen;
        TxtDetail.Text = "模型已就绪";
        TxtSpeed.Text = "";
        DialogResult = true;
        Close();
    }

    /// <summary>下载失败：显示原因并给出重试入口（网络受限时可换镜像重试）。</summary>
    private void OnDownloadFailed(string message)
    {
        TxtDetail.Foreground = (Brush)TryFindResource("Danger") ?? Brushes.IndianRed;
        TxtDetail.Text = "下载失败：" + message;
        TxtSpeed.Text = "请检查网络连接后点击“重试”（会自动尝试其他下载源）";
        BtnRetry.Visibility = Visibility.Visible;
        BtnCancel.Content = "退出";
    }

    /// <summary>取消：中断下载并关闭窗口。</summary>
    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        if (_succeeded)
        {
            Close();
            return;
        }
        _cts?.Cancel();
        Close();
    }

    /// <summary>重试：重新开始下载流程。</summary>
    private void OnRetryClick(object sender, RoutedEventArgs e)
    {
        StartDownload();
    }

    /// <summary>字节数转 MB 文本。</summary>
    private static string FormatMb(long bytes) => $"{bytes / 1024.0 / 1024.0:0.0} MB";
}
