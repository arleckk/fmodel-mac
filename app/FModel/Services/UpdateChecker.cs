using System.Globalization;
using System.Net.Http.Headers;
using Newtonsoft.Json.Linq;

namespace FModel.Services;

/// <summary>A file attached to a release (the zipped app).</summary>
public sealed record ReleaseAsset(string Name, string Url, long Size);

/// <summary>A published release of this port on GitHub.</summary>
public sealed record ReleaseInfo(string Tag, string Name, Version Version, string Notes, string PageUrl, DateTimeOffset Published,
    IReadOnlyList<ReleaseAsset> Assets)
{
    /// <summary>The zipped app for this Mac (what CI attaches as FModel-&lt;tag&gt;-macos-arm64.zip).</summary>
    public ReleaseAsset? Download => Assets.FirstOrDefault(a => a.Name.EndsWith("-macos-arm64.zip", StringComparison.OrdinalIgnoreCase))
                                     ?? Assets.FirstOrDefault(a => a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// FModel's update check, against this port's GitHub releases instead of FModel's API: at launch (unless silenced with
/// Remind Me, like FModel's NextUpdateCheck) and from Help › Releases. Only stable releases count; drafts and
/// pre-releases are left out.
/// </summary>
public static class UpdateChecker
{
    public const string ReleasesApi = "https://api.github.com/repos/arleckk/fmodel-mac/releases?per_page=30";

    /// <summary>The running version (the csproj's Version, as major.minor.patch).</summary>
    public static Version Current { get; } = typeof(UpdateChecker).Assembly.GetName().Version is { } v
        ? new Version(v.Major, v.Minor, Math.Max(0, v.Build))
        : new Version(0, 0, 0);

    /// <summary>FMODEL_NO_UPDATES=1 turns the launch check off (the snapshot harness and CI, which shouldn't go online).</summary>
    public static bool LaunchCheckDisabled => Environment.GetEnvironmentVariable("FMODEL_NO_UPDATES") == "1";

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"{AppSettings.UserAgentProduct}/{Current}");
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        return client;
    }

    /// <summary>The stable releases, newest first.</summary>
    public static async Task<IReadOnlyList<ReleaseInfo>> GetReleasesAsync(CancellationToken ct = default)
    {
        using var response = await Http.GetAsync(ReleasesApi, ct);
        Serilog.Log.Information("[GET] [{Status}({Code})] '{Url}'", response.ReasonPhrase, (int) response.StatusCode, ReleasesApi);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"GitHub answered {(int) response.StatusCode} {response.ReasonPhrase}");
        return Parse(await response.Content.ReadAsStringAsync(ct));
    }

    /// <summary>GitHub's release list (JSON) as stable releases with a version tag, newest first.</summary>
    public static IReadOnlyList<ReleaseInfo> Parse(string json)
    {
        var releases = new List<ReleaseInfo>();
        foreach (var item in JArray.Parse(json))
        {
            if (item.Value<bool>("draft") || item.Value<bool>("prerelease")) continue;
            var tag = item.Value<string>("tag_name") ?? "";
            if (ParseVersion(tag) is not { } version) continue;
            var assets = (item["assets"] as JArray ?? [])
                .Select(a => new ReleaseAsset(a.Value<string>("name") ?? "", a.Value<string>("browser_download_url") ?? "", a.Value<long>("size")))
                .Where(a => a.Url.Length > 0).ToArray();
            releases.Add(new ReleaseInfo(tag, item.Value<string>("name") is { Length: > 0 } name ? name : tag, version,
                (item.Value<string>("body") ?? "").Replace("\r\n", "\n").Trim(), item.Value<string>("html_url") ?? AppSettings.ProjectLink + "/releases",
                item["published_at"]?.ToObject<DateTimeOffset?>() ?? DateTimeOffset.MinValue, assets));
        }
        return releases.OrderByDescending(r => r.Version).ToArray();
    }

    /// <summary>"v1.2.3" or "1.2" as a version; null for anything else (pre-release tags like v1.0.0-beta included).</summary>
    public static Version? ParseVersion(string tag)
    {
        var text = tag.Trim();
        if (text.StartsWith('v') || text.StartsWith('V')) text = text[1..];
        if (!Version.TryParse(text, out var version) || version.Revision > 0) return null;
        return new Version(version.Major, version.Minor, Math.Max(0, version.Build));
    }

    /// <summary>The newest release above <paramref name="current"/>, if any.</summary>
    public static ReleaseInfo? Newer(IReadOnlyList<ReleaseInfo> releases, Version current) =>
        releases.Where(r => r.Version > current).MaxBy(r => r.Version);

    /// <summary>FModel's RemindMeCommand: when to check again ("Days", "Week", "Month", "Never"; anything else is now).</summary>
    public static DateTime RemindAt(string? when, DateTime now) => when switch
    {
        "Days" => now.AddDays(3),
        "Week" => now.Date.AddDays((DayOfWeek.Monday - now.DayOfWeek + 7) % 7 is var days && days == 0 ? 7 : days),
        "Month" => now.Date.AddDays(1 - now.Day).AddMonths(1),
        "Never" => DateTime.MaxValue,
        _ => now
    };

    /// <summary>FModel's "Next Refresh" label.</summary>
    public static string NextRefreshText(DateTime next) =>
        next == DateTime.MaxValue ? "Next Refresh: Never" : next <= DateTime.Now ? "Next Refresh: at launch" : $"Next Refresh: {next.ToString("MMM d, yyyy", CultureInfo.InvariantCulture)}";
}
