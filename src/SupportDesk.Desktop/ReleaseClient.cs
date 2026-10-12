using System.Reflection;
using System.Text.RegularExpressions;
namespace SupportDesk.Desktop;

public sealed record ReleaseAsset(string Name, Uri Url, long Size, string? Sha256);
public sealed record AppRelease(Version Version, Uri Page, string Notes, ReleaseAsset? Package = null, ReleaseAsset? Checksums = null);

public static class ReleaseClient
{
    public const string Repository = "JHayz01/support-desk";
    public static Version Current => typeof(ReleaseClient).Assembly.GetName().Version!;
    public static string CurrentLabel => Current.ToString(3);
    public static bool IsNewer(Version version) => new Version(version.Major, version.Minor, Math.Max(0, version.Build), Math.Max(0, version.Revision)) > Current;

    public static bool IsAssetUrl(AppRelease release, ReleaseAsset asset) =>
        asset.Url.Scheme == "https" && asset.Url.Host == "github.com" && asset.Url.UserInfo.Length == 0 &&
        asset.Url.AbsolutePath == release.Page.AbsolutePath.Replace("/releases/tag/", "/releases/download/", StringComparison.Ordinal) + "/" + Uri.EscapeDataString(asset.Name);

    public static string NormalizeRepository(string value)
    {
        var repository = value.Trim().TrimEnd('/');
        if (repository.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase))
            repository = repository[19..];
        if (!Regex.IsMatch(repository, @"^[A-Za-z0-9][A-Za-z0-9-]{0,38}/[A-Za-z0-9_][A-Za-z0-9_.-]{0,99}$"))
            throw new ArgumentException("Укажите репозиторий в формате владелец/название или ссылку на GitHub.");
        return repository;
    }

    public static AppRelease? Parse(string repository, string json)
    {
        repository = NormalizeRepository(repository);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean()) return null;
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var version)) return null;
        // Use the known repository rather than opening an arbitrary URL from the response.
        var page = new Uri($"https://github.com/{repository}/releases/tag/{Uri.EscapeDataString(tag)}");
        var release = new AppRelease(version, page, root.TryGetProperty("body", out var notes) ? notes.GetString() ?? "" : "");
        if (root.TryGetProperty("assets", out var assets))
        {
            foreach (var asset in assets.EnumerateArray())
            {
                string name = asset.GetProperty("name").GetString() ?? "";
                if (name != $"support-desk-windows-v{version}.zip" && name != "SHA256SUMS.txt") continue;
                if (!Uri.TryCreate(asset.GetProperty("browser_download_url").GetString(), UriKind.Absolute, out var url)) continue;
                string? digest = asset.TryGetProperty("digest", out var hash) ? hash.GetString() : null;
                digest = digest is not null && Regex.IsMatch(digest, "^sha256:[a-fA-F0-9]{64}$") ? digest[7..] : null;
                var file = new ReleaseAsset(name, url, asset.GetProperty("size").GetInt64(), digest);
                if (!IsAssetUrl(release, file)) continue;
                release = name == "SHA256SUMS.txt" ? release with { Checksums = file } : release with { Package = file };
            }
        }
        return release;
    }

    public static async Task<AppRelease?> Check(string repository, CancellationToken cancellationToken = default)
    {
        repository = NormalizeRepository(repository);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15), MaxResponseContentBufferSize = 2 * 1024 * 1024 };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("CristalixSupport/" + CurrentLabel);
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        using var response = await http.GetAsync($"https://api.github.com/repos/{repository}/releases/latest", cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return Parse(repository, await response.Content.ReadAsStringAsync(cancellationToken));
    }
}
