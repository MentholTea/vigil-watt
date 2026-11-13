using Velopack;
using Velopack.Sources;
using VigilWatt.Core.Utils;

namespace VigilWatt.App.Services;

/// <summary>
/// 功 能： 自动更新：包一层 Velopack 的 UpdateManager，GitHub 优先、连不上再回退 Gitee。
/// 2026/10/14 22:17:14 xliu
/// 两个仓库的 Release 资产必须一致（同一次 build-installer 的产物各传一份），见 docs/dev/build.md §8。
/// </summary>
public sealed class UpdateService
{
    /// <summary>
    /// 更新源。这是**fork 后的自有仓库**（MentholTea/vigil-watt），不是上游 evren-oct/vigil-watt：
    /// 指向上游会让本 fork 发布的版本对用户完全不可见（应用会按上游最新 tag 判定"已是最新"）。
    /// 上游仓库只作为代码来源，不再作为更新源。
    /// </summary>
    private const string GitHubRepoUrl = "https://github.com/MentholTea/vigil-watt";

    /// <summary>Gitee 镜像；本 fork 暂未在 Gitee 建仓，置空即只走 GitHub，不再白等 8 秒回退。</summary>
    private const string GiteeRepoUrl = "";

    // GitHub 被墙时表现为"连得上但不回包"，不设上限会一直等到 HttpClient 默认超时（100 秒），
    // 启动检查会白等；超过这个时间就直接当它不可用。
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(8);

    private readonly UpdateManager _github;

    /// <summary>回退源；未配置时为 null，<see cref="CheckAsync"/> 直接跳过，不会白等超时。</summary>
    private readonly UpdateManager? _gitee;

    // 检查成功后固定下来：delta 包必须从"给出清单的那个源"继续下载，中途换源会对不上
    private UpdateManager _active;

    public UpdateService()
    {
        // 匿名访问即可（公开仓库）；accessToken / downloader 传 null 用默认实现
        _github = new UpdateManager(new GithubSource(GitHubRepoUrl, null, false, null));
        _gitee = string.IsNullOrWhiteSpace(GiteeRepoUrl)
            ? null
            : new UpdateManager(new GiteaSource(GiteeRepoUrl, null, false, null));
        _active = _github;
    }

    public bool IsInstalled => _active.IsInstalled;

    public string? CurrentVersion => _active.CurrentVersion?.ToString();

    /// <summary>本次检查实际走通的源（GitHub / Gitee），供界面文案与日志显示。</summary>
    public string SourceName { get; private set; } = "GitHub";

    /// <summary>把原始异常翻译成用户能看懂的原因。Velopack 对 403 只抛 HTTP 状态码，界面照抄等于没说。</summary>
    private static string Describe(Exception ex)
    {
        string msg = ex.Message;
        if (msg.Contains("403") && msg.Contains("rate limit", StringComparison.OrdinalIgnoreCase))
            return "GitHub 接口调用次数已用尽（匿名每小时 60 次，代理/共享 IP 常见），请稍后再试";
        if (msg.Contains("403"))
            return "GitHub 拒绝了本次请求（403）";
        if (ex is TimeoutException)
            return msg;
        if (msg.Contains("No such host") || msg.Contains("actively refused") || msg.Contains("timed out"))
            return "连不上 GitHub，检查网络或代理";
        return msg;
    }

    /// <summary>检查更新。null = 已是最新；源不可达才抛异常，由调用方呈现。</summary>
    public async Task<UpdateInfo?> CheckAsync()
    {
        string githubError;
        try
        {
            var info = await WithTimeout(_github.CheckForUpdatesAsync()).ConfigureAwait(false);
            _active = _github;
            SourceName = "GitHub";
            return info;
        }
        catch (Exception ex)
        {
            githubError = Describe(ex);
            if (_gitee == null)
                throw new InvalidOperationException(githubError, ex);
            Log.Warn("GitHub 更新源不可用，回退 Gitee: " + ex.Message);
        }

        try
        {
            var info = await _gitee!.CheckForUpdatesAsync().ConfigureAwait(false);
            _active = _gitee;
            SourceName = "Gitee";
            return info;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"GitHub 不可达（{githubError}）；Gitee 也不可达（{Describe(ex)}）", ex);
        }
    }

    public Task DownloadAsync(UpdateInfo info, Action<int>? progress = null, CancellationToken cancel = default)
    {
        return _active.DownloadUpdatesAsync(info, progress, cancel);
    }

    /// <summary>应用更新并重启。Velopack 的流程是“Update.exe 等本进程退出 → 应用更新 → 重启”。</summary>
    public void ApplyAndRestart(UpdateInfo info)
    {
        _active.ApplyUpdatesAndRestart(info);
    }

    private static async Task<T> WithTimeout<T>(Task<T> task)
    {
        if (await Task.WhenAny(task, Task.Delay(ProbeTimeout)).ConfigureAwait(false) != task)
        {
            // 被放弃的任务还会继续跑，接住它后面的异常，免得变成 UnobservedTaskException
            _ = task.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            throw new TimeoutException($"{(int)ProbeTimeout.TotalSeconds} 秒内无响应");
        }

        return await task.ConfigureAwait(false);
    }
}
