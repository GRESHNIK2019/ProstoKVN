using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using ProstoKVN;

public static class VlessLinkTests
{
    private static void Check(bool condition, string description)
    {
        if (!condition) throw new Exception("VLESS_TEST_FAILED: " + description);
        Console.WriteLine("PASS " + description);
    }

    private static async Task ValidateXrayAsync(ProxyNode node, string label)
    {
        string temp = Path.Combine(Path.GetTempPath(),
            "prostokvn-vless-test-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(temp, XraySidecar.BuildConfig(node.NativeXrayConfig!).ToJsonString());
            using var process = Process.Start(new ProcessStartInfo(XraySidecar.Executable)
            {
                ArgumentList = { "run", "-test", "-config", temp },
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardError = true, RedirectStandardOutput = true
            }) ?? throw new Exception("Cannot start Xray configuration test");
            var err = process.StandardError.ReadToEndAsync();
            var output = process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();
            await Task.WhenAll(err, output);
            Check(process.ExitCode == 0, label);
        }
        finally { try { File.Delete(temp); } catch { } }
    }

    public static async Task RunAsync()
    {
        const string uuid = "00000000-0000-0000-0000-000000000001";
        const string tls = "vless://" + uuid + "@edge.example:443?security=tls&type=ws&sni=front.example&host=ws.example&path=%2Fsocket%3Fed%3D2048&alpn=h2%2Chttp%2F1.1#TLS%20WS";
        Check(SubscriptionImporter.IsValidUrl(tls)
            && SubscriptionImporter.IsValidUrl("https://example.org/sub/token")
            && !SubscriptionImporter.IsValidUrl("vless://not-a-uuid@example.org:443")
            && !SubscriptionImporter.IsValidUrl("http://example.org/config"),
            "Valid VLESS and existing HTTPS accepted; malformed links rejected");
        var imported = await SubscriptionImporter.LoadAsync(tls);
        var node = imported.Nodes[0];
        Check(imported.Nodes.Count == 1 && imported.DetectedFormat == "VLESS"
            && node.Name == "TLS WS" && node.NativeXrayConfig != null,
            "Standalone VLESS link imported with original name and native Xray configuration");
        var xray = JsonNode.Parse(node.NativeXrayConfig!)!;
        var outbound = xray["outbounds"]![0]!;
        var stream = outbound["streamSettings"]!;
        Check(outbound["settings"]!["vnext"]![0]!["users"]![0]!["id"]!.ToString() == uuid
            && stream["network"]!.ToString() == "ws"
            && stream["tlsSettings"]!["serverName"]!.ToString() == "front.example"
            && stream["wsSettings"]!["path"]!.ToString() == "/socket?ed=2048"
            && stream["wsSettings"]!["headers"]!["Host"]!.ToString() == "ws.example"
            && ((JsonArray)stream["tlsSettings"]!["alpn"]!).Count == 2,
            "TLS, WebSocket, Host, path and ALPN faithfully mapped");
        Check(EngineSelector.Choose(node) == TunnelEngine.Xray,
            "Native VLESS uses Xray core");
        await ValidateXrayAsync(node, "Xray 26.10 validates VLESS TLS+WebSocket");
        var mixed = VpnCore.BuildConfig(new [] { node }, new List<RouteRule>(),
            new AppSettings { Mode = "smart", SelectedNode = node.Name }, useXraySidecar: true);
        var proxy = ((Dictionary<string, object?>[])mixed["proxies"]!)[0];
        Check(proxy["type"]?.ToString() == "socks5"
            && Convert.ToInt32(proxy["port"]) == XraySidecar.SocksPort
            && ((List<string>)mixed["rules"]!).Contains("PROCESS-NAME,xray.exe,DIRECT"),
            "Mihomo routing uses local Xray SOCKS without TUN recursion");

        const string reality = "vless://" + uuid
            + "@reality.example:443?security=reality&type=xhttp&sni=front.example"
            + "&fp=chrome&pbk=AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"
            + "&sid=deadbeef&spx=%2Fspider&path=%2Fconnect&mode=auto#Reality";
        var real = (await SubscriptionImporter.LoadAsync(reality)).Nodes[0];
        var realStream = JsonNode.Parse(real.NativeXrayConfig!)!["outbounds"]![0]!["streamSettings"]!;
        Check(realStream["security"]!.ToString() == "reality"
            && realStream["realitySettings"]!["publicKey"]!.ToString().Length == 43
            && realStream["realitySettings"]!["shortId"]!.ToString() == "deadbeef"
            && realStream["realitySettings"]!["spiderX"]!.ToString() == "/spider"
            && realStream["network"]!.ToString() == "xhttp"
            && realStream["xhttpSettings"]!["path"]!.ToString() == "/connect",
            "REALITY, XHTTP, short ID and spiderX preserved");
        Check(XraySidecar.BuildConfig(real.NativeXrayConfig!)["routing"] != null,
            "Generated Reality config is accepted by Xray sidecar sanitizer");
        await ValidateXrayAsync(real, "Xray 26.10 validates VLESS Reality+XHTTP");
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PROSTOKVN_TEST_HOME")))
        {
            var profile = new SubscriptionProfile
            {
                Name = "Direct VLESS",
                ProtectedUrl = UserStore.EncryptUrl(tls),
                Format = "auto", DetectedFormat = "VLESS"
            };
            SubscriptionProfileStore.Save(new[] { profile });
            var restored = SubscriptionProfileStore.Load(new AppSettings());
            Check(restored.Count == 1 && restored[0].Url == tls
                && !File.ReadAllText(Path.Combine(UserStore.Home, "subscriptions.json"))
                    .Contains(uuid, StringComparison.OrdinalIgnoreCase),
                "VLESS share URL is DPAPI-protected and survives profile reload");
            Check(SubscriptionCache.Save(tls, imported.Nodes)
                && SubscriptionCache.TryLoad(tls, out var fromCache, out _)
                && fromCache.Count == 1
                && !string.IsNullOrWhiteSpace(fromCache[0].NativeXrayConfig),
                "Native VLESS outbound survives encrypted offline cache reload");
        }
        bool rejected = false;
        try { await SubscriptionImporter.LoadAsync("vless://" + uuid
              + "@example.org:443?security=reality&sni=front.example"); }
        catch (Exception) { rejected = true; }
        Check(rejected, "Incomplete Reality link rejected before connection");
        Console.WriteLine("ALL_VLESS_LINK_TESTS_PASS");
    }
}
