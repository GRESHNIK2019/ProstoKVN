using System.Text.Json.Nodes;
using ProstoKVN;

public static class XrayRoutingTests
{
    static void Check(bool ok, string name)
    {
        if (!ok) throw new Exception(name);
        Console.WriteLine("PASS " + name);
    }
    public static void Run()
    {
        var custom = XraySidecar.BuildConfig("""
            {"outbounds":[{"tag":"direct","protocol":"freedom"},
              {"tag":"location-auto","protocol":"vless","settings":{"id":"retained"}}]}
            """);
        Check(custom["routing"]!["rules"]![0]!["outboundTag"]!.ToString() == "location-auto",
            "Custom smart-location outbound replaces nonexistent proxy");
        Check(custom["outbounds"]![1]!["settings"]!["id"]!.ToString() == "retained",
            "Native connection settings retained");
        var proxy = XraySidecar.BuildConfig("""
            {"outbounds":[{"tag":"proxy","protocol":"vless"}]}
            """);
        Check(proxy["routing"]!["rules"]![0]!["outboundTag"]!.ToString() == "proxy",
            "Existing proxy profiles remain supported");
        var balanced = XraySidecar.BuildConfig("""
            {"outbounds":[{"tag":"node-a","protocol":"vless"}],
             "routing":{"balancers":[{"tag":"auto","selector":["node-"]}],
             "rules":[{"type":"field","network":"tcp,udp","balancerTag":"auto"}]},
             "observatory":{"subjectSelector":["node-"]}}
            """);
        Check(balanced["routing"]!["rules"]![0]!["balancerTag"]!.ToString() == "auto"
            && balanced["routing"]!["balancers"]![0]!["tag"]!.ToString() == "auto"
            && balanced["observatory"] != null,
            "Native smart-location balancer and observatory retained");
        var untagged = XraySidecar.BuildConfig("""
            {"outbounds":[{"tag":"vega-egress","protocol":"freedom"},{"protocol":"vless"}]}
            """);
        Check(untagged["routing"]!["rules"]![0]!["outboundTag"]!.ToString() == "vega-egress-1",
            "Untagged VPN gets a unique route tag");
        Check(custom["inbounds"]![0]!["listen"]!.ToString() == "127.0.0.1",
            "SOCKS remains loopback-only");
        try
        {
            XraySidecar.BuildConfig("""{"outbounds":[{"protocol":"freedom"}]}""");
            throw new Exception("Missing VPN accepted");
        }
        catch (FormatException) { Console.WriteLine("PASS Missing VPN rejected before startup"); }
        Console.WriteLine("ALL_XRAY_ROUTING_TESTS_PASS");
    }
}
