using Newtonsoft.Json;

namespace FModel.Services;

public sealed record OnlineBackupMeta(string? Version, long Entries);

public sealed record OnlineBackup(string FileName, string Url, long Size, OnlineBackupMeta? Meta)
{
    public string Display => Size > 0 ? $"{FileName} ({AssetPreview.FormatSize(Size)})" : FileName;
}

/// <summary>The pre-made backups FModel offers (its Dilly API), listed and downloaded on request.</summary>
public static class OnlineBackups
{
    public const string ListUrl = "https://export-service-new.dillyapis.com/v1/backups";

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        var version = typeof(OnlineBackups).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"{AppSettings.UserAgentProduct}/{version}");
        return client;
    }

    public static async Task<IReadOnlyList<OnlineBackup>> ListAsync(CancellationToken ct = default)
    {
        var json = await Http.GetStringAsync(ListUrl, ct);
        return (JsonConvert.DeserializeObject<OnlineBackup[]>(json) ?? [])
            .Where(b => !string.IsNullOrWhiteSpace(b.FileName) && Uri.TryCreate(b.Url, UriKind.Absolute, out _))
            .OrderBy(b => b.FileName, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>Downloads into the backups folder; a partial file is removed if it fails.</summary>
    public static async Task<string> DownloadAsync(OnlineBackup backup, string directory, CancellationToken ct = default)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, Path.GetFileName(backup.FileName));
        try
        {
            await using var source = await Http.GetStreamAsync(backup.Url, ct);
            await using var target = File.Create(path);
            await source.CopyToAsync(target, ct);
        }
        catch
        {
            if (File.Exists(path)) File.Delete(path);
            throw;
        }
        return path;
    }
}
