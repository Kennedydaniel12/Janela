using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace Janela;

sealed record UpdatePackage(string Path, Version Version);

static class Updater
{
    const string Repository = "Kennedydaniel12/Janela";
    const string Asset = "Janela-Setup-x64.exe";
    const long MaximumBytes = 150 * 1024 * 1024;
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(3) };

    public static string InstalledMarker => Path.Combine(AppContext.BaseDirectory, "update-enabled");
    public static Version InstalledVersion { get; } = Assembly.GetExecutingAssembly().GetName().Version!;
    static string DownloadFolder => Path.Combine(Program.DataPath, "updates");
    static string LastAttempt => Path.Combine(DownloadFolder, "last-attempt.txt");

    public static async Task<UpdatePackage?> Check(CancellationToken token)
    {
        if (!File.Exists(InstalledMarker)) return null;
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"https://api.github.com/repos/{Repository}/releases/latest");
        request.Headers.UserAgent.ParseAdd("Janela-Updater/1.0");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await Http.SendAsync(request, token);
        response.EnsureSuccessStatusCode();
        using var release = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(token));
        var root = release.RootElement;
        var tag = root.GetProperty("tag_name").GetString();
        if (root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean() ||
            tag is null || !tag.StartsWith('v') || !Version.TryParse(tag[1..], out var version) ||
            version.Revision >= 0 || version.Build < 0 ||
            new Version(version.Major, version.Minor, version.Build, 0) <= InstalledVersion) return null;
        if (File.Exists(LastAttempt) &&
            DateTime.UtcNow - File.GetLastWriteTimeUtc(LastAttempt) < TimeSpan.FromHours(6) &&
            File.ReadAllText(LastAttempt) == tag) return null;

        var asset = root.GetProperty("assets").EnumerateArray()
            .SingleOrDefault(item => item.GetProperty("name").GetString() == Asset);
        if (asset.ValueKind == JsonValueKind.Undefined) return null;
        if (!asset.TryGetProperty("digest", out var digestProperty)) return null;
        var digest = digestProperty.GetString();
        if (digest is null || digest.Length != 71 || !digest.StartsWith("sha256:", StringComparison.Ordinal)) return null;
        var expected = Convert.FromHexString(digest[7..]);
        var size = asset.GetProperty("size").GetInt64();
        if (size <= 0 || size > MaximumBytes) return null;

        Directory.CreateDirectory(DownloadFolder);
        var path = Path.Combine(DownloadFolder, $"Janela-Setup-{version}-{Guid.NewGuid():N}.exe");
        var url = $"https://github.com/{Repository}/releases/download/{Uri.EscapeDataString(tag)}/{Asset}";
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromMinutes(3));
        var download = deadline.Token;
        bool verified = false;
        try
        {
            using var package = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, download);
            package.EnsureSuccessStatusCode();
            await using var source = await package.Content.ReadAsStreamAsync(download);
            await using var target = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[81920];
            long total = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, download)) != 0)
            {
                total += read;
                if (total > size) return null;
                hash.AppendData(buffer, 0, read);
                await target.WriteAsync(buffer.AsMemory(0, read), download);
            }
            verified = total == size && CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), expected);
            return verified ? new UpdatePackage(path, version) : null;
        }
        finally { if (!verified) File.Delete(path); }
    }

    public static void CleanOldDownloads()
    {
        if (!Directory.Exists(DownloadFolder)) return;
        foreach (var file in Directory.EnumerateFiles(DownloadFolder, "Janela-Setup-*.exe"))
        {
            try { File.Delete(file); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    public static void Launch(UpdatePackage update)
    {
        File.WriteAllText(LastAttempt, "v" + update.Version);
        var start = new ProcessStartInfo(update.Path) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var flag in new[] { "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/SP-", "/JanelaUpdate=1" })
            start.ArgumentList.Add(flag);
        start.ArgumentList.Add("/DIR=" + Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory));
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Não foi possível iniciar a atualização.");
    }
}
