using System.Text.RegularExpressions;
using CUE4Parse.MappingsProvider;
using CUE4Parse.MappingsProvider.Jmap;
using CUE4Parse.MappingsProvider.Usmap;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FModelMac.Services;

public enum EndpointType { Aes, Mapping }

/// <summary>
/// FModel's endpoint configuration: a URL returning JSON and a JSONPath expression picking the values out of it.
/// AES: the main key, then optionally a list of { guid, key, name } dynamic keys.
/// Mapping: the mapping download URL, then optionally its file name.
/// </summary>
public sealed class EndpointConfig
{
    public string Url { get; set; } = "";
    public string Path { get; set; } = "";
    /// <summary>Set once the configuration was tested and returned usable values.</summary>
    public bool IsValid { get; set; }

    [JsonIgnore] public bool IsUsable => IsValid && !string.IsNullOrWhiteSpace(Url) && !string.IsNullOrWhiteSpace(Path);

    public EndpointConfig Clone() => new() { Url = Url, Path = Path, IsValid = IsValid };
}

public sealed record EndpointPreset(string Name, EndpointConfig Aes, EndpointConfig Mapping);

public sealed record DynamicKeyInfo(string Guid, string Key, string? Name);

public sealed record AesResult(string MainKey, IReadOnlyList<DynamicKeyInfo> DynamicKeys)
{
    // Same rule as FModel's AesResponse: a 256-bit main key, or at least dynamic keys
    public bool IsValid => MainKey.Length == 66 || DynamicKeys.Count > 0;
}

public sealed record MappingResult(string? Url, string? FileName)
{
    public bool IsValid => !string.IsNullOrEmpty(Url) && !string.IsNullOrEmpty(FileName);
}

public static class Endpoints
{
    /// <summary>FModel's defaults for the games it configures endpoints for.</summary>
    public static readonly EndpointPreset[] Presets =
    [
        new("Fortnite",
            new EndpointConfig { Url = "https://uedb.dev/svc/api/v1/fortnite/aes", Path = "$.['mainKey','dynamicKeys']", IsValid = true },
            new EndpointConfig { Url = "https://uedb.dev/svc/api/v1/fortnite/mappings", Path = "$.mappings.ZStandard", IsValid = true }),
        new("VALORANT",
            new EndpointConfig { Url = "https://uedb.dev/svc/api/v1/valorant/aes", Path = "$.['mainKey','dynamicKeys']", IsValid = true },
            new EndpointConfig { Url = "https://uedb.dev/svc/api/v1/valorant/mappings", Path = "$.mappings.ZStandard", IsValid = true })
    ];

    /// <summary>Downloaded mapping files, reused when the endpoint is unreachable.</summary>
    public static string MappingsDirectory => System.IO.Path.Combine(AppSettings.DataDirectory, "Mappings");

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        var version = typeof(Endpoints).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"FModelMac/{version}");
        return client;
    }

    /// <summary>The endpoint's JSON response ({} when the request fails, like FModel).</summary>
    public static async Task<JToken> GetBodyAsync(string url, CancellationToken ct = default)
    {
        using var response = await Http.GetAsync(url, ct);
        var content = await response.Content.ReadAsStringAsync(ct);
        Serilog.Log.Information("[GET] [{Status}({Code})] '{Url}'", response.ReasonPhrase, (int) response.StatusCode, url);
        if (!response.IsSuccessStatusCode || string.IsNullOrWhiteSpace(content)) return new JObject();
        try
        {
            return JToken.Parse(content);
        }
        catch (JsonReaderException)
        {
            return new JObject();
        }
    }

    public static AesResult ReadAes(JToken body, string path)
    {
        var tokens = body.SelectTokens(path).ToArray();
        var dynamicKeys = new List<DynamicKeyInfo>();
        if (tokens.ElementAtOrDefault(1) is JArray list)
        {
            foreach (var item in list)
            {
                if (item["guid"]?.ToString() is not { Length: > 0 } guid || FixKey(item["key"]?.ToString()) is not { Length: 66 } key) continue;
                dynamicKeys.Add(new DynamicKeyInfo(guid.Replace("-", ""), key, item["name"]?.ToString()));
            }
        }
        return new AesResult(FixKey(tokens.ElementAtOrDefault(0)?.ToString()), dynamicKeys);
    }

    public static MappingResult ReadMapping(JToken body, string path)
    {
        var tokens = body.SelectTokens(path).ToArray();
        var url = tokens.ElementAtOrDefault(0)?.ToString();
        var fileName = tokens.ElementAtOrDefault(1)?.ToString();
        if (string.IsNullOrEmpty(fileName) && !string.IsNullOrEmpty(url))
            fileName = System.IO.Path.GetFileName(new Uri(url, UriKind.RelativeOrAbsolute) is { IsAbsoluteUri: true } uri ? uri.AbsolutePath : url);
        return new MappingResult(url, fileName);
    }

    public static async Task<AesResult> GetAesAsync(EndpointConfig endpoint, CancellationToken ct = default) =>
        ReadAes(await GetBodyAsync(endpoint.Url, ct), endpoint.Path);

    /// <summary>
    /// The mapping file to use: downloaded from the endpoint (or reused from the cache when already there),
    /// falling back to the newest mapping downloaded before when the endpoint fails.
    /// </summary>
    public static async Task<(string? Path, string Source)> ResolveMappingAsync(EndpointConfig endpoint, bool force = false, CancellationToken ct = default)
    {
        // One folder per endpoint, so the fallback never picks another game's mapping
        var folder = System.IO.Path.Combine(MappingsDirectory, CacheFolder(endpoint.Url));
        Directory.CreateDirectory(folder);
        try
        {
            var mapping = ReadMapping(await GetBodyAsync(endpoint.Url, ct), endpoint.Path);
            if (mapping.IsValid)
            {
                var path = System.IO.Path.Combine(folder, SafeFileName(mapping.FileName!));
                if (force || !File.Exists(path) || new FileInfo(path).Length == 0)
                {
                    var data = await Http.GetByteArrayAsync(mapping.Url, ct);
                    await File.WriteAllBytesAsync(path, data, ct);
                    return (path, $"downloaded {mapping.FileName} ({AssetPreview.FormatSize(data.Length)})");
                }
                return (path, $"{mapping.FileName} (cached)");
            }
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            Serilog.Log.Warning(e, "Mapping endpoint {Url} failed", endpoint.Url);
        }

        var latest = new DirectoryInfo(folder).EnumerateFiles()
            .Where(f => f.Extension is ".usmap" || f.Name.EndsWith(".jmap", StringComparison.OrdinalIgnoreCase) || f.Name.EndsWith(".jmap.gz", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();
        return latest is null ? (null, "endpoint unavailable, no mapping cached") : (latest.FullName, $"endpoint unavailable, using the last download {latest.Name}");
    }

    /// <summary>.usmap, or Jmap (.jmap / .jmap.gz) like FModel.</summary>
    public static ITypeMappingsProvider OpenMappings(string path) =>
        path.EndsWith(".jmap", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".jmap.gz", StringComparison.OrdinalIgnoreCase)
            ? new JmapTypeMappingsProvider(path)
            : new FileUsmapTypeMappingsProvider(path);

    /// <summary>FModel's Helper.FixKey: upper-case hex with a 0x prefix, empty when it can't be a key.</summary>
    public static string FixKey(string? key)
    {
        var k = key?.Trim() ?? "";
        if (k.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) k = k[2..];
        if (k.Length == 0 || k.Length > 64 || !Regex.IsMatch(k, "^[0-9a-fA-F]+$")) return "";
        return "0x" + k.ToUpperInvariant();
    }

    private static string CacheFolder(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? SafeFileName($"{uri.Host}{uri.AbsolutePath}".Replace('/', '_')) : SafeFileName(url);

    private static string SafeFileName(string name) =>
        string.Concat(System.IO.Path.GetFileName(name).Select(c => System.IO.Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
}
