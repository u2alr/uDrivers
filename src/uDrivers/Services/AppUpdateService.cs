using Velopack;
using Velopack.Sources;

namespace uDrivers.Services;

/// <summary>Self-updates uDrivers from GitHub Releases using Velopack.</summary>
public sealed class AppUpdateService
{
    private readonly UpdateManager _manager =
        new(new GithubSource(AppInfo.GitHubRepoUrl, accessToken: null, prerelease: false));

    private UpdateInfo? _pending;

    /// <summary>False when running from Visual Studio / an unpacked build.</summary>
    public bool IsInstalled => _manager.IsInstalled;

    /// <returns>The new version string if an update is available, otherwise null.</returns>
    public async Task<string?> CheckAsync()
    {
        if (!IsInstalled) return null;
        try
        {
            _pending = await _manager.CheckForUpdatesAsync();
            return _pending?.TargetFullRelease.Version.ToString();
        }
        catch (Exception ex)
        {
            Log.Error("App update check failed", ex);
            return null;
        }
    }

    public async Task DownloadAndRestartAsync(Action<int> progress)
    {
        if (_pending is null) return;
        await _manager.DownloadUpdatesAsync(_pending, progress);
        _manager.ApplyUpdatesAndRestart(_pending);
    }
}
