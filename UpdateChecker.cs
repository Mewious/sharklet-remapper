using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace TailwindRemapper;

public sealed record UpdateInfo(Version Version, string InstallerUrl, string ChecksumUrl);

// Asks GitHub for the newest Sharklet release and installs it when the user agrees.
internal static class UpdateChecker
{
    private const string LatestRelease = "https://api.github.com/repos/Mewious/sharklet-remapper/releases/latest";
    private static readonly HttpClient http = CreateClient();

    public static Version CurrentVersion
    {
        get
        {
            var version = typeof(UpdateChecker).Assembly.GetName().Version ?? new Version(0, 0, 0);
            return new Version(version.Major, version.Minor, Math.Max(version.Build, 0));
        }
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Sharklet", CurrentVersion.ToString()));
        return client;
    }

    // Returns null when Sharklet is up to date or GitHub can't be reached.
    public static async Task<UpdateInfo?> Check()
    {
        try
        {
            var release = JsonNode.Parse(await http.GetStringAsync(LatestRelease));
            var tag = release?["tag_name"]?.GetValue<string>().TrimStart('v') ?? "";
            if (!Version.TryParse(tag, out var latest) || latest <= CurrentVersion) return null;
            string? Asset(string suffix) => release!["assets"]?.AsArray()
                .Select(asset => asset?["browser_download_url"]?.GetValue<string>())
                .FirstOrDefault(url => url != null && url.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
            var installer = Asset(".exe");
            var checksum = Asset(".exe.sha256");
            return installer == null || checksum == null ? null : new UpdateInfo(latest, installer, checksum);
        }
        catch (Exception) { return null; }
    }

    // Downloads the setup, checks it against the release's SHA-256 file, then starts it.
    // Setup closes Sharklet itself before replacing it.
    public static async Task Install(UpdateInfo update)
    {
        var path = Path.Combine(Path.GetTempPath(), $"Sharklet-Setup-{update.Version}.exe");
        var expected = (await http.GetStringAsync(update.ChecksumUrl)).Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
        await File.WriteAllBytesAsync(path, await http.GetByteArrayAsync(update.InstallerUrl));
        var actual = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path)));
        if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(path);
            throw new InvalidDataException("The downloaded update didn't match its checksum, so it was not run. Try again later.");
        }
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }
}
