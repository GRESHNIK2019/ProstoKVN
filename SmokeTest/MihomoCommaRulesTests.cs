using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ProstoKVN;

public static class MihomoCommaRulesTests
{
    private static void Check(bool ok, string label)
    {
        if (!ok) throw new Exception("MIHOMO_COMMA_TEST_FAILED: " + label);
        Console.WriteLine("PASS " + label);
    }

    public static async Task RunAsync()
    {
        const string name = "ProstoKVN • Netherlands, Amsterdam ++[B]";
        var node = new ProxyNode
        {
            Name = name,
            Values = new Dictionary<string, object?>
            {
                ["name"] = name, ["type"] = "socks5",
                ["server"] = "127.0.0.1", ["port"] = 18888, ["udp"] = true
            }
        };
        string? core = Environment.GetEnvironmentVariable("PROSTOKVN_MIHOMO_TEST_CORE");
        foreach (string mode in new[] { "smart", "vpn", "direct" })
        {
            var config = VpnCore.BuildConfig(new[] { node },
                new List<RouteRule>(),
                new AppSettings { Mode = mode, SelectedNode = name },
                useXraySidecar: true);
            var rules = (List<string>)config["rules"]!;
            var groups = (object[])config["proxy-groups"]!;
            var diagnostic = rules.Single(r => r.StartsWith("IN-NAME,vega-probe-node-0,"));
            var parts = diagnostic.Split(',');
            Check(parts.Length == 3 && !parts[2].Contains(',')
                && !diagnostic.Contains(name), "Diagnostic route uses comma-free alias in " + mode);
            var aliases = groups.OfType<Dictionary<string, object?>>().ToArray();
            Check(aliases.Any(x => x["name"]?.ToString() == parts[2]
                && ((string[])x["proxies"]!)[0] == name),
                "Alias resolves to original named proxy in " + mode);
            if (!string.IsNullOrEmpty(core) && File.Exists(core))
            {
                string home = Path.Combine(Path.GetTempPath(),
                    "prostokvn-mihomo-comma-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(home);
                string file = Path.Combine(home, "config.yaml");
                try
                {
                    File.WriteAllText(file, YamlWriter.Serialize(config));
                    using var process = Process.Start(new ProcessStartInfo(core)
                    {
                        ArgumentList = { "-t", "-f", file, "-d", home },
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardError = true,
                        RedirectStandardOutput = true
                    }) ?? throw new Exception("Unable to launch Mihomo");
                    var error = process.StandardError.ReadToEndAsync();
                    var output = process.StandardOutput.ReadToEndAsync();
                    await process.WaitForExitAsync();
                    await Task.WhenAll(error, output);
                    if (process.ExitCode != 0)
                        throw new Exception("Mihomo rejected " + mode + ": " +
                            (error.Result + output.Result)[..Math.Min(
                                350, (error.Result + output.Result).Length)]);
                    Check(true, "Real Mihomo validates comma-containing name in " + mode);
                }
                finally
                {
                    try { Directory.Delete(home, true); } catch { }
                }
            }
        }
        Console.WriteLine("ALL_MIHOMO_COMMA_TESTS_PASS");
    }
}
