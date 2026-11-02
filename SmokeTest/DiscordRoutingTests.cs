using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ProstoKVN;

public static class DiscordRoutingTests
{
    private static void Check(bool ok, string label)
    {
        if (!ok) throw new Exception("DISCORD_ROUTING_FAILED: " + label);
        Console.WriteLine("PASS " + label);
    }

    public static void Run()
    {
        Check(DiscordRouting.IsDiscordProcess(@"C:\Users\Test\AppData\Discord\Discord.exe")
            && DiscordRouting.IsDiscordProcess("discordptb.EXE")
            && DiscordRouting.IsDiscordProcess("DiscordCanary.exe")
            && !DiscordRouting.IsDiscordProcess("NotDiscord.exe")
            && !DiscordRouting.IsDiscordProcess("chrome.exe"),
            "Discord desktop executables only");
        Check(DiscordRouting.IsDiscordDomain("media.discordapp.net")
            && DiscordRouting.IsDiscordDomain("discord.media")
            && !DiscordRouting.IsDiscordDomain("discord.com.example.org"),
            "WebRTC domain suffix matching without false positives");

        var node = new ProxyNode { Name = "TEST", Values = new()
        {
            ["name"] = "TEST", ["type"] = "socks5",
            ["server"] = "127.0.0.1", ["port"] = 19888
        }};
        var routeRules = new List<RouteRule>
        {
            new() { Domain = "discord.com", Route = "DIRECT", Manual = true },
            new() { Domain = "203.0.113.17", Route = "DIRECT", Manual = true },
            new() { Domain = "discord.media", Route = "DIRECT" }
        };
        var cfg = VpnCore.BuildConfig(new[] { node }, routeRules,
            new AppSettings { Mode = "smart", Tun = true, SelectedNode = "TEST" });
        var rules = (List<string>)cfg["rules"]!;
        var desktop = "PROCESS-NAME,Discord.exe,VEGA";
        Check(rules.IndexOf("DOMAIN-SUFFIX,discord.com,DIRECT") < rules.IndexOf(desktop)
            && rules.IndexOf("IP-CIDR,203.0.113.17/32,DIRECT,no-resolve") < rules.IndexOf(desktop),
            "Explicit manual domain and IP bypass rules take priority");
        Check(rules.IndexOf(desktop) >= 0
            && rules.IndexOf(desktop) < rules.IndexOf("DOMAIN-SUFFIX,discord.media,DIRECT")
            && rules.Contains("DOMAIN-SUFFIX,discord.media,VEGA")
            && rules.Contains("DOMAIN-SUFFIX,discordcdn.com,VEGA")
            && rules.IndexOf(desktop) < rules.IndexOf("MATCH,DIRECT"),
            "Desktop WebRTC bypasses AUTO and DIRECT fallback");
        Check(VpnCore.IsStaleConnection("203.0.113.88", new[] { "DIRECT" }, routeRules,
                "smart", "203.0.113.88", @"C:\Discord\Discord.exe")
            && !VpnCore.IsStaleConnection("203.0.113.17", new[] { "DIRECT" }, routeRules,
                "smart", "203.0.113.17", "Discord.exe")
            && !VpnCore.IsStaleConnection("203.0.113.88", new[] { "DIRECT" }, routeRules,
                "smart", "203.0.113.88", "chrome.exe")
            && !VpnCore.IsStaleConnection("203.0.113.88", new[] { "DIRECT" }, routeRules,
                "direct", "203.0.113.88", "Discord.exe"),
            "Only affected Discord sessions reconnect; manual overrides remain respected");
        var allVpn = (List<string>)VpnCore.BuildConfig(new[] { node }, new List<RouteRule>(),
            new AppSettings { Mode = "vpn", Tun = true, SelectedNode = "TEST" })["rules"]!;
        var direct = (List<string>)VpnCore.BuildConfig(new[] { node }, new List<RouteRule>(),
            new AppSettings { Mode = "direct", Tun = true, SelectedNode = "TEST" })["rules"]!;
        Check(allVpn.Contains(desktop) && !direct.Contains(desktop)
            && direct.Last() == "MATCH,DIRECT",
            "VPN mode enabled and explicit DIRECT mode preserved");
        using var learner = new SmartRouteLearner(false);
        Check(!learner.CanInspect("discord.com") && !learner.CanInspect("media.discordapp.net"),
            "HTTP auto-learning cannot misclassify Discord voice traffic");
        Console.WriteLine("ALL_DISCORD_ROUTING_TESTS_PASS");
    }
}
