using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace uDrivers.Services;

/// <summary>Shared HTTP client, file downloads with progress, and hashing.</summary>
public static class Web
{
    public static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(60) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("uDrivers", AppInfo.Version));
        return client;
    }

    public static string DownloadsFolder
    {
        get
        {
            var path = Path.Combine(AppInfo.DataFolder, "downloads");
            Directory.CreateDirectory(path);
            return path;
        }
    }

    public static async Task<string?> TryGetStringAsync(string url, CancellationToken ct)
    {
        try
        {
            using var resp = await Http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadAsStringAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            Log.Info($"GET {url} failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>Downloads to <paramref name="path"/>. Returns false on 404 or network failure.</summary>
    public static async Task<bool> TryDownloadAsync(string url, string path, CancellationToken ct, Action<int>? percent = null)
    {
        try
        {
            await DownloadAsync(url, path, ct, percent);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            Log.Info($"Download {url} failed: {ex.Message}");
            return false;
        }
    }

    public static async Task DownloadAsync(string url, string path, CancellationToken ct, Action<int>? percent = null, string? referer = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".part";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (referer is not null) request.Headers.Referrer = new Uri(referer);
        using var resp = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        var total = resp.Content.Headers.ContentLength ?? 0;

        await using (var src = await resp.Content.ReadAsStreamAsync(ct))
        await using (var dst = File.Create(temp))
        {
            var buffer = new byte[1 << 16];
            long read = 0;
            int lastPct = -1, n;
            while ((n = await src.ReadAsync(buffer, ct)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, n), ct);
                read += n;
                if (total > 0 && percent is not null)
                {
                    var pct = (int)(read * 100 / total);
                    if (pct != lastPct) { lastPct = pct; percent(pct); }
                }
            }
        }

        File.Move(temp, path, overwrite: true);
    }

    public static string Sha256(string path)
    {
        using var s = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(s));
    }

    public static string Md5(string path)
    {
        using var s = File.OpenRead(path);
        return Convert.ToHexString(MD5.HashData(s));
    }

    /// <summary>Checks a published hash (SHA-256 or MD5, picked by length). Returns null if no usable hash was given.</summary>
    public static bool? VerifyHash(string path, string? expected)
    {
        if (string.IsNullOrWhiteSpace(expected)) return null;
        expected = expected.Trim();
        return expected.Length switch
        {
            64 => string.Equals(Sha256(path), expected, StringComparison.OrdinalIgnoreCase),
            32 => string.Equals(Md5(path), expected, StringComparison.OrdinalIgnoreCase),
            _ => null,
        };
    }

    public static string SafeFileName(string url)
    {
        var name = Path.GetFileName(new Uri(url).LocalPath);
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name.Length == 0 ? "package.exe" : name;
    }
}
