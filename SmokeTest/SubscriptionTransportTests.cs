using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using ProstoKVN;

public static class SubscriptionTransportTests
{
    private static void Check(bool ok, string label)
    {
        if (!ok) throw new Exception("SUB_TRANSPORT_FAIL: " + label);
        Console.WriteLine("PASS " + label);
    }

    public static async Task RunAsync()
    {
        string url = "https://subscription.test.invalid/sub/hidden-token";
        var node = new ProxyNode {
            Name = "Test Hysteria",
            Values = new()
            {
                ["name"] = "Test Hysteria", ["type"] = "hysteria2",
                ["server"] = "test.invalid", ["port"] = 443,
                ["password"] = "SECRET_TEST_108492",
                ["ws-opts"] = new Dictionary<string,object?>
                {
                    ["path"] = "/web", ["headers"] = new Dictionary<string,object?> { ["Host"] = "test.invalid" }
                },
                ["alpn"] = new[] { "h3", "h2" }
            }
        };
        Check(SubscriptionCache.Save(url, new List<ProxyNode> { node }),
            "DPAPI encrypted cache write");
        string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(url.Trim())));
        string raw = File.ReadAllText(Path.Combine(UserStore.Home,
            "subscription-caches", hash + ".json"));
        Check(!raw.Contains("SECRET_TEST_108492") && !raw.Contains("hidden-token"),
            "No proxy credentials or subscription token in plaintext cache");
        Check(SubscriptionCache.TryLoad(url, out var cached, out _)
            && cached.Count == 1
            && cached[0].Name == "Test Hysteria"
            && cached[0].Values["ws-opts"] is Dictionary<string,object?>
            && cached[0].Values["alpn"] is object?[],
            "Cache roundtrip preserves nested transport options");
        Check(!SubscriptionCache.TryLoad(url + "different", out _, out _),
            "Cache cannot be reused for a different subscription URL");

        var routes = new List<bool>();
        SubscriptionTransport.TestVpnAvailable = () => Task.FromResult(true);
        SubscriptionTransport.TestFetch = (vpn, _) => {
            routes.Add(vpn);
            return vpn ? Task.FromResult("VPN OK") :
                Task.FromException<string>(new HttpRequestException("DIRECT should not be used"));
        };
        Check(await SubscriptionTransport.GetStringAsync(url) == "VPN OK"
            && routes.SequenceEqual(new[] {true}),
            "Active VPN-bound listener takes priority over broken TUN fake-IP route");

        routes.Clear();
        SubscriptionTransport.TestFetch = (vpn, _) => {
            routes.Add(vpn);
            return vpn ? Task.FromException<string>(new HttpRequestException("TLS failed"))
                : Task.FromResult("DIRECT OK");
        };
        Check(await SubscriptionTransport.GetStringAsync(url) == "DIRECT OK"
            && routes.SequenceEqual(new[] {true,false}),
            "Network errors can recover via direct route");

        routes.Clear();
        SubscriptionTransport.TestVpnAvailable = () => Task.FromResult(false);
        SubscriptionTransport.TestFetch = (vpn, _) => {
            routes.Add(vpn);
            return Task.FromException<string>(new HttpRequestException("TLS failed"));
        };
        bool actionable = false;
        try { await SubscriptionTransport.GetStringAsync(url); }
        catch (InvalidOperationException ex)
        {
            actionable = ex.Message.Contains("Включи VPN");
        }
        Check(actionable && routes.SequenceEqual(new[] {false}),
            "Without VPN listener, TLS failure gives useful non-secret error");

        routes.Clear();
        SubscriptionTransport.TestVpnAvailable = () => Task.FromResult(true);
        SubscriptionTransport.TestFetch = (vpn, _) => {
            routes.Add(vpn);
            return Task.FromException<string>(new HttpRequestException(
                "Access denied", null, System.Net.HttpStatusCode.Forbidden));
        };
        try { await SubscriptionTransport.GetStringAsync(url); }
        catch (HttpRequestException) { }
        Check(routes.SequenceEqual(new[] {true}),
            "HTTP 403 not bypassed or retried as a TLS failure");
        SubscriptionTransport.TestFetch = null;
        SubscriptionTransport.TestVpnAvailable = null;
        Console.WriteLine("ALL_SUBSCRIPTION_TRANSPORT_TESTS_PASS");
    }

    public static async Task SeedSavedCacheAsync()
    {
        string url = UserStore.DecryptUrl(UserStore.LoadSettings().ProtectedUrl);
        if (!SubscriptionImporter.IsValidUrl(url))
            throw new InvalidOperationException("No saved HTTPS subscription");
        var imported = await SubscriptionImporter.LoadAsync(url);
        Check(imported.Nodes.Count > 0
            && SubscriptionCache.Save(url, imported.Nodes),
            "Actual saved subscription securely cached without changing settings");
        Console.WriteLine("PROTECTED_CACHE_NODES=" + imported.Nodes.Count);
    }

    public static async Task LiveReadOnlyAsync()
    {
        // Only used as a separate explicit smoke run. Never print/decrypt
        // a subscription URL into diagnostics, and never modify settings.
        string url = UserStore.DecryptUrl(UserStore.LoadSettings().ProtectedUrl);
        if (!SubscriptionImporter.IsValidUrl(url))
            throw new InvalidOperationException("No saved HTTPS subscription");
        var imported = await SubscriptionImporter.LoadAsync(url);
        Check(imported.Nodes.Count > 0, "Live saved subscription returned usable nodes");
        Console.WriteLine("LIVE_SUBSCRIPTION_NODE_COUNT=" + imported.Nodes.Count);
    }
}
