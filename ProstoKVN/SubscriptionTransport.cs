using System;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.IO;
using System.Text;
using System.Threading.Tasks;

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
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent", "ProstoKVN/0.30.2");
        using var response = await (vpn ? ThroughVpn : Direct)
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
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
