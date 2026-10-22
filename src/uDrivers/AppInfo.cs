namespace uDrivers;

public static class AppInfo
{
    public const string Name = "uDrivers";

    // Releases published there are what installed copies update from.
    public const string GitHubRepoUrl = "https://github.com/u2alr/uDrivers";

    public static string Version =>
        typeof(AppInfo).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    public static string DataFolder
    {
        get
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Name);
            Directory.CreateDirectory(path);
            return path;
        }
    }
}
