using System;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ProstoKVN;

/// <summary>
/// Verifies the selected VPN exit, NOT access to an arbitrary website.
/// A Cloudflare CAPTCHA or an HTTP 403 on a third-party landing page must
/// never be interpreted as a failed VPN connection.
/// </summary>
public sealed record ConnectionVerificationResult(
    bool Verified,
    string Detail,
    string VpnCountry = "",
    string DirectCountry = "");

public static class ConnectionVerifier
{
    private sealed record ExitResult(string Ip, string Country, string Error)
    {
        public bool Valid => !string.IsNullOrWhiteSpace(Ip) && Country.Length == 2;
    }

    private static HttpClient CreateClient(int port)
    {
        var handler = new HttpClientHandler
        {
            Proxy = new WebProxy($"http://127.0.0.1:{port}"),
            UseProxy = true,
            AllowAutoRedirect = false,
            UseCookies = false
        };
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(6) };
    }

    public static async Task<ConnectionVerificationResult> VerifyAsync()
    {
        using var vpn = CreateClient(VpnCore.VpnProbePort);
        using var direct = CreateClient(VpnCore.DirectProbePort);
        return await VerifyAsync(vpn, direct, () => Task.Delay(900));
    }

    /// <summary>
    /// Public injectable HTTP clients enable deterministic tests without
    /// starting any networking core or altering the live system VPN.
    /// </summary>
    public static async Task<ConnectionVerificationResult> VerifyAsync(
        HttpClient vpn, HttpClient direct, Func<Task>? pause)
    {
        string lastError = "";
        for (int attempt = 0; attempt < 3; attempt++)
        {
            // Independent proxy listeners are tied to VEGA and DIRECT by
            // Mihomo IN-NAME rules, regardless of Smart mode's MATCH fallback.
            var vpnExitTask = ReadExitAsync(vpn);
            var directExitTask = ReadExitAsync(direct);
            await Task.WhenAll(vpnExitTask, directExitTask);
            var vpnExit = vpnExitTask.Result;
            var directExit = directExitTask.Result;

            if (vpnExit.Valid)
            {
                // If both listeners expose the same public IP, we cannot
                // honestly tell the user that traffic uses the VPN.
                if (directExit.Valid &&
                    string.Equals(vpnExit.Ip, directExit.Ip,
                                  StringComparison.OrdinalIgnoreCase))
                    return new ConnectionVerificationResult(false,
                        "VPN-прокси отвечает, но его внешний IP совпадает с DIRECT. Проверь выбранный сервер и цепочку Mihomo.",
                        vpnExit.Country, directExit.Country);

                string description = "VPN-выход подтверждён";
                if (directExit.Valid)
                    description += $": DIRECT {directExit.Country} / VPN {vpnExit.Country}.";
                else
                    description += $": страна {vpnExit.Country}; сравнение с DIRECT недоступно.";

                return new ConnectionVerificationResult(true, description,
                    vpnExit.Country, directExit.Country);
            }

            // Connectivity fallback for services where ipinfo.io is blocked.
            // Only HTTP 204 on the VPN-bound listener is success. It cannot
            // confirm the country, which is reported explicitly.
            if (await GoogleConnectivityAsync(vpn))
            {
                return new ConnectionVerificationResult(true,
                    "VPN-прокси отвечает: HTTPS 204 через выбранный сервер. Страна выхода не определена.",
                    "", directExit.Country);
            }

            lastError = string.IsNullOrWhiteSpace(vpnExit.Error)
                ? "контрольные HTTPS-запросы не прошли"
                : vpnExit.Error;
            if (attempt < 2 && pause != null)
                await pause();
        }
        return new ConnectionVerificationResult(false,
            "Не удалось подтвердить выход через VPN после трёх попыток: " +
            lastError + ". Проверь сервер и подключение; отдельный сайт может быть недоступен.");
    }

    private static async Task<ExitResult> ReadExitAsync(HttpClient client)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(6));
            using var response = await client.GetAsync(
                "https://ipinfo.io/json", HttpCompletionOption.ResponseHeadersRead,
                cts.Token);
            if (!response.IsSuccessStatusCode)
                return new ExitResult("", "",
                    $"ipinfo.io вернул HTTP {(int)response.StatusCode}");
            string json = await response.Content.ReadAsStringAsync(cts.Token);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("country", out var countryValue)
                || !doc.RootElement.TryGetProperty("ip", out var ipValue))
                return new ExitResult("", "", "Сервис определения IP вернул неполный ответ");
            string country = countryValue.GetString()?.Trim().ToUpperInvariant() ?? "";
            string ip = ipValue.GetString()?.Trim() ?? "";
            if (country.Length != 2 || !IPAddress.TryParse(ip, out _))
                return new ExitResult("", "", "Сервис определения IP вернул некорректный ответ");
            return new ExitResult(ip, country, "");
        }
        catch (OperationCanceledException)
        {
            return new ExitResult("", "", "тайм-аут проверки VPN-выхода");
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or ArgumentException)
        {
            return new ExitResult("", "", "ошибка сети или ответа IP-сервиса");
        }
    }

    private static async Task<bool> GoogleConnectivityAsync(HttpClient client)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var response = await client.GetAsync(
                "https://www.google.com/generate_204",
                HttpCompletionOption.ResponseHeadersRead, cts.Token);
            return response.StatusCode == HttpStatusCode.NoContent;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            return false;
        }
    }
}
