using System.Net;
using System.Net.Http;
using System.Text;
using ProstoKVN;

public static class ConnectionVerificationTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(handler(request));
    }

    private static HttpClient Client(Func<HttpRequestMessage, HttpResponseMessage> f) =>
        new(new StubHandler(f)) { Timeout = TimeSpan.FromSeconds(1) };

    private static HttpResponseMessage Exit(string country, string ip) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                $"{{\"country\":\"{country}\",\"ip\":\"{ip}\"}}",
                Encoding.UTF8, "application/json")
        };

    private static HttpResponseMessage Http(int code) =>
        new((HttpStatusCode)code) { Content = new StringContent("") };

    private static void Check(bool value, string label)
    {
        if (!value) throw new Exception("Connection verification test failed: " + label);
        Console.WriteLine("PASS " + label);
    }

    public static async Task RunAsync()
    {
        const string directIp = "198.51.100.12";
        const string vpnIp = "203.0.113.13";
        using (var direct = Client(_ => Exit("RU", directIp)))
        using (var vpn = Client(_ => Exit("DE", vpnIp)))
        {
            var answer = await ConnectionVerifier.VerifyAsync(vpn, direct,
                () => Task.CompletedTask);
            Check(answer.Verified && answer.VpnCountry == "DE"
                && answer.DirectCountry == "RU",
                "Verified different exit IPs via DIRECT RU and VPN DE");
        }
        using (var direct = Client(_ => Exit("RU", directIp)))
        using (var vpn = Client(req =>
        {
            if (req.RequestUri!.Host.Contains("ipinfo.io"))
                return Http(403); // Egress-IP check blocked, tunnel still healthy.
            return Http(204);
        }))
        {
            var answer = await ConnectionVerifier.VerifyAsync(vpn, direct,
                () => Task.CompletedTask);
            Check(answer.Verified && answer.Detail.Contains("204"),
                "Fallback 204 is accepted despite HTTP 403 from IP-info service");
        }
        using (var direct = Client(_ => Exit("RU", directIp)))
        using (var vpn = Client(_ => Http(403)))
        {
            var answer = await ConnectionVerifier.VerifyAsync(vpn, direct,
                () => Task.CompletedTask);
            Check(!answer.Verified && answer.Detail.Contains("трёх"),
                "Three failed VPN-path probes remain unverified");
        }
        using (var direct = Client(_ => Exit("RU", directIp)))
        using (var vpn = Client(_ => Exit("RU", directIp)))
        {
            var answer = await ConnectionVerifier.VerifyAsync(vpn, direct,
                () => Task.CompletedTask);
            Check(!answer.Verified && answer.Detail.Contains("совпадает"),
                "Direct/VPN identical public IP is not a false success");
        }
        int attempts = 0;
        using (var direct = Client(_ => Exit("RU", directIp)))
        using (var vpn = Client(req =>
        {
            if (req.RequestUri!.Host.Contains("ipinfo.io"))
                return ++attempts == 1 ? Http(503) : Exit("DE", vpnIp);
            return Http(403);
        }))
        {
            var answer = await ConnectionVerifier.VerifyAsync(vpn, direct,
                () => Task.CompletedTask);
            Check(answer.Verified && attempts == 2,
                "Startup transient recovers automatically on second attempt");
        }
        Console.WriteLine("ALL_CONNECTION_VERIFICATION_TESTS_PASS");
    }
}
