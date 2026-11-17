using System;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using System.Net.Http.Headers;

namespace ProstoKVN;

/// <summary>
/// Only subscription downloads use the dedicated Mihomo VPN-bound local listener.
/// This is a routing fallback, NOT a TLS-certificate bypass.
/// </summary>
public static class SubscriptionTransport
{
    public const int MaxResponseBytes = 8 * 1024 * 1024;
    private static readonly HttpClient Direct = CreateDirect();
    private static readonly HttpClient ThroughVpn = CreateVpn();
    public static Func<bool, string, Task<string>>? TestFetch;
    public static Func<Task<bool>>? TestVpnAvailable;
    public static Func<bool, HttpRequestMessage, Task<HttpResponseMessage>>? TestSend;
    private static readonly ConcurrentDictionary<string, byte> HwidOrigins = new(StringComparer.OrdinalIgnoreCase);

    public static bool RequiresHardwareIdentity(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && HwidOrigins.ContainsKey(uri.GetLeftPart(UriPartial.Authority));

    private static bool HeaderTrue(HttpResponseHeaders headers, string name)
        => headers.TryGetValues(name, out var values)
            && values.Any(x => x.Equals("true", StringComparison.OrdinalIgnoreCase));

    private static HttpClient CreateDirect() => new(new HttpClientHandler
    {
        UseProxy = false,
        UseCookies = false,
        AllowAutoRedirect = false
    }) { Timeout = TimeSpan.FromSeconds(10) };

    private static HttpClient CreateVpn() => new(new HttpClientHandler
    {
        UseProxy = true,
        Proxy = new WebProxy($"http://127.0.0.1:{VpnCore.VpnProbePort}"),
        UseCookies = false,
        AllowAutoRedirect = false
    }) { Timeout = TimeSpan.FromSeconds(10) };

    private static async Task<bool> VpnAvailableAsync()
    {
        if (TestVpnAvailable is { } probe) return await probe();
        try
        {
            using var socket = new TcpClient();
            await socket.ConnectAsync(IPAddress.Loopback, VpnCore.VpnProbePort)
                .WaitAsync(TimeSpan.FromMilliseconds(400));
            return socket.Connected;
        }
        catch (Exception ex) when (ex is SocketException or TimeoutException
            or OperationCanceledException) { return false; }
    }

    private static async Task<string> FetchAsync(string url, bool vpn)
    {
        if (TestFetch is { } callback) return await callback(vpn, url);
        return await FetchHttpAsync(url, vpn, RequiresHardwareIdentity(url));
    }

    private static async Task<string> FetchHttpAsync(string url, bool vpn, bool includeDevice)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        var version = typeof(SubscriptionTransport).Assembly.GetName().Version;
        request.Headers.TryAddWithoutValidation("User-Agent", $"ProstoKVN/{version?.Major}.{version?.Minor}.{version?.Build}");
        request.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain;q=0.9, */*;q=0.1");
        if (includeDevice)
        {
            request.Headers.TryAddWithoutValidation("x-hwid", SubscriptionDeviceIdentity.Id);
            request.Headers.TryAddWithoutValidation("x-device-os", "Windows");
            request.Headers.TryAddWithoutValidation("x-ver-os", Environment.OSVersion.Version.ToString());
            request.Headers.TryAddWithoutValidation("x-device-model", "ProstoKVN Windows");
        }
        using var response = TestSend is { } send ? await send(vpn, request)
            : await (vpn ? ThroughVpn : Direct).SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        if (!includeDevice && (HeaderTrue(response.Headers, "x-hwid-not-supported")
            || HeaderTrue(response.Headers, "x-hwid-active")))
        {
            HwidOrigins.TryAdd(new Uri(url).GetLeftPart(UriPartial.Authority), 0);
            response.Dispose();
            return await FetchHttpAsync(url, vpn, includeDevice: true);
        }
        if (HeaderTrue(response.Headers, "x-hwid-max-devices-reached") || HeaderTrue(response.Headers, "x-hwid-limit"))
            throw new SubscriptionAccessException(SubscriptionAccessFailure.DeviceLimit);
        if (HeaderTrue(response.Headers, "x-hwid-not-supported"))
            throw new SubscriptionAccessException(SubscriptionAccessFailure.DeviceIdentity);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaxResponseBytes)
            throw new InvalidOperationException("Подписка слишком большая (более 8 МБ).");
        using var source = await response.Content.ReadAsStreamAsync();
        using var buffer = new MemoryStream();
        byte[] block = new byte[32 * 1024];
        int read;
        while ((read = await source.ReadAsync(block)) > 0)
        {
            if (buffer.Length + read > MaxResponseBytes)
                throw new InvalidOperationException("Подписка слишком большая (более 8 МБ).");
            buffer.Write(block, 0, read);
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    public static async Task<string> GetStringAsync(string url)
    {
        if (!SubscriptionImporter.IsValidUrl(url))
            throw new InvalidOperationException("Нужна корректная HTTPS-ссылка подписки.");
        bool vpn = await VpnAvailableAsync();
        try { return await FetchAsync(url, vpn); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // HTTP 4xx/5xx are server responses, not an invitation to bypass
            // authentication. Retry only network failures / failed TLS handshakes.
            if (ex is HttpRequestException { StatusCode: not null }) throw;
            if (!vpn) throw new InvalidOperationException(
                "HTTPS-подписка недоступна напрямую. Включи VPN либо используй сохранённые серверы.", ex);
            try { return await FetchAsync(url, false); }
            catch (Exception fallback) when (fallback is HttpRequestException or TaskCanceledException)
            {
                throw new InvalidOperationException(
                    "Не удалось получить подписку ни через VPN, ни напрямую. Проверь маршрутизацию и TLS.",
                    new AggregateException(ex, fallback));
            }
        }
    }
}
