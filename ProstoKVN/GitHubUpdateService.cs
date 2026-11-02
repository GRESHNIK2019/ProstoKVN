using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ProstoKVN;

public sealed record GitHubUpdate(Version Version, string Tag, string ZipUrl,
    string Sha256, long DownloadSize, string ReleaseUrl);

public static class GitHubUpdateService
{
    public const string Repository = "GRESHNIK2019/ProstoKVN";
    public const string LatestUrl = "https://api.github.com/repos/" + Repository + "/releases/latest";
    private const long MaxZipSize = 220 * 1024 * 1024;
    private const long MaxExpandedSize = 600 * 1024 * 1024;
    private const int MaxEntries = 2500;
    private static readonly Regex TagPattern = new(@"^v\d+\.\d+(?:\.\d+){0,2}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex HashPattern = new(@"^[a-fA-F0-9]{64}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static Func<string, CancellationToken, Task<string>>? TestGetString { get; set; }
    public static Func<string, string, IProgress<int>?, CancellationToken, Task>? TestDownload { get; set; }

    private static HttpClient Client(bool forceVpn)
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            UseCookies = false,
            UseProxy = forceVpn,
            Proxy = forceVpn ? new WebProxy($"http://127.0.0.1:{VpnCore.VpnProbePort}") : null
        };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(35) };
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "ProstoKVN-Updater");
        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/vnd.github+json");
        return client;
    }

    private static async Task<T> TryTransport<T>(Func<HttpClient, Task<T>> action)
    {
        Exception? last = null;
        foreach (bool vpn in new[] { true, false })
        {
            using var client = Client(vpn);
            try { return await action(client); }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                last = ex;
                if (ex is HttpRequestException { StatusCode: not null })
                    throw;
            }
        }
        throw new InvalidOperationException("GitHub недоступен через VPN и напрямую.", last);
    }

    private static async Task<string> GetStringAsync(string url, CancellationToken ct)
    {
        if (TestGetString != null) return await TestGetString(url, ct);
        return await TryTransport(async client => await client.GetStringAsync(url, ct));
    }

    public static async Task<GitHubUpdate?> CheckAsync(Version current, CancellationToken ct = default)
    {
        string json;
        try { json = await GetStringAsync(LatestUrl, ct); }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null; // Repository exists, but no stable release yet.
        }
        using var document = JsonDocument.Parse(json);
        var release = document.RootElement;
        if (release.TryGetProperty("draft", out var draft) && draft.GetBoolean()) return null;
        if (release.TryGetProperty("prerelease", out var pre) && pre.GetBoolean()) return null;
        string tag = release.GetProperty("tag_name").GetString() ?? "";
        if (!TagPattern.IsMatch(tag) || !Version.TryParse(tag[1..], out var version))
            throw new InvalidOperationException("Некорректный тег GitHub Release.");
        if (version <= current) return null;
        string assetName = $"ProstoKVN-{tag}-win-x64.zip";
        var assets = release.GetProperty("assets").EnumerateArray().ToArray();
        var asset = assets.FirstOrDefault(a => a.TryGetProperty("name", out var n)
            && n.GetString() == assetName);
        if (asset.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException($"В релизе {tag} отсутствует архив {assetName}.");
        long size = asset.GetProperty("size").GetInt64();
        if (size <= 0 || size > MaxZipSize)
            throw new InvalidOperationException("Размер ZIP выходит за допустимые пределы.");
        string url = asset.GetProperty("browser_download_url").GetString() ?? "";
        ValidateAssetUrl(url, tag, assetName);
        string digest = asset.TryGetProperty("digest", out var hashValue)
            ? hashValue.GetString() ?? "" : "";
        string sha = digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
            ? digest["sha256:".Length..] : "";
        if (!HashPattern.IsMatch(sha))
        {
            string sumName = assetName + ".sha256";
            var sum = assets.FirstOrDefault(a => a.TryGetProperty("name", out var n)
                && n.GetString() == sumName);
            if (sum.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException("Релиз не содержит SHA-256 для проверки.");
            string checksumUrl = sum.GetProperty("browser_download_url").GetString() ?? "";
            ValidateAssetUrl(checksumUrl, tag, sumName);
            string text = await GetStringAsync(checksumUrl, ct);
            sha = text.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
            if (!HashPattern.IsMatch(sha))
                throw new InvalidOperationException("Некорректный контрольный SHA-256.");
        }
        string releaseUrl = release.TryGetProperty("html_url", out var html) ? html.GetString() ?? "" : "";
        return new(version, tag, url, sha.ToUpperInvariant(), size, releaseUrl);
    }

    private static void ValidateAssetUrl(string url, string tag, string name)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https"
            || !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            || uri.AbsolutePath != $"/{Repository}/releases/download/{tag}/{name}")
            throw new InvalidOperationException("GitHub вернул недоверенный адрес файла обновления.");
    }

    public static async Task<string> StageAsync(GitHubUpdate release,
        IProgress<int>? progress = null, CancellationToken ct = default)
    {
        string baseDir = Path.Combine(UserStore.Home, "updates");
        string dir = Path.Combine(baseDir, "stage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string zip = Path.Combine(dir, "release.zip");
        string payload = Path.Combine(dir, "payload");
        Directory.CreateDirectory(payload);
        try
        {
            if (TestDownload != null) await TestDownload(release.ZipUrl, zip, progress, ct);
            else await DownloadAsync(release.ZipUrl, zip, progress, ct);
            var zipInfo = new FileInfo(zip);
            if (zipInfo.Length == 0 || zipInfo.Length > MaxZipSize)
                throw new InvalidOperationException("Некорректный размер скачанного ZIP.");
            string actual;
            await using (var zipStream = File.OpenRead(zip))
                actual = Convert.ToHexString(await SHA256.HashDataAsync(zipStream, ct));
            if (!CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(actual), Convert.FromHexString(release.Sha256)))
                throw new InvalidOperationException("SHA-256 обновления не совпадает. Установка отменена.");
            ExtractValidated(zip, payload);
            // Hash every payload file separately to detect any changes between staging and installation.
            var hashes = Directory.GetFiles(payload, "*", SearchOption.AllDirectories)
                .ToDictionary(p => Path.GetRelativePath(payload, p).Replace('\\', '/'),
                    p => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))),
                    StringComparer.OrdinalIgnoreCase);
            File.WriteAllText(Path.Combine(dir, "hashes.json"), JsonSerializer.Serialize(hashes));
            return dir;
        }
        catch
        {
            // Delete only the unique staging folder created by this call.
            Directory.Delete(dir, true);
            throw;
        }
    }

    private static async Task DownloadAsync(string url, string destination,
        IProgress<int>? progress, CancellationToken ct)
    {
        await TryTransport(async client =>
        {
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            var length = response.Content.Headers.ContentLength;
            if (length > MaxZipSize) throw new InvalidOperationException("ZIP слишком большой.");
            await using var source = await response.Content.ReadAsStreamAsync(ct);
            await using var target = new FileStream(destination, FileMode.Create, FileAccess.Write,
                FileShare.None, 64 * 1024, true);
            byte[] buffer = new byte[64 * 1024];
            long total = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, ct)) > 0)
            {
                total += read;
                if (total > MaxZipSize) throw new InvalidOperationException("ZIP слишком большой.");
                await target.WriteAsync(buffer.AsMemory(0, read), ct);
                if (length is > 0) progress?.Report((int)(total * 100 / length.Value));
            }
            return true;
        });
    }

    public static void ExtractValidated(string zipPath, string destination)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        if (archive.Entries.Count == 0 || archive.Entries.Count > MaxEntries)
            throw new InvalidOperationException("ZIP содержит недопустимое количество файлов.");
        long size = 0;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string root = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (var entry in archive.Entries)
        {
            string normalized = entry.FullName.Replace('\\', '/');
            bool directory = normalized.EndsWith('/');
            if (string.IsNullOrWhiteSpace(normalized) || normalized.StartsWith('/')
                || normalized.Contains(':') || normalized.Contains('\0')
                || normalized.Split('/').Any(s => s is "." or ".." || s.Length == 0 && !directory))
                throw new InvalidOperationException("ZIP содержит опасный путь.");
            int unixMode = (entry.ExternalAttributes >> 16) & 0xF000;
            if (unixMode is 0xA000 or 0x6000 or 0x2000)
                throw new InvalidOperationException("Символьные ссылки в обновлениях запрещены.");
            string full = Path.GetFullPath(Path.Combine(destination, normalized.Replace('/', '\\')));
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("ZIP пытается выйти за пределы папки.");
            if (!names.Add(normalized.TrimEnd('/')))
                throw new InvalidOperationException("ZIP содержит дублирующийся путь.");
            size += entry.Length;
            if (entry.Length > 150 * 1024 * 1024 || size > MaxExpandedSize)
                throw new InvalidOperationException("Распакованное обновление слишком большое.");
            if (directory) { Directory.CreateDirectory(full); continue; }
            if (normalized.Equals("settings.json", StringComparison.OrdinalIgnoreCase)
                || normalized.Equals("routes.json", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("subscription-cache", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Релиз не должен содержать личные настройки.");
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            entry.ExtractToFile(full);
        }
        foreach (string required in new[] { "ProstoKVN.exe", "ProstoKVN.dll",
            "ProstoKVN.runtimeconfig.json", "updater/ProstoKVN.Updater.exe" })
            if (!File.Exists(Path.Combine(destination, required.Replace('/', '\\'))))
                throw new InvalidOperationException("Архив не содержит " + required);
    }
}
