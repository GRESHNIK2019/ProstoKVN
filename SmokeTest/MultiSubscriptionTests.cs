using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ProstoKVN;

public static class MultiSubscriptionTests
{
    static void Check(bool ok, string label)
    {
        if (!ok) throw new Exception("MULTI_SUBSCRIPTIONS_TEST_FAILED: " + label);
        Console.WriteLine("PASS " + label);
    }
    static ProxyNode Node(string name, string server)
        => new() { Name = name, Values = new()
        {
            ["name"] = name, ["type"] = "vless", ["server"] = server,
            ["port"] = 443, ["uuid"] = "00000000-0000-0000-0000-000000000002"
        } };

    public static async Task RunAsync()
    {
        const string legacyUrl = "https://unit.test/config/legacy-test-profile";
        const string secondUrl = "https://unit.test/config/cloud-mihomo";
        string store = Path.Combine(UserStore.Home, "subscriptions.json");
        UserStore.EnsureHome();
        if (File.Exists(store)) File.Delete(store);
        var settings = new AppSettings { ProtectedUrl = UserStore.EncryptUrl(legacyUrl) };
        var existing = SubscriptionProfileStore.Load(settings);
        Check(existing.Count == 1 && existing[0].Url == legacyUrl,
            "Existing DPAPI-protected 3x-ui URL migrates to first subscription profile");
        var second = new SubscriptionProfile {
            Name = "Backup profile", Format = "mihomo",
            ProtectedUrl = UserStore.EncryptUrl(secondUrl)
        };
        existing.Add(second);
        SubscriptionProfileStore.Save(existing);
        string raw = File.ReadAllText(store);
        Check(!raw.Contains(legacyUrl) && !raw.Contains(secondUrl)
            && !raw.Contains("secret-placeholder-token"),
            "Stored subscription list contains no plaintext credentials or URLs");
        var restored = SubscriptionProfileStore.Load(settings);
        Check(restored.Count == 2 && restored[0].Url == legacyUrl
            && restored[1].Url == secondUrl && restored[1].Format == "mihomo",
            "Multiple subscription profiles and formats survive restart");
        SubscriptionProfileStore.Save(new List<SubscriptionProfile>());
        Check(SubscriptionProfileStore.Load(settings).Count == 0,
            "Removing all profiles does not resurrect legacy 3x-ui URL");
        SubscriptionProfileStore.Save(restored);

        var firstNodes = new List<ProxyNode> { Node("Germany", "de.test") };
        var secondNodes = new List<ProxyNode> { Node("Germany", "ch.test") };
        Check(SubscriptionCache.Save(legacyUrl, firstNodes)
            && SubscriptionCache.Save(secondUrl, secondNodes),
            "Each subscription can be cached encrypted independently");
        bool firstRestored = SubscriptionCache.TryLoad(legacyUrl, out var first, out _);
        bool secondRestored = SubscriptionCache.TryLoad(secondUrl, out var secondCache, out _);
        Check(firstRestored && secondRestored
            && first[0].Name == "Germany" && secondCache[0].Name == "Germany"
            && first[0].Values["server"]?.ToString() == "de.test"
            && secondCache[0].Values["server"]?.ToString() == "ch.test",
            "Independent caches do not overwrite each other");
        var merged = SubscriptionProfileStore.Merge(new[]
        {
            (restored[0], (IReadOnlyList<ProxyNode>)first),
            (restored[1], (IReadOnlyList<ProxyNode>)secondCache)
        });
        Check(merged.Count == 2 && merged.All(x => x.Name.Contains(" · "))
            && merged.Select(x => x.Name).Distinct().Count() == 2,
            "Nodes merge with profile names and no name collisions");
        var deduplicated = SubscriptionProfileStore.Merge(new[]
        {
            (restored[0], (IReadOnlyList<ProxyNode>)first),
            (restored[1], (IReadOnlyList<ProxyNode>)first)
        });
        Check(deduplicated.Count == 1,
            "Identical nodes from different providers deduplicate");

        string links = "vless://00000000-0000-0000-0000-000000000001@edge.test:443?security=tls&sni=edge.test#Test%20VLESS";
        string base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(links));
        var standard = UniversalSubscriptionParser.Parse(base64);
        Check(standard.Format.Contains("Base64") && standard.Nodes.Count == 1
            && standard.Nodes[0].Values["type"]?.ToString() == "vless",
            "Auto-detects Base64/V2Ray share links");

        string mihomo = """
        mixed-port: 7890
        dns:
          enable: true
          nameserver: [1.2.3.4]
        rules:
          - MATCH,DIRECT
        proxies:
          - name: DE Reality
            type: vless
            server: de.example
            port: 443
            uuid: 00000000-0000-0000-0000-000000000001
            tls: true
            network: grpc
            grpc-opts:
              grpc-service-name: tunnel
            reality-opts:
              public-key: PUBLICKEYTEST
              short-id: "00"
          - name: HY2
            type: hysteria2
            server: hy.example
            port: 2087
            password: TEST-PASSWORD
            sni: hy.example
        """;
        var yaml = UniversalSubscriptionParser.Parse(mihomo);
        Check(yaml.Format == "Mihomo YAML" && yaml.Nodes.Count == 2
            && yaml.Nodes[0].Values.ContainsKey("reality-opts")
            && !yaml.Nodes[0].Values.ContainsKey("dns")
            && !yaml.Nodes[0].Values.ContainsKey("rules"),
            "Auto-detects Mihomo YAML and ignores foreign DNS and routing");
        Check(UniversalSubscriptionParser.Parse(mihomo, "mihomo").Nodes.Count == 2,
            "Explicit Mihomo format works");

        string singBox = """
        {
          "dns": {"servers": [{"tag":"evil","address":"1.2.3.4"}]},
          "route": {"final":"direct"},
          "outbounds": [
            {"type":"vless","tag":"Sing VLESS","server":"sg.example","server_port":443,
             "uuid":"00000000-0000-0000-0000-000000000001",
             "tls":{"enabled":true,"server_name":"sg.example",
                    "reality":{"enabled":true,"public_key":"testpub","short_id":"abcd"}},
             "transport":{"type":"ws","path":"/connect","headers":{"Host":"sg.example"}}},
            {"type":"hysteria2","tag":"Sing HY2","server":"hy.example","server_port":443,
             "password":"test-password","tls":{"enabled":true,"server_name":"hy.example"}},
            {"type":"direct","tag":"direct"}
          ]
        }
        """;
        var sing = UniversalSubscriptionParser.Parse(singBox);
        Check(sing.Format == "sing-box JSON" && sing.Nodes.Count == 2
            && sing.Nodes[0].Values.ContainsKey("reality-opts")
            && !sing.Nodes[0].Values.ContainsKey("dns")
            && !sing.Nodes[0].Values.ContainsKey("route"),
            "Auto-detects sing-box JSON, converts server fields, ignores DNS/routing");
        Check(EngineSelector.Choose(sing.Nodes[0]) == TunnelEngine.Mihomo,
            "REALITY WebSocket uses Mihomo rather than incomplete sing-box sidecar");
        Check(sing.Nodes[1].Values["sni"]?.ToString() == "hy.example"
            && SingBoxSidecar.BuildConfig(sing.Nodes[1])["outbounds"] != null,
            "Hysteria2 sing-box TLS/SNI converts to the sidecar configuration");

        string xray = """
        {
          "routing":{"domainStrategy":"IPIfNonMatch"},
          "outbounds":[
            {"tag":"proxy","protocol":"vless",
             "settings":{"vnext":[{"address":"xray.example","port":443,
               "users":[{"id":"00000000-0000-0000-0000-000000000001","encryption":"none"}]}]},
             "streamSettings":{"security":"reality","network":"tcp",
                "realitySettings":{"serverName":"www.example.com","publicKey":"testpub","shortId":"00"}}},
            {"tag":"direct","protocol":"freedom"}
          ]
        }
        """;
        var xr = UniversalSubscriptionParser.Parse(xray);
        Check(xr.Format == "Xray JSON" && xr.Nodes.Count == 1
            && xr.Nodes[0].Values["server"]?.ToString() == "xray.example"
            && xr.Nodes[0].NativeXrayConfig != null
            && !xr.Nodes[0].NativeXrayConfig!.Contains("domainStrategy")
            && !xr.Nodes[0].NativeXrayConfig!.Contains("routing"),
            "Auto-detects Xray vnext while stripping imported routing and DNS");
        var sanitized = XraySidecar.BuildConfig(xr.Nodes[0].NativeXrayConfig!);
        Check(sanitized["dns"] == null && sanitized["outbounds"] is System.Text.Json.Nodes.JsonArray a
            && a.Count == 1, "Xray native sidecar uses only one sanitized outbound");

        SubscriptionTransport.TestVpnAvailable = () => Task.FromResult(false);
        SubscriptionTransport.TestFetch = (vpn, url) => Task.FromResult(mihomo);
        try
        {
            var imported = await SubscriptionImporter.LoadAsync(secondUrl);
            Check(imported.Nodes.Count == 2 && imported.DetectedFormat == "Mihomo YAML",
                "Generic HTTPS URL imported by response body without 3x-ui paths");
        }
        finally
        {
            SubscriptionTransport.TestVpnAvailable = null;
            SubscriptionTransport.TestFetch = null;
        }
        bool rejected = false;
        try { UniversalSubscriptionParser.Parse("garbage input"); }
        catch (Exception) { rejected = true; }
        Check(rejected, "Invalid format rejected without changing settings");
        Console.WriteLine("ALL_MULTI_SUBSCRIPTION_TESTS_PASS");
    }
}
