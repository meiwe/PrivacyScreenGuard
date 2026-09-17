using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PrivacyScreenGuard.Services;

/// <summary>更新检查结果（HasUpdate=false 时 LatestVersion/ReleaseUrl 无意义）。</summary>
public sealed record UpdateCheckResult(bool HasUpdate, string LatestVersion, string ReleaseUrl);

/// <summary>
/// 更新检查服务：调用 GitHub Releases API 获取最新发布版本，与当前程序集版本比较。
/// - 使用系统代理（HttpClient 默认行为），适配本地代理环境
/// - API 无需鉴权；仓库为私密时返回 404 → CheckAsync 返回 null（静默视为无更新），开源后自动生效
/// - latest 端点天然排除 draft 与 prerelease
/// </summary>
public sealed class UpdateCheckService
{
    /// <summary>仓库（owner/repo）。</summary>
    public const string Repo = "meiwe/PrivacyScreenGuard";

    /// <summary>Release 下载页地址。</summary>
    public const string ReleasesUrl = $"https://github.com/{Repo}/releases/latest";

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient(new HttpClientHandler
        {
            UseProxy = true, // 默认读取系统代理设置，适配本地代理环境
        });
        client.Timeout = TimeSpan.FromSeconds(15);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("privacy-screen-guard-update-check");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    /// <summary>当前程序集版本（如 1.0.0）。</summary>
    public static Version CurrentVersion => System.Reflection.Assembly
        .GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);

    /// <summary>
    /// 检查最新发布版本。
    /// 返回 null 表示检查失败（无网络/仓库私密/接口异常）——调用方应静默处理。
    /// </summary>
    public async Task<UpdateCheckResult?> CheckAsync(CancellationToken token = default)
    {
        try
        {
            using var response = await Http.GetAsync(
                $"https://api.github.com/repos/{Repo}/releases/latest", token);
            if (!response.IsSuccessStatusCode)
            {
                return null; // 404（私密仓库）/ 限流 / 网络异常 → 静默
            }

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
            string tagName = doc.RootElement.GetProperty("tag_name").GetString() ?? "";
            string htmlUrl = doc.RootElement.GetProperty("html_url").GetString() ?? ReleasesUrl;

            // tag 形如 "v1.2.0" → 解析为 Version 比较
            if (!Version.TryParse(tagName.TrimStart('v', 'V'), out var latest))
            {
                return null;
            }

            return new UpdateCheckResult(latest > CurrentVersion, latest.ToString(), htmlUrl);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception)
        {
            return null; // 网络/解析异常一律静默
        }
    }
}
