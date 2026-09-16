using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace PrivacyScreenGuard.Services;

/// <summary>
/// 模型下载服务：首次运行时从 OpenCV Zoo 下载 YuNet 与 SFace 模型（均为 Apache-2.0，可商用）。
///
/// 隐私说明：这是本应用【唯一】的联网行为，且仅在模型缺失、用户明确同意后发生；
/// 下载的是官方公开的模型文件，不上传、不发送任何本地数据（含人脸特征）。
/// 模型就绪后正常运行期间不再产生任何网络请求。
/// </summary>
public static class ModelDownloadService
{
    /// <summary>单个模型描述。</summary>
    /// <param name="FileName">下载后保存的文件名（放在 models 目录）。</param>
    /// <param name="RepoPath">opencv_zoo 仓库内的相对路径。</param>
    /// <param name="MinBytes">最小有效字节数，用于校验下载是否完整（防止残缺文件被当作模型）。</param>
    /// <param name="ApproxBytes">约略大小（用于界面显示与进度估算）。</param>
    public sealed record ModelSpec(string FileName, string RepoPath, long MinBytes, long ApproxBytes);

    /// <summary>需要下载的模型清单。</summary>
    public static readonly ModelSpec[] Models =
    {
        new(ModelLocator.YuNetFile,
            "models/face_detection_yunet/face_detection_yunet_2023mar.onnx",
            100_000, 233_000),
        new(ModelLocator.SFaceFile,
            "models/face_recognition_sface/face_recognition_sface_2021dec.onnx",
            10_000_000, 38_700_000),
    };

    /// <summary>下载源前缀（按顺序尝试；后两个为国内镜像，网络受限时通常可用）。</summary>
    private static readonly string[] Sources =
    {
        "https://raw.githubusercontent.com/opencv/opencv_zoo/main/",
        "https://github.com/opencv/opencv_zoo/raw/main/",
        "https://ghfast.top/https://github.com/opencv/opencv_zoo/raw/main/",
        "https://gh-proxy.com/https://github.com/opencv/opencv_zoo/raw/main/",
    };

    /// <summary>单个源的超时时间（下载大模型文件需要较长时间）。</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(10);

    /// <summary>全部模型的合计约略大小（字节），用于界面显示。</summary>
    public static long TotalApproxBytes
    {
        get
        {
            long total = 0;
            foreach (ModelSpec m in Models)
            {
                total += m.ApproxBytes;
            }
            return total;
        }
    }

    /// <summary>检查两个模型文件是否都已存在且大小合理。</summary>
    public static bool AllModelsPresent()
    {
        foreach (ModelSpec m in Models)
        {
            string? path = ModelLocator.FindModelFile(m.FileName);
            if (path is null || new FileInfo(path).Length < m.MinBytes)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// 模型存放目录：优先放在程序同目录下的 models（便于用户查看/手动替换），
    /// 该位置不可写时（如安装在 Program Files）退回 %LOCALAPPDATA%\PrivacyScreenGuard\models。
    /// </summary>
    public static string GetTargetDir()
    {
        string beside = Path.Combine(AppContext.BaseDirectory, "models");
        if (TryEnsureWritable(beside))
        {
            return beside;
        }

        string local = Path.Combine(SettingsService.GetSettingsDir(), "models");
        Directory.CreateDirectory(local);
        return local;
    }

    /// <summary>
    /// 下载全部模型（顺序下载，逐个尝试所有下载源）。
    /// </summary>
    /// <param name="progress">
    /// 进度回调：参数为已下载字节数与当前总字节数估算（用于界面百分比）。
    /// 在后台线程触发，订阅方需自行切换到 UI 线程。
    /// </param>
    /// <param name="cancellationToken">取消令牌（用户点击取消时触发）。</param>
    public static async Task DownloadAllAsync(IProgress<(long Done, long Total)> progress,
        CancellationToken cancellationToken)
    {
        string targetDir = GetTargetDir();
        long totalBytes = TotalApproxBytes;  // 估算总量，用于百分比
        long doneBytes = 0;

        foreach (ModelSpec spec in Models)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // 已存在且完整则跳过（支持中途取消后继续）
            string dest = Path.Combine(targetDir, spec.FileName);
            if (File.Exists(dest) && new FileInfo(dest).Length >= spec.MinBytes)
            {
                doneBytes += spec.ApproxBytes;
                progress.Report((doneBytes, totalBytes));
                continue;
            }

            long baseDone = doneBytes;
            await DownloadOneAsync(spec, dest, (bytesForThisFile) =>
            {
                // 把单文件进度换算为整体进度
                progress.Report((baseDone + bytesForThisFile, totalBytes));
            }, cancellationToken);

            doneBytes += spec.ApproxBytes;
            progress.Report((doneBytes, totalBytes));
        }
    }

    /// <summary>下载单个模型：按顺序尝试各下载源，全部失败才抛异常。</summary>
    private static async Task DownloadOneAsync(ModelSpec spec, string dest,
        Action<long> onBytes, CancellationToken cancellationToken)
    {
        Exception? lastError = null;

        foreach (string source in Sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await DownloadFromSourceAsync(source + spec.RepoPath, dest, spec.MinBytes, onBytes, cancellationToken);
                return;
            }
            catch (OperationCanceledException)
            {
                throw;  // 用户取消：不继续尝试其他源
            }
            catch (Exception ex)
            {
                lastError = ex;  // 该源失败，换下一个源
            }
        }

        throw new InvalidOperationException(
            $"模型 {spec.FileName} 下载失败（已尝试全部 {Sources.Length} 个下载源）。" +
            $"最后一个错误：{lastError?.Message}", lastError);
    }

    /// <summary>
    /// 从指定 URL 下载并保存到 dest：先写入 .tmp 临时文件，校验大小后原子改名，
    /// 避免半成品文件被当成完整模型使用。
    /// </summary>
    private static async Task DownloadFromSourceAsync(string url, string dest, long minBytes,
        Action<long> onBytes, CancellationToken cancellationToken)
    {
        string tempPath = dest + ".tmp";

        using var handler = new HttpClientHandler();  // 默认使用系统代理设置
        using var client = new HttpClient(handler) { Timeout = Timeout };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("PrivacyScreenGuard/1.0");

        using HttpResponseMessage response = await client.GetAsync(
            url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        long? contentLength = response.Content.Headers.ContentLength;
        long received = 0;

        await using (Stream remote = await response.Content.ReadAsStreamAsync(cancellationToken))
        await using (var file = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            byte[] buffer = new byte[64 * 1024];
            int read;
            long lastReported = 0;
            while ((read = await remote.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                received += read;

                // 每 256KB 上报一次，避免过于频繁地刷新界面
                if (received - lastReported >= 256 * 1024)
                {
                    lastReported = received;
                    onBytes(received);
                }
            }
        }

        // 完整性校验：文件过小说明下载失败或响应异常
        var info = new FileInfo(tempPath);
        if (info.Length < minBytes)
        {
            info.Delete();
            throw new IOException($"下载内容不完整（{info.Length} 字节，期望至少 {minBytes} 字节，" +
                                 $"服务器声明 {contentLength?.ToString() ?? "未知"} 字节）");
        }

        // 原子替换目标文件（覆盖已存在的残缺文件）
        File.Move(tempPath, dest, overwrite: true);
        onBytes(info.Length);
    }

    /// <summary>检测目录是否可写（不存在则尝试创建）；不可写返回 false。</summary>
    private static bool TryEnsureWritable(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            string probe = Path.Combine(dir, ".writable_probe");
            using (var fs = new FileStream(probe, FileMode.Create, FileAccess.Write, FileShare.None, 1,
                       FileOptions.DeleteOnClose))
            {
                // 能创建即视为可写（DeleteOnClose 自动清理，不留痕迹）
            }
            return true;
        }
        catch
        {
            return false;
        }
    }
}
