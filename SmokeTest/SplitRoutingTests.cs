using System.Diagnostics;
using System.IO;
using ProstoKVN;

public static class SplitRoutingTests
{
    static void Check(bool ok, string message)
    {
        if (!ok) throw new Exception("SPLIT_TUN_FAILED: " + message);
        Console.WriteLine("PASS " + message);
    }

    public static void Run()
    {
        Check(!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PROSTOKVN_TEST_HOME")),
            "Tests use isolated settings");
        var node = new ProxyNode { Name = "TEST", Values = new() {
            ["name"] = "TEST", ["type"] = "socks5", ["server"] = "127.0.0.1", ["port"] = 18888
        }};
        var settings = new AppSettings { Mode = "apps", Tun = true, SelectedNode = "TEST" };
        var manual = new List<RouteRule> {
            new() { Domain = "process-name:chrome.exe", Route = "VEGA", Manual = true },
            new() { Domain = "process-name:Discord.exe", Route = "DIRECT", Manual = true },
            new() { Domain = "example.org", Route = "VEGA", Manual = true },
            new() { Domain = "openai.com", Route = "VEGA", Manual = false }
        };
        var config = VpnCore.BuildConfig(new[] { node }, manual, settings, useXraySidecar: true);
        var rules = (List<string>)config["rules"]!;
        Check(rules.Last() == "MATCH,DIRECT", "Non-selected traffic defaults to DIRECT");
        Check((bool)((Dictionary<string, object?>)config["tun"]!)["enable"]!,
            "TUN remains enabled so process rules include UDP");
        Check(rules.Contains("PROCESS-NAME,Discord.exe,VEGA") && rules.Contains("PROCESS-NAME,DiscordPTB.exe,VEGA"),
            "Discord voice/video uses the VPN process rule");
        Check(!rules.Any(rule => rule == "DOMAIN-SUFFIX,discord.com,VEGA"
            || rule == "DOMAIN-SUFFIX,openai.com,VEGA"
            || rule == "PROCESS-NAME,Telegram.exe,VEGA"),
            "No implicit Discord-browser, OpenAI or Telegram VPN routing");
        Check(rules.IndexOf("PROCESS-NAME,chrome.exe,VEGA") <
            rules.IndexOf("PROCESS-NAME,Discord.exe,VEGA"), "Manual application route precedes Discord defaults");
        Check(rules.IndexOf("PROCESS-NAME,Discord.exe,DIRECT") <
            rules.IndexOf("PROCESS-NAME,Discord.exe,VEGA"), "Manual Discord DIRECT override is honored");
        Check(rules.Contains("DOMAIN-SUFFIX,example.org,VEGA"),
            "Explicit site route is honored");
        var socks = ((Dictionary<string, object?>[])config["proxies"]!)[0];
        Check(Convert.ToString(socks["type"]) == "socks5" && socks["udp"] is true &&
            Convert.ToString(socks["server"]) == "127.0.0.1",
            "Xray sidecar keeps local UDP-capable SOCKS5 forwarding");
        Check(!VpnCore.IsStaleConnection("1.1.1.1", new[] { "DIRECT" }, manual,
            "apps", "1.1.1.1", "Telegram.exe"), "Telegram stays DIRECT in selected-app mode");
        var plan = HotRuleProviders.Create(new[] { node }, manual, settings);
        var hot = VpnCore.BuildConfig(new[] { node }, manual, settings,
            useXraySidecar: true, hotRules: true, preparedHotRules: plan);
        var hotRules = (List<string>)hot["rules"]!;
        Check(hotRules.Last() == "MATCH,DIRECT" &&
            hotRules.Contains("PROCESS-NAME,Discord.exe,VEGA") &&
            hotRules.Count(x => x.StartsWith("DOMAIN-SUFFIX,discord.")) == 0 &&
            plan.Files.Values.All(text => !text.Contains("openai.com")),
            "Hot rule providers preserve strict manual-plus-Discord routing");
        UserStore.EnsureHome();
        HotRuleProviders.Stage(plan);
        var configFile = Path.Combine(UserStore.Home, "split-test.yaml");
        File.WriteAllText(configFile, YamlWriter.Serialize(hot));
        using var check = Process.Start(new ProcessStartInfo(VpnCore.CoreExecutable) {
            ArgumentList = { "-t", "-f", configFile, "-d", UserStore.Home },
            CreateNoWindow = true, UseShellExecute = false,
            RedirectStandardError = true, RedirectStandardOutput = true
        }) ?? throw new Exception("Mihomo not started");
        var stdout = check.StandardOutput.ReadToEndAsync();
        var stderr = check.StandardError.ReadToEndAsync();
        if (!check.WaitForExit(15000)) { check.Kill(true); throw new Exception("Mihomo config validation timeout"); }
        Task.WaitAll(stdout, stderr);
        Check(check.ExitCode == 0, "Actual Mihomo accepts selected-app TUN and hot rules");
        Console.WriteLine("ALL_SPLIT_ROUTING_TESTS_PASS");
    }
}
