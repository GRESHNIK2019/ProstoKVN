using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using ProstoKVN;

public static class Version025Tests
{
    static void Check(bool good, string message)
    {
        if (!good) throw new Exception("V025_FAIL: " + message);
        Console.WriteLine("PASS " + message);
    }

    public static async Task RunAsync()
    {
        var policy = new RecoveryPolicy();
        var now = DateTime.UtcNow;
        Check(!policy.ShouldRecover(false, now), "Manual disconnect never recovers");
        policy.Enable();
        Check(!policy.ShouldRecover(false, now) && policy.ShouldRecover(false, now),
            "Recovery requires two consecutive unhealthy samples");
        policy.Failed(now);
        Check(!policy.ShouldRecover(false, now.AddSeconds(3))
            && policy.ShouldRecover(false, now.AddSeconds(5)),
            "Backoff prevents a tight restart loop");
        policy.Disable();
        Check(!policy.ShouldRecover(false, now.AddMinutes(1)), "Manual stop cancels recovery");

        var meter = new ConnectionMetrics();
        string sample1 = """
            {"connections":[
              {"id":"abc","metadata":{"network":"udp","process":"Discord.exe"},"upload":1000,"download":3000},
              {"id":"def","metadata":{"network":"udp","process":"chrome.exe"},"upload":800,"download":1400},
              {"id":"ghi","metadata":{"network":"tcp","process":"Discord.exe"},"upload":2000,"download":4000}
            ]}
            """;
        string sample2 = """
            {"connections":[
              {"id":"abc","metadata":{"network":"udp","process":"Discord.exe"},"upload":3000,"download":7000},
              {"id":"def","metadata":{"network":"udp","process":"chrome.exe"},"upload":1800,"download":3400},
              {"id":"ghi","metadata":{"network":"tcp","process":"Discord.exe"},"upload":8000,"download":9000}
            ]}
            """;
        var first = meter.Parse(sample1, now, null);
        var second = meter.Parse(sample2, now.AddSeconds(10), 85);
        Check(first.UdpConnections == 2 && first.DiscordUdpConnections == 1
            && first.UdpUpBytesPerSecond == 0
            && second.UdpUpBytesPerSecond == 300
            && second.UdpDownBytesPerSecond == 600 && second.HttpsLatencyMs == 85,
            "UDP rates use per-connection deltas; TCP excluded, latency labeled HTTPS");

        var node = new ProxyNode { Name = "TEST", Values = new() {
            ["name"] = "TEST", ["type"] = "socks5",
            ["server"] = "127.0.0.1", ["port"] = 18888, ["udp"] = true } };
        var rules = new List<RouteRule> {
            new() { Domain = "example.com", Route = "VEGA", Manual = true },
            new() { Domain = "sub.example.com", Route = "DIRECT", Manual = true },
            new() { Domain = "other.com", Route = "VEGA" },
            new() { Domain = "working.other.com", Route = "DIRECT" }
        };
        var settings = new AppSettings { Mode = "smart", Tun = false, SelectedNode = "TEST" };
        var plan = HotRuleProviders.Create(new[] { node }, rules, settings);
        Check(plan.ManualRuleCount > 0 && plan.Rules.Count > plan.ManualRuleCount,
            "Manual and AUTO priorities remain separate");
        Check(plan.Files.Values.Any(x => x.Contains("sub.example.com"))
            && plan.Files.Values.Any(x => x.Contains("working.other.com"))
            && plan.Files.Values.Any(x => x == "payload: []\n"),
            "Provider content covers specific exceptions and empty slots");
        HotRuleProviders.Stage(plan);
        var config = VpnCore.BuildConfig(new[] { node }, rules, settings, hotRules: true);
        var routeList = (List<string>)config["rules"]!;
        Check(routeList.FindIndex(r => r.StartsWith("RULE-SET,vega-manual")) <
            routeList.IndexOf("PROCESS-NAME,Discord.exe,VEGA")
            && routeList.IndexOf("PROCESS-NAME,Discord.exe,VEGA") <
            routeList.FindIndex(r => r.StartsWith("RULE-SET,vega-auto")),
            "Manual exceptions precede Discord and AUTO rules");
        config["secret"] = "test-secret";
        config["mixed-port"] = 18897;
        config["external-controller"] = "127.0.0.1:19997";
        foreach (var listener in (object[])config["listeners"]!)
        {
            var map = (Dictionary<string, object?>)listener;
            map["port"] = Convert.ToInt32(map["port"]) + 1200;
        }
        string yaml = Path.Combine(UserStore.Home, "v025-smoke.yaml");
        File.WriteAllText(yaml, YamlWriter.Serialize(config));
        var info = new ProcessStartInfo(VpnCore.CoreExecutable) {
            Arguments = $"-t -f \"{yaml}\" -d \"{UserStore.Home}\"",
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardError = true, RedirectStandardOutput = true
        };
        using (var check = Process.Start(info)!)
        {
            var err = await check.StandardError.ReadToEndAsync();
            var output = await check.StandardOutput.ReadToEndAsync();
            await check.WaitForExitAsync();
            Check(check.ExitCode == 0, "Mihomo accepts all file rule-providers: " +
                (err + output)[..Math.Min(200, (err + output).Length)]);
        }

        // Launch isolated core with TUN disabled and non-live API ports.
        using var isolated = new Process { StartInfo = new ProcessStartInfo(VpnCore.CoreExecutable) {
            Arguments = $"-f \"{yaml}\" -d \"{UserStore.Home}\"",
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardError = false, RedirectStandardOutput = false
        }};
        isolated.Start();
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "test-secret");
            bool alive = false;
            for (int i = 0; i < 12; i++)
            {
                await Task.Delay(300);
                if (isolated.HasExited) break;
                try {
                    using var response = await client.GetAsync("http://127.0.0.1:19997/providers/rules");
                    alive = response.IsSuccessStatusCode;
                    if (alive) break;
                } catch (HttpRequestException) { } catch (TaskCanceledException) { }
            }
            Check(alive, "Isolated Mihomo rule provider API is accessible");
            string? provider = plan.Files.FirstOrDefault(x => x.Value.Contains("other.com")
                && !x.Value.Contains("working.other.com")).Key;
            Check(provider != null, "Target rule provider identified");
            string file = Path.Combine(UserStore.Home, "hot-rules", provider + ".yaml");
            HotRuleProviders.WriteAtomic(file, HotRuleProviders.Render(new[] { "DOMAIN-SUFFIX,updated.test" }));
            using var refresh = await client.PutAsync("http://127.0.0.1:19997/providers/rules/" + provider, null);
            Check(refresh.IsSuccessStatusCode, "Rule provider updated through API without /configs reload");
            using var result = await client.GetAsync("http://127.0.0.1:19997/providers/rules");
            Check(result.IsSuccessStatusCode && !isolated.HasExited,
                "Isolated Mihomo kept running after rule provider update");
        }
        finally {
            if (!isolated.HasExited) { isolated.Kill(entireProcessTree: true); isolated.WaitForExit(3000); }
        }
        string? rehearsal = Environment.GetEnvironmentVariable("PROSTOKVN_V025_RULES_REHEARSAL");
        if (!string.IsNullOrWhiteSpace(rehearsal) && File.Exists(rehearsal))
        {
            var realRules = JsonSerializer.Deserialize<List<RouteRule>>(
                File.ReadAllText(rehearsal)) ?? new();
            var testNodes = new List<ProxyNode> { node };
            foreach (string preferred in realRules.Select(r => r.PreferredNode)
                .Where(x => !string.IsNullOrWhiteSpace(x)).Distinct())
            {
                testNodes.Add(new ProxyNode { Name = preferred, Values = new()
                { ["name"] = preferred, ["type"] = "socks5",
                  ["server"] = "127.0.0.1", ["port"] = 18888 } });
            }
            var actualPlan = HotRuleProviders.Create(testNodes, realRules, settings);
            Check(actualPlan.Files.Count >= 96,
                "Full current user rules fit hot provider capacity");
            HotRuleProviders.Stage(actualPlan);
            var actualConfig = VpnCore.BuildConfig(testNodes, realRules, settings, hotRules: true);
            actualConfig["secret"] = "test-secret";
            actualConfig["mixed-port"] = 18899;
            actualConfig["external-controller"] = "127.0.0.1:19999";
            foreach (var listener in (object[])actualConfig["listeners"]!)
                ((Dictionary<string, object?>)listener)["port"] =
                    Convert.ToInt32(((Dictionary<string, object?>)listener)["port"]) + 1300;
            string actualFile = Path.Combine(UserStore.Home, "v025-user-rules-rehearsal.yaml");
            File.WriteAllText(actualFile, YamlWriter.Serialize(actualConfig));
            using var validation = Process.Start(new ProcessStartInfo(VpnCore.CoreExecutable)
            {
                Arguments = $"-t -f \"{actualFile}\" -d \"{UserStore.Home}\"",
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardError = true, RedirectStandardOutput = true
            })!;
            string validationError = await validation.StandardError.ReadToEndAsync();
            string validationOutput = await validation.StandardOutput.ReadToEndAsync();
            await validation.WaitForExitAsync();
            Check(validation.ExitCode == 0,
                "Existing user rules accepted by isolated Mihomo: " +
                (validationError + validationOutput)[..Math.Min(130, (validationError + validationOutput).Length)]);
        }
        Console.WriteLine("ALL_V025_TESTS_PASS");
    }
}
