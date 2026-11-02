using ProstoKVN;

public static class TransportTests
{
    public static void Run()
    {
        var node = new ProxyNode { Name = "TEST", Values = new() {
            ["name"] = "TEST", ["type"] = "socks5", ["server"] = "127.0.0.1", ["port"] = 18888 } };
        foreach (var mode in new[] { "smart", "vpn", "direct" })
        {
            var settings = new AppSettings { Mode = mode, Tun = true, SelectedNode = "TEST" };
            var manual = new List<RouteRule> { new() { Domain = "example.com", Route = "VEGA", Manual = true } };
            var config = VpnCore.BuildConfig(new[] { node }, manual, settings, useXraySidecar: true);
            var rules = (List<string>)config["rules"]!;
            foreach (var process in new[] { "xray.exe", "sing-box.exe", "mihomo.exe" })
            {
                int bypass = rules.IndexOf("PROCESS-NAME," + process + ",DIRECT");
                if (bypass < 0 || bypass >= rules.FindIndex(r => r.StartsWith("MATCH,")))
                    throw new Exception("Transport can loop: " + process + " in " + mode);
            }
            if (rules[1] != "IN-NAME,vega-probe-vpn,VEGA")
                throw new Exception("Diagnostic VPN listener bypassed");
            if (mode == "vpn" && rules.Last() != "MATCH,VEGA")
                throw new Exception("User all-VPN traffic no longer uses VPN");
            Console.WriteLine("PASS Outer transport bypasses TUN recursion in " + mode + "; diagnostic VPN and user fallback retained");
        }
        Console.WriteLine("ALL_TRANSPORT_TESTS_PASS");
    }
}
