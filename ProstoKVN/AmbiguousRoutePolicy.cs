using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace ProstoKVN;

/// <summary>
/// Conservative best-effort routing when anti-bot pages return HTTP 403 even
/// though the site works in a regular browser through the foreign VPN.
/// A proposed rule is NOT evidence of successful page loading.
/// </summary>
public static class AmbiguousRoutePolicy
{
    public static bool CanTryVpn(RouteComparison comparison, string directCountry, string vpnCountry)
    {
        if (!string.Equals(directCountry, "RU", StringComparison.OrdinalIgnoreCase))
            return false;
        if (vpnCountry.Length != 2 || vpnCountry.Equals("RU", StringComparison.OrdinalIgnoreCase))
            return false;
        if (comparison.Direct.State != ProbeState.Restricted)
            return false;
        if (comparison.Vpn.Usable || comparison.Vpn.HttpCode == null)
            return false;
        int code = comparison.Vpn.HttpCode.Value;
        // Never infer reachability from a network or TLS failure, rate limit
        // or upstream server outage. A 403 challenge on both paths is ambiguous.
        return code is 200 or 301 or 302 or 303 or 307 or 308 or 401 or 403 or 451;
    }

    public static bool ConfirmAmbiguous(RouteComparison first, RouteComparison second)
    {
        return first.Direct.State == ProbeState.Restricted
            && second.Direct.State == ProbeState.Restricted
            && !second.Vpn.Usable && second.Vpn.HttpCode.HasValue
            && second.Vpn.HttpCode.Value is not (429 or >= 500);
    }

    public static async Task<string> ReadCountryAsync(HttpClient client)
    {
        try
        {
            using var timeout = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(6));
            using var result = await client.GetAsync("https://ipinfo.io/json",
                HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!result.IsSuccessStatusCode) return "";
            using var doc = JsonDocument.Parse(await result.Content.ReadAsStringAsync(timeout.Token));
            if (!doc.RootElement.TryGetProperty("country", out var country))
                return "";
            string code = country.GetString()?.Trim().ToUpperInvariant() ?? "";
            return code.Length == 2 && char.IsLetter(code[0]) && char.IsLetter(code[1]) ? code : "";
        }
        catch { return ""; }
    }
}
