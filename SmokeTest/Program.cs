using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using ProstoKVN;
if (Environment.GetEnvironmentVariable("PROSTOKVN_MULTI_SUB_UI_TEST") == "1")
{
    MultiSubscriptionUiTests.Run();
    return;
}

if (Environment.GetEnvironmentVariable("PROSTOKVN_MULTI_SUB_TEST") == "1")
{
    await MultiSubscriptionTests.RunAsync();
    return;
}

if (Environment.GetEnvironmentVariable("PROSTOKVN_VERIFY_STATUS_TEST") == "1")
{
    ConnectionStatusPolicyTests.Run();
    return;
}

if (Environment.GetEnvironmentVariable("PROSTOKVN_REBRAND_TEST") == "1")
{
    RebrandingMigrationTests.Run();
    return;
}

if (Environment.GetEnvironmentVariable("PROSTOKVN_GITHUB_UPDATER_TEST") == "1")
{
    await GitHubUpdateTests.RunAsync();
    return;
}
if (Environment.GetEnvironmentVariable("PROSTOKVN_SEED_SAVED_CACHE") == "1")
{
    await SubscriptionTransportTests.SeedSavedCacheAsync();
    return;
}
if (Environment.GetEnvironmentVariable("PROSTOKVN_LIVE_SUB_READ_ONLY") == "1")
{
    await SubscriptionTransportTests.LiveReadOnlyAsync();
    return;
}
if (Environment.GetEnvironmentVariable("PROSTOKVN_SUB_TRANSPORT_TEST") == "1")
{
    await SubscriptionTransportTests.RunAsync();
    return;
}
if (Environment.GetEnvironmentVariable("PROSTOKVN_RADAR_LAYOUT_TEST") == "1")
{
    HeroRadarLayoutTests.Run();
    return;
}
if (Environment.GetEnvironmentVariable("PROSTOKVN_STARTUP_SUB_TEST") == "1")
{
    StartupSubscriptionTests.Run();
    return;
}
if (Environment.GetEnvironmentVariable("PROSTOKVN_SYMMETRIC_TABS_TEST") == "1")
{
    SymmetricTabsTests.Run();
    return;
}
if (Environment.GetEnvironmentVariable("PROSTOKVN_THEME_TEST") == "1")
{
    ThemeTests.Run();
    return;
}
if (Environment.GetEnvironmentVariable("PROSTOKVN_DESIGN_UI_TEST") == "1")
{
    DesignUiTests.Run();
    return;
}
if (Environment.GetEnvironmentVariable("PROSTOKVN_COMPACT_UI_TEST") == "1")
{
    CompactUiTests.Run();
    return;
}
if (Environment.GetEnvironmentVariable("PROSTOKVN_V025_TEST") == "1")
{
    await Version025Tests.RunAsync();
    return;
}
if (Environment.GetEnvironmentVariable("PROSTOKVN_DISCORD_TEST") == "1")
{
    DiscordRoutingTests.Run();
    return;
}
if (Environment.GetEnvironmentVariable("PROSTOKVN_TRANSPORT_TEST") == "1")
{
    TransportTests.Run();
    return;
}
if (Environment.GetEnvironmentVariable("PROSTOKVN_LEARN_TEST") == "1")
{
    await LearningTests.RunAsync();
    return;
}
if (Environment.GetEnvironmentVariable("PROSTOKVN_XRAY_ROUTE_TEST") == "1")
{
    XrayRoutingTests.Run();
    return;
}
if (Environment.GetEnvironmentVariable("PROSTOKVN_CONNECTION_VERIFY_TEST") == "1")
{
    await ConnectionVerificationTests.RunAsync();
    return;
}
if (Environment.GetEnvironmentVariable("PROSTOKVN_CONNECTION_LIVE_TEST") == "1")
{
    var result = await ConnectionVerifier.VerifyAsync();
    Console.WriteLine("LIVE_CHECK_VERIFIED=" + result.Verified);
    Console.WriteLine("LIVE_CHECK_VPN_COUNTRY=" + result.VpnCountry);
    Console.WriteLine("LIVE_CHECK_DIRECT_COUNTRY=" + result.DirectCountry);
    Console.WriteLine("LIVE_CHECK_DETAIL=" + result.Detail);
    if (!result.Verified) Environment.ExitCode = 4;
    return;
}
if (Environment.GetEnvironmentVariable("PROSTOKVN_AI_ROUTE_TEST") == "1")
{
    void CheckAi(bool yes, string name)
    {
        if (!yes) throw new Exception("AI_SERVICE_ROUTING_FAILED: " + name);
        Console.WriteLine("PASS " + name);
    }
    CheckAi(AiServiceRouting.Matches("auth.openai.com")
        && AiServiceRouting.Matches("ws.chatgpt.com")
        && AiServiceRouting.Matches("console.anthropic.com")
        && AiServiceRouting.Matches("bridge.claudeusercontent.com")
        && AiServiceRouting.Matches("sso.claude.com")
        && !AiServiceRouting.Matches("notopenai.com")
        && !AiServiceRouting.Matches("evilclaude.ai.example.com"),
        "Boundary-safe service identification includes OAuth, streaming, Claude desktop");
    var rules = new List<RouteRule>
    {
        new() { Domain = "openai.com", Route = "DIRECT", Manual = true },
        new() { Domain = "claude.com", Route = "REJECT", Manual = true },
        new() { Domain = "chatgpt.com", Route = "VEGA",
                Provisional = true, ExpiresUtc = DateTime.UtcNow.AddHours(1) },
        new() { Domain = "discord.com", Route = "VEGA" },
        new() { Domain = "vk.ru", Route = "DIRECT", Manual = true }
    };
    UserStore.EnsureHome();
    UserStore.SaveRules(rules);
    string original = File.ReadAllText(Path.Combine(UserStore.Home, "routes.json"));
    string backup = UserStore.BackupRoutesBeforeServicePresets();
    CheckAi(File.ReadAllText(backup) == original, "Existing saved rules backed up before service seed");
    int added = AiServiceRouting.EnsureDefaults(rules);
    CheckAi(added > 0 && AiServiceRouting.EnsureDefaults(rules) == 0,
        "Initial creation and idempotent subsequent start");
    CheckAi(rules.Count(r => r.Domain == "openai.com" && r.Manual && r.Route == "DIRECT") == 1
        && rules.Count(r => r.Domain == "claude.com" && r.Manual && r.Route == "REJECT") == 1
        && rules.Any(r => r.Domain == "chatgpt.com" && !r.Provisional && r.ExpiresUtc == null),
        "Manual overrides and existing provisional AI rules preserved/repaired");
    CheckAi(rules.Any(r => r.Domain == "claude.ai" && r.Route == "DIRECT" && !r.Manual)
        && rules.Any(r => r.Domain == "anthropic.com" && r.Route == "DIRECT" && !r.Manual),
        "Claude service profile initially prefers working DIRECT");
    CheckAi(rules.Any(r => r.Domain == "discord.com" && r.Route == "VEGA")
        && rules.Any(r => r.Domain == "vk.ru" && r.Route == "DIRECT"),
        "Non-AI rules retained");
    UserStore.SaveRules(rules);
    CheckAi(UserStore.LoadRules().Count == rules.Count,
        "Service route JSON persistence");
    using var learner = new SmartRouteLearner(false);
    int countBeforeProbe = rules.Count;
    await learner.InspectAsync("auth.openai.com", rules, true, force: true);
    CheckAi(rules.Count == countBeforeProbe, "OpenAI auth challenge cannot create duplicate service rules");
    var confirmedClaude = new List<RouteRule>
    {
        new() { Domain = "claude.ai", Route = "VEGA", Manual = false }
    };
    AiServiceRouting.EnsureDefaults(confirmedClaude);
    CheckAi(confirmedClaude.Any(r => r.Domain == "claude.ai"
        && r.Route == "VEGA"), "Confirmed Claude VPN is preserved on restart");

    var node = new ProxyNode { Name = "TEST", Values = new()
    {
        ["name"] = "TEST", ["type"] = "socks5",
        ["server"] = "127.0.0.1", ["port"] = 19888
    } };
    var smart = new AppSettings { Mode = "smart", SelectedNode = "TEST" };
    var built = VpnCore.BuildConfig(new[] { node }, rules, smart);
    var effective = (List<string>)built["rules"]!;
    CheckAi(effective.Contains("DOMAIN-SUFFIX,openai.com,DIRECT")
        && effective.IndexOf("DOMAIN-SUFFIX,openai.com,DIRECT")
            < effective.IndexOf("DOMAIN-SUFFIX,openai.com,VEGA")
        && effective.Contains("DOMAIN-SUFFIX,claude.com,REJECT"),
        "Manual DIRECT / REJECT precede preset routing");
    CheckAi(!effective.Contains("DOMAIN-SUFFIX,claude.ai,VEGA")
        && !effective.Contains("DOMAIN-SUFFIX,anthropic.com,VEGA")
        && effective.Contains("DOMAIN-SUFFIX,oaistatsig.com,VEGA")
        && effective.IndexOf("DOMAIN-SUFFIX,oaistatsig.com,VEGA")
            < effective.IndexOf("DOMAIN-SUFFIX,ru,DIRECT"),
        "OpenAI pre-routed; Claude stays DIRECT until probe confirms VPN");
    CheckAi(ServiceRuleGrouper.ServiceName("api.anthropic.com") == "Claude / Anthropic",
        "Claude auto rules grouped under one service");
    var freshConfig = VpnCore.BuildConfig(new[] { node }, new List<RouteRule>(), smart);
    CheckAi(((List<string>)freshConfig["rules"]!).Contains("DOMAIN-SUFFIX,openai.com,VEGA"),
        "Core fallback protects first request even before saving presets");
    var directConfig = VpnCore.BuildConfig(new[] { node }, rules, new AppSettings
    {
        Mode = "direct", SelectedNode = "TEST"
    });
    CheckAi(!((List<string>)directConfig["rules"]!).Contains("DOMAIN-SUFFIX,claude.ai,VEGA"),
        "Explicit direct mode bypasses presets");
    var configDir = UserStore.Home;
    string yaml = Path.Combine(configDir, "ai-service-test.yaml");
    var isolated = new Dictionary<string, object?>(built)
    {
        ["mixed-port"] = 18897,
        ["external-controller"] = "127.0.0.1:19997"
    };
    foreach (var listener in (object[])isolated["listeners"]!)
        ((Dictionary<string, object?>)listener)["port"] =
            Convert.ToInt32(((Dictionary<string, object?>)listener)["port"]) + 1050;
    File.WriteAllText(yaml, YamlWriter.Serialize(isolated));
    using var verify = Process.Start(new ProcessStartInfo(VpnCore.CoreExecutable)
    {
        Arguments = $"-t -f \"{yaml}\" -d \"{configDir}\"",
        UseShellExecute = false, CreateNoWindow = true,
        RedirectStandardError = true, RedirectStandardOutput = true
    })!;
    string stderr = await verify.StandardError.ReadToEndAsync();
    string stdout = await verify.StandardOutput.ReadToEndAsync();
    await verify.WaitForExitAsync();
    CheckAi(verify.ExitCode == 0, "Mihomo verifies compiled AI routing config: " + stderr.Take(110).Aggregate("", (a, c) => a+c));
    Console.WriteLine("ALL_AI_SERVICE_ROUTING_TESTS_PASS");
    return;
}
if (Environment.GetEnvironmentVariable("PROSTOKVN_RU_POLICY_TEST") == "1")
{
    void CheckRu(bool yes, string name)
    {
        if (!yes) throw new Exception("RU_ROUTE_TEST_FAILED: " + name);
        Console.WriteLine("PASS " + name);
    }
    CheckRu(RuDomainPolicy.IsRuDomain("vk.ru") &&
        RuDomainPolicy.IsRuDomain("SUN9-52.VKUSERPHOTO.RU.") &&
        !RuDomainPolicy.IsRuDomain("vk.ru.evil.com") &&
        !RuDomainPolicy.IsRuDomain("example.com"),
        "Only real .ru domain suffixes are classified");
    var saved = new List<RouteRule>
    {
        new() { Domain = "sun9-52.vkuserphoto.ru", Route = "VEGA", Provisional = true },
        new() { Domain = "queuev4.vk.ru", Route = "VEGA" },
        new() { Domain = "vk.ru", Route = "VEGA", Manual = true },
        new() { Domain = "vkuserphoto.ru", Route = "DIRECT", Manual = true },
        new() { Domain = "chatgpt.com", Route = "VEGA" }
    };
    UserStore.EnsureHome();
    UserStore.SaveRules(saved);
    string original = File.ReadAllText(Path.Combine(UserStore.Home, "routes.json"));
    string backupPath = UserStore.BackupRoutesBeforeRuCleanup();
    CheckRu(backupPath.Length > 0 && File.ReadAllText(backupPath) == original,
        "Backup preserves original routes.json before cleanup");
    int removed = RuDomainPolicy.RemoveAutomaticRoutes(saved);
    CheckRu(removed == 2 && saved.Count == 3
        && saved.Count(r => r.Manual && RuDomainPolicy.IsRuDomain(r.Domain)) == 2,
        "Only automatic .ru routes removed; manual overrides preserved");
    UserStore.SaveRules(saved);
    CheckRu(UserStore.LoadRules().Count == 3, "Cleaned routes persist");
    using var learner = new SmartRouteLearner(persistRules: false);
    await learner.InspectAsync("sun9-64.vkuserphoto.ru", saved, true, force: true);
    CheckRu(saved.Count == 3, "Auto-learning cannot recreate an .ru rule");
    var proxy = new ProxyNode { Name = "DUMMY", Values = new()
    {
        ["name"] = "DUMMY", ["type"] = "socks5",
        ["server"] = "127.0.0.1", ["port"] = 19888
    }};
    var stale = saved.Append(new RouteRule {
        Domain = "another.vk.ru", Route = "VEGA", Manual = false
    }).ToList();
    var config = VpnCore.BuildConfig(new[] { proxy }, stale,
        new AppSettings { Mode = "smart", SelectedNode = "DUMMY" });
    var configRules = (List<string>)config["rules"]!;
    int manual = configRules.IndexOf("DOMAIN-SUFFIX,vk.ru,VEGA");
    int ruDirect = configRules.IndexOf("DOMAIN-SUFFIX,ru,DIRECT");
    CheckRu(manual >= 0 && ruDirect > manual,
        "Explicit manual VPN exception precedes .ru DIRECT fallback");
    CheckRu(!configRules.Contains("DOMAIN-SUFFIX,another.vk.ru,VEGA"),
        "Config ignores stale automatic .ru VPN rules");
    CheckRu(configRules.IndexOf("DOMAIN-SUFFIX,chatgpt.com,VEGA") >= 0,
        "Existing non-.ru automatic VPN rules remain");
    var vpnConfig = VpnCore.BuildConfig(new[] { proxy }, saved,
        new AppSettings { Mode = "vpn", SelectedNode = "DUMMY" });
    CheckRu(!((List<string>)vpnConfig["rules"]!).Contains("DOMAIN-SUFFIX,ru,DIRECT"),
        "Explicit all-VPN mode is unchanged");
    Console.WriteLine("ALL_RU_POLICY_TESTS_PASS");
    return;
}
if (Environment.GetEnvironmentVariable("PROSTOKVN_IP_RULE_TEST") == "1")
{
    void Check(bool condition, string label)
    {
        if (!condition) throw new Exception("EXCEPTION_RULE_TEST_FAILED: " + label);
        Console.WriteLine("PASS: " + label);
    }
    RouteTarget Parse(string value)
    {
        Check(RouteTarget.TryParse(value, out var target), "Valid target " + value);
        return target;
    }
    var domain = Parse("*.Example.COM");
    Check(domain.Value == "example.com"
        && domain.ToRule("DIRECT") == "DOMAIN-SUFFIX,example.com,DIRECT",
        "Wildcard and casing normalization");
    var ipv4 = Parse("8.8.8.8");
    Check(ipv4.ToRule("DIRECT") == "IP-CIDR,8.8.8.8/32,DIRECT,no-resolve",
        "Exact IPv4 exclusion");
    Check(ipv4.Matches("", "8.8.8.8") && !ipv4.Matches("", "8.8.8.9"),
        "Single IP matching");
    var ipv6 = Parse("2606:4700:4700::1111");
    Check(ipv6.ToRule("VEGA") == "IP-CIDR6,2606:4700:4700::1111/128,VEGA,no-resolve",
        "Exact IPv6 route");
    var subnet = Parse("149.154.167.91/20");
    Check(subnet.Value == "149.154.160.0/20" && subnet.Matches("", "149.154.175.100")
        && !subnet.Matches("", "149.155.0.1"),
        "CIDR normalization and membership");
    var sixNetwork = Parse("2001:db8:abcd:1234::1/48");
    Check(sixNetwork.Value == "2001:db8:abcd::/48"
        && sixNetwork.Matches("", "2001:db8:abcd:11::9")
        && !sixNetwork.Matches("", "2001:db8:abce::1"),
        "IPv6 network normalization");
    foreach (var invalid in new[]
    {
        "999.1.1.1", "1.2.3.4/33", "2001:db8::1/129",
        "1.2.3.4/-1", "1.2.3.4/abc", "example.com/path", "10.0.0.0//8"
    })
        Check(!RouteTarget.TryParse(invalid, out _), "Reject invalid " + invalid);
    var dummy = new ProxyNode
    {
        Name = "TEST",
        Values = new Dictionary<string, object?>
        {
            ["name"] = "TEST", ["type"] = "socks5",
            ["server"] = "127.0.0.1", ["port"] = 19888
        }
    };
    var exceptions = new List<RouteRule>
    {
        new() { Domain = "149.154.160.0/20", Route = "VEGA", Manual = true },
        new() { Domain = "149.154.167.91", Route = "DIRECT", Manual = true },
        new() { Domain = "chatgpt.com", Route = "DIRECT", Manual = true },
        new() { Domain = "chatgpt.com", Route = "VEGA", Manual = false },
        new() { Domain = "2001:db8::/32", Route = "REJECT", Manual = true }
    };
    var settings = new AppSettings { Mode = "smart", SelectedNode = "TEST" };
    var built = VpnCore.BuildConfig(new[] { dummy }, exceptions, settings);
    var configRules = (List<string>)built["rules"]!;
    var exactIndex = configRules.IndexOf("IP-CIDR,149.154.167.91/32,DIRECT,no-resolve");
    var networkIndex = configRules.IndexOf("IP-CIDR,149.154.160.0/20,VEGA,no-resolve");
    var telegramIndex = configRules.IndexOf("PROCESS-NAME,Telegram.exe,VEGA");
    var manualDomainIndex = configRules.IndexOf("DOMAIN-SUFFIX,chatgpt.com,DIRECT");
    var autoDomainIndex = configRules.IndexOf("DOMAIN-SUFFIX,chatgpt.com,VEGA");
    Check(exactIndex >= 0 && networkIndex > exactIndex && telegramIndex > networkIndex,
        "Manual exact IP before subnet and Telegram");
    Check(manualDomainIndex >= 0 && autoDomainIndex > manualDomainIndex,
        "Manual domain overrides auto learning");
    Check(configRules.Contains("IP-CIDR6,2001:db8::/32,REJECT,no-resolve"), "IPv6 CIDR included");
    Check((bool)built["ipv6"]!, "IPv6 is enabled only when user added an IPv6 target");
    Check((bool)((Dictionary<string, object?>)built["dns"]!)["ipv6"]!, "IPv6 DNS is enabled for IPv6 target");
    var ipv4Only = VpnCore.BuildConfig(new[] { dummy }, exceptions.Where(r =>
        !r.Domain.Contains(':')).ToList(), settings);
    Check(!(bool)ipv4Only["ipv6"]!, "Default IPv4-only configuration unchanged");
    UserStore.EnsureHome();
    string testYaml = Path.Combine(UserStore.Home, "test-prostokvn-ip-exclusions.yaml");
    var isolatedCfg = new Dictionary<string, object?>(built)
    {
        ["mixed-port"] = 18890,
        ["external-controller"] = "127.0.0.1:19995"
    };
    foreach (var listener in (object[])isolatedCfg["listeners"]!)
        ((Dictionary<string, object?>)listener)["port"] =
            Convert.ToInt32(((Dictionary<string, object?>)listener)["port"]) + 1000;
    File.WriteAllText(testYaml, YamlWriter.Serialize(isolatedCfg));
    try
    {
        using var verify = Process.Start(new ProcessStartInfo(VpnCore.CoreExecutable)
        {
            Arguments = $"-t -f \"{testYaml}\" -d \"{UserStore.Home}\"",
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        })!;
        string error = await verify.StandardError.ReadToEndAsync()
            + await verify.StandardOutput.ReadToEndAsync();
        await verify.WaitForExitAsync();
        Check(verify.ExitCode == 0, "Mihomo validates domain, IPv4/IPv6, CIDR and route precedence");
        if (verify.ExitCode != 0) throw new Exception(error);
    }
    finally { File.Delete(testYaml); }
    Check(VpnCore.IsStaleConnection("", new[] { "VEGA", "TEST" }, exceptions,
            "smart", "149.154.167.91", "Telegram.exe"), "Stale Telegram session obeys manual direct IP");
    Check(!VpnCore.IsStaleConnection("", new[] { "DIRECT" }, exceptions,
            "smart", "149.154.167.91", "Telegram.exe"), "Manual direct IP not interrupted");
    Check(VpnCore.IsStaleConnection("", new[] { "DIRECT" }, exceptions,
            "smart", "149.154.175.100", "Telegram.exe"), "Subnet VPN updates old direct session");
    Check(ServiceRuleGrouper.ServiceName("8.8.8.8") == "IP-адреса"
        && ServiceRuleGrouper.ServiceName("8.8.8.0/24") == "Подсети",
        "Separate compact UI categories");
    var labeled = new RouteRule
    {
        Domain = "149.154.160.0/20", Route = "DIRECT",
        Manual = true, Label = "Telegram"
    };
    Check(labeled.ToString().Contains("Telegram  ·  149.154.160.0/20"),
        "Friendly IP/subnet label displayed with address");
    string labelJson = System.Text.Json.JsonSerializer.Serialize(labeled);
    var labelRoundTrip = System.Text.Json.JsonSerializer.Deserialize<RouteRule>(labelJson);
    Check(labelRoundTrip?.Label == "Telegram" &&
        labelRoundTrip.Domain == "149.154.160.0/20",
        "Friendly name persists in old routes.json schema");
    var oldRule = System.Text.Json.JsonSerializer.Deserialize<RouteRule>(
        "{\"Domain\":\"8.8.8.8\",\"Route\":\"DIRECT\",\"Manual\":true}");
    Check(oldRule?.Label == "", "Existing saved rules load without a name");

    Console.WriteLine("ALL_IP_DOMAIN_SUBNET_TESTS=PASS");
    return;
}
string? url = Environment.GetEnvironmentVariable("VEGA_VPN_TEST_URL");
if (string.IsNullOrWhiteSpace(url)) { Console.WriteLine("No test URL"); return; }
string Rewrite(string input, string type)
{
    var b = new UriBuilder(input);
    var s = b.Path.Trim('/').Split('/');
    s[0] = type;
    b.Path = "/" + string.Join("/", s);
    return b.Uri.AbsoluteUri;
}
var nodes = new List<ProxyNode>();
try
{
    var json = await JsonSubscription.LoadAsync(Rewrite(url, "json"));
    nodes.AddRange(json);
    Console.WriteLine("JSON_NODES=" + json.Count);
}
catch (Exception ex) { Console.WriteLine("JSON_FAILURE_TYPE=" + ex.GetType().Name); }
try
{
    var (fromSub, skipped) = await SubscriptionReader.LoadAsync(Rewrite(url, "sub"));
    Console.WriteLine("SUB_NODES=" + fromSub.Count + " SKIPPED=" + skipped.Count);
    var seen = new HashSet<string>(nodes.Select(Key));
    foreach (var n in fromSub) if (seen.Add(Key(n))) nodes.Add(n);
}
catch (Exception ex) { Console.WriteLine("SUB_FAILURE_TYPE=" + ex.GetType().Name); }
string Key(ProxyNode n) => string.Join("|", new[]{"type","server","port","uuid","network"}.Select(k => n.Values.TryGetValue(k, out var v) ? v?.ToString() : ""));
Console.WriteLine("TOTAL_NODES=" + nodes.Count);
int count = 0;
foreach (var node in nodes)
{
    var settings = new AppSettings { SelectedNode = node.Name };
    var data = VpnCore.BuildConfig(new[] { node }, new List<RouteRule>(), settings);
    data["secret"] = "test-only";
    string json = YamlWriter.Serialize(data);
    UserStore.EnsureHome();
    string path = Path.Combine(UserStore.Home, "smoketest-" + count + ".yaml");
    await File.WriteAllTextAsync(path, json);
    using var p = Process.Start(new ProcessStartInfo(VpnCore.CoreExecutable)
    {
        Arguments = $"-t -f \"{path}\" -d \"{UserStore.Home}\"",
        UseShellExecute=false, CreateNoWindow=true, RedirectStandardOutput=true, RedirectStandardError=true
    })!;
    string stdout = await p.StandardOutput.ReadToEndAsync();
    string stderr = await p.StandardError.ReadToEndAsync();
    await p.WaitForExitAsync();
    var type = node.Values["type"]?.ToString();
    string net = node.Values.TryGetValue("network", out var nw) ? nw?.ToString() ?? "tcp" : "tcp";
    Console.WriteLine($"NODE_{count}={type}/{net} TEST_EXIT={p.ExitCode}");
    if (p.ExitCode != 0)
    {
        string errorLine = (stderr + stdout).Split('\n').FirstOrDefault(x => x.Contains("error", StringComparison.OrdinalIgnoreCase) || x.Contains("failed", StringComparison.OrdinalIgnoreCase)) ?? "";
        errorLine = System.Text.RegularExpressions.Regex.Replace(errorLine, @"(?i)[a-f0-9]{8}-[a-f0-9-]{25,}", "[id]");
        Console.WriteLine("  ERROR=" + errorLine[..Math.Min(errorLine.Length, 230)]);
    }
    File.Delete(path);
    count++;
}

if (Environment.GetEnvironmentVariable("VEGA_VPN_LIVE_TEST") == "1" && nodes.Count > 0)
{
    int index = int.TryParse(Environment.GetEnvironmentVariable("VEGA_VPN_LIVE_INDEX"), out var idx) && idx >= 0 && idx < nodes.Count ? idx : 0;
    var sample = nodes[index];
    Console.WriteLine("LIVE_NODE_INDEX=" + index);
    var settings = new AppSettings { SelectedNode = sample.Name, Mode = "smart" };
    var config = VpnCore.BuildConfig(new[] { sample }, new List<RouteRule>(), settings);
    config["secret"] = "live-test-secret";
    string cfg = Path.Combine(UserStore.Home, "live-smoke.yaml");
    await File.WriteAllTextAsync(cfg, YamlWriter.Serialize(config));
    using var core = Process.Start(new ProcessStartInfo(VpnCore.CoreExecutable)
    {
        Arguments = $"-f \"{cfg}\" -d \"{UserStore.Home}\"",
        UseShellExecute = false, CreateNoWindow = true,
        RedirectStandardOutput = true, RedirectStandardError = true
    })!;
    try
    {
        await Task.Delay(1600);
        Console.WriteLine("CORE_RUNNING=" + !core.HasExited);
        foreach (int port in new[] { VpnCore.DirectProbePort, VpnCore.VpnProbePort })
        {
            try
            {
                using var handler = new HttpClientHandler { Proxy = new WebProxy($"http://127.0.0.1:{port}"), UseProxy = true, AllowAutoRedirect = false };
                using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(9) };
                using var response = await http.GetAsync("https://www.cloudflare.com/cdn-cgi/trace", HttpCompletionOption.ResponseHeadersRead);
                Console.WriteLine("PROBE_" + port + "=HTTP_" + (int)response.StatusCode);
            }
            catch (Exception ex) { Console.WriteLine("PROBE_" + port + "=" + ex.GetType().Name); }
        }
    }
    finally
    {
        try { if (!core.HasExited) { core.Kill(true); await core.WaitForExitAsync(); } } catch { }
        string logs = (await core.StandardError.ReadToEndAsync()) + "\n" + (await core.StandardOutput.ReadToEndAsync());
        var trouble = logs.Split('\n').Where(l => l.Contains("error", StringComparison.OrdinalIgnoreCase) || l.Contains("fail", StringComparison.OrdinalIgnoreCase)).TakeLast(3);
        foreach (string line in trouble)
        {
            string cleaned = System.Text.RegularExpressions.Regex.Replace(line, @"(?i)([0-9]{1,3}\\.){3}[0-9]{1,3}", "[ip]");
            cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, @"[0-9a-fA-F]{8}-[0-9a-fA-F-]{25,}", "[id]");
            Console.WriteLine("CORE_ERROR=" + cleaned[..Math.Min(cleaned.Length, 320)]);
        }
        File.Delete(cfg);
    }
}

if (Environment.GetEnvironmentVariable("VEGA_VPN_XRAY_TEST") == "1")
{
    int index = int.TryParse(Environment.GetEnvironmentVariable("VEGA_VPN_LIVE_INDEX"), out var id) && id >= 0 && id < nodes.Count ? id : 1;
    var sample = nodes[index];
    Console.WriteLine("XRAY_NODE_INDEX=" + index);
    if (sample.NativeXrayConfig == null) { Console.WriteLine("NO_NATIVE_JSON"); return; }
    using var sidecar = new XraySidecar();
    try
    {
        await sidecar.StartAsync(sample.NativeXrayConfig);
        Console.WriteLine("XRAY_LISTENING=True");
        var settings = new AppSettings { SelectedNode = sample.Name, Mode = "smart" };
        var config = VpnCore.BuildConfig(new[] { sample }, new List<RouteRule>(), settings, useXraySidecar: true);
        config["secret"] = "test-only";
        string cfg = Path.Combine(UserStore.Home, "hybrid-test.yaml");
        await File.WriteAllTextAsync(cfg, YamlWriter.Serialize(config));
        using var core = Process.Start(new ProcessStartInfo(VpnCore.CoreExecutable)
        {
            Arguments = $"-f \"{cfg}\" -d \"{UserStore.Home}\"",
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardError = true, RedirectStandardOutput = true
        })!;
        try
        {
            await Task.Delay(1200);
            using var handler = new HttpClientHandler { Proxy = new WebProxy($"http://127.0.0.1:{VpnCore.VpnProbePort}"), UseProxy = true, AllowAutoRedirect = false };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(12) };
            using var response = await http.GetAsync("https://www.cloudflare.com/cdn-cgi/trace", HttpCompletionOption.ResponseHeadersRead);
            Console.WriteLine("XRAY_VPN_PROBE_HTTP=" + (int)response.StatusCode);
        }
        catch (Exception ex) { Console.WriteLine("XRAY_VPN_PROBE_ERROR=" + ex.GetType().Name); }
        finally
        {
            try { if (!core.HasExited) { core.Kill(true); await core.WaitForExitAsync(); } } catch { }
            File.Delete(cfg);
        }
    }
    catch (Exception ex) { Console.WriteLine("XRAY_SETUP_ERROR=" + ex.GetType().Name + ": " + ex.Message[..Math.Min(180, ex.Message.Length)]); }
}

if (Environment.GetEnvironmentVariable("VEGA_VPN_ALT_TEST") == "1" && nodes.Count > 0)
{
    var settings = new AppSettings { SelectedNode = nodes.FirstOrDefault(x => x.Values.TryGetValue("type", out var t) && t?.ToString() == "hysteria2")?.Name ?? nodes[0].Name, Mode = "smart" };
    var config = VpnCore.BuildConfig(nodes, new List<RouteRule> { new() { Domain="chatgpt.com", Route="VEGA", Manual=true } }, settings);
    config["mixed-port"] = 18890;
    config["external-controller"] = "127.0.0.1:19990";
    config["secret"] = "test-only";
    foreach (var listener in (object[])config["listeners"]!)
    {
        var item = (Dictionary<string,object?>)listener;
        item["port"] = Convert.ToInt32(item["port"]) + 1000;
    }
    string temp = Path.Combine(UserStore.Home, "alternate-nodes-check.yaml");
    File.WriteAllText(temp, YamlWriter.Serialize(config));
    using var child = Process.Start(new ProcessStartInfo(VpnCore.CoreExecutable)
    {
        Arguments = $"-f \"{temp}\" -d \"{UserStore.Home}\"",
        UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true
    })!;
    try
    {
        await Task.Delay(1400);
        Console.WriteLine("ALT_CORE_RUNNING=" + !child.HasExited);
        try
        {
            var updateRules = (List<string>)config["rules"]!;
            updateRules.Insert(updateRules.Count - 1, "DOMAIN-SUFFIX,openai.com,VEGA");
            using var client = new HttpClient() { Timeout = TimeSpan.FromSeconds(4) };
            using var req = new HttpRequestMessage(HttpMethod.Put, "http://127.0.0.1:19990/configs?force=true");
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer test-only");
            req.Content = new StringContent(JsonSerializer.Serialize(new { payload = YamlWriter.Serialize(config) }), System.Text.Encoding.UTF8, "application/json");
            using var resp = await client.SendAsync(req);
            Console.WriteLine("ROUTES_RELOAD_HTTP=" + (int)resp.StatusCode);
        }
        catch(Exception ex) {Console.WriteLine("ROUTES_RELOAD_ERROR=" + ex.GetType().Name);}
        foreach (int i in new[]{2,3,7})
        {
            if (i >= nodes.Count) continue;
            int port=VpnCore.NodeProbePortBase+i+1000;
            using var handler = new HttpClientHandler { Proxy = new WebProxy($"http://127.0.0.1:{port}"), UseProxy = true, AllowAutoRedirect = false };
            using var http = new HttpClient(handler) { Timeout=TimeSpan.FromSeconds(8) };
            try
            {
                using var resp = await http.GetAsync("https://chatgpt.com/",HttpCompletionOption.ResponseHeadersRead);
                Console.WriteLine($"ALT_NODE_{i}=HTTP_{(int)resp.StatusCode}");
            }
            catch(Exception ex){Console.WriteLine($"ALT_NODE_{i}={ex.GetType().Name}");}
        }
    }
    finally
    {
        try {if(!child.HasExited){child.Kill(true);await child.WaitForExitAsync();}}catch{}
        string logMessages = (await child.StandardOutput.ReadToEndAsync())
            + (await child.StandardError.ReadToEndAsync());
        Console.WriteLine("CORE_INFO_LOG_HAS_TCP=" + logMessages.Contains("[TCP]", StringComparison.OrdinalIgnoreCase));
        Console.WriteLine("CORE_INFO_LOG_HAS_DESTINATION=" +
            System.Text.RegularExpressions.Regex.IsMatch(logMessages,
                @"-->\s+chatgpt\.com:443", System.Text.RegularExpressions.RegexOptions.IgnoreCase));
        File.Delete(temp);
    }
}

if (Environment.GetEnvironmentVariable("VEGA_VPN_LOGIC_TEST") == "1" && nodes.Count > 3)
{
    var manuallyPinned = new List<RouteRule>{new(){Domain="chatgpt.com", Route="VEGA", Manual=true, PreferredNode=nodes[3].Name}};
    var current = new AppSettings{SelectedNode=nodes[2].Name, Mode="smart"};
    var built = VpnCore.BuildConfig(nodes, manuallyPinned, current);
    var builtRules = (List<string>)built["rules"]!;
    Console.WriteLine("PINNED_DOMAIN_NODE_RULE="+builtRules.Contains($"DOMAIN-SUFFIX,chatgpt.com,{nodes[3].Name}"));
    Console.WriteLine("DEDICATED_PROBE_LISTENERS="+(((object[])built["listeners"]!).Length==nodes.Count+2));
    using var learner = new SmartRouteLearner();
    var result=await learner.ProbeDetailsAsync("chatgpt.com");
    Console.WriteLine("CLASSIFIER_DIRECT="+result.Direct.State);
    Console.WriteLine("CLASSIFIER_VPN="+result.Vpn.State);
}

if (Environment.GetEnvironmentVariable("VEGA_VPN_VERDICT_TEST") == "1")
{
    var denied = new RouteComparison(
        new RouteProbe(ProbeState.Restricted, 403, "HTTP forbidden"),
        new RouteProbe(ProbeState.Restricted, 403, "HTTP forbidden"));
    var pinned = new RouteRule { Domain = "chatgpt.com", Manual = true, Route = "VEGA" };
    var description = RouteAssessment.Describe("chatgpt.com", denied, pinned);
    if (!description.Contains("HTTP-ПРОВЕРКА") || !description.Contains("HTTP 403"))
        throw new Exception("FALSE POSITIVE: HTTP 403 was marked reachable");
    Console.WriteLine("TEST_BOTH_403=PASS");
    var uncertain = new RouteComparison(
        new RouteProbe(ProbeState.Unknown, 200, "webapp unconfirmed"),
        new RouteProbe(ProbeState.Unknown, 200, "webapp unconfirmed"));
    var text = RouteAssessment.Describe("chatgpt.com", uncertain, null);
    if (!text.Contains("ДОСТУПНОСТЬ НЕ ПОДТВЕРЖДЕНА"))
        throw new Exception("FALSE POSITIVE: HTTP 200 was marked usable");
    Console.WriteLine("TEST_APP_200_NOT_VERIFIED=PASS");
    using var liveLearner = new SmartRouteLearner();
    var live = await liveLearner.ProbeDetailsAsync("chatgpt.com");
    Console.WriteLine($"LIVE_CHATGPT_DIRECT={live.Direct.State} HTTP={live.Direct.HttpCode}");
    Console.WriteLine($"LIVE_CHATGPT_VPN={live.Vpn.State} HTTP={live.Vpn.HttpCode}");
    var liveSummary = RouteAssessment.Describe("chatgpt.com", live, pinned);
    if (live.Direct.Usable && live.Vpn.Usable)
        throw new Exception("Both routes claimed accessible unexpectedly");
    Console.WriteLine("LIVE_NOT_FALSE_AVAILABLE=PASS");
}

if (Environment.GetEnvironmentVariable("VEGA_VPN_AUTO_403_TEST") == "1")
{
    var both403 = new RouteComparison(new RouteProbe(ProbeState.Restricted, 403, "blocked"),
        new RouteProbe(ProbeState.Restricted, 403, "challenge"));
    void Assert(bool expression, string description)
    {
        if (!expression) throw new Exception("FAIL: " + description);
        Console.WriteLine("PASS: " + description);
    }
    Assert(AmbiguousRoutePolicy.CanTryVpn(both403, "RU", "DE"), "RU direct and foreign VPN with 403 challenge -> trial VPN");
    Assert(!AmbiguousRoutePolicy.CanTryVpn(both403, "DE", "DE"), "Non-RU direct does not automatically force VPN");
    Assert(!AmbiguousRoutePolicy.CanTryVpn(both403, "RU", "RU"), "Same RU exit: no trial");
    Assert(!AmbiguousRoutePolicy.CanTryVpn(both403, "RU", ""), "Unknown egress: no trial");
    Assert(!AmbiguousRoutePolicy.CanTryVpn(new RouteComparison(
         new RouteProbe(ProbeState.Reachable, 200, "ok"),
         new RouteProbe(ProbeState.Restricted, 403, "anti-bot")), "RU", "DE"),
         "Working DIRECT remains DIRECT");
    Assert(!AmbiguousRoutePolicy.CanTryVpn(new RouteComparison(
         new RouteProbe(ProbeState.Restricted, 403, "blocked"),
         new RouteProbe(ProbeState.Failed, null, "TLS failure")), "RU", "DE"),
         "Failed VPN tunnel cannot be automatically chosen");
    Assert(AmbiguousRoutePolicy.ConfirmAmbiguous(both403, both403), "Repeated challenge eligible for trial route");
    var trial = new RouteRule { Domain = "trial.example", Route = "VEGA",
        Manual = false, Provisional = true, ExpiresUtc = DateTime.UtcNow.AddHours(12) };
    Assert(trial.ToString().Contains("предположительно"), "Trial route is clearly labeled tentative");
    var summary = RouteAssessment.Describe("trial.example", both403, trial);
    Assert(summary.Contains("пробный AUTO") && summary.Contains("Обычный браузер"),
        "Trial UI explains browser vs HTTP");
    if (Environment.GetEnvironmentVariable("VEGA_VPN_AUTO_403_LIVE") == "1")
    {
        using var directHttp = new HttpClient(new HttpClientHandler
        { Proxy = new WebProxy("http://127.0.0.1:17891"), UseProxy = true })
        { Timeout = TimeSpan.FromSeconds(9) };
        using var vpnHttp = new HttpClient(new HttpClientHandler
        { Proxy = new WebProxy("http://127.0.0.1:17892"), UseProxy = true })
        { Timeout = TimeSpan.FromSeconds(9) };
        var countries = await Task.WhenAll(
            AmbiguousRoutePolicy.ReadCountryAsync(directHttp),
            AmbiguousRoutePolicy.ReadCountryAsync(vpnHttp));
        Console.WriteLine("EXIT_COUNTRIES=" + countries[0] + "/" + countries[1]);
        using var learner = new SmartRouteLearner();
        var comparison = await learner.ProbeDetailsAsync("chatgpt.com");
        Console.WriteLine("CHATGPT_HTTP_STATES=" + comparison.Direct.State + "/" + comparison.Vpn.State);
        Console.WriteLine("TRIAL_ELIGIBLE=" + AmbiguousRoutePolicy.CanTryVpn(comparison, countries[0], countries[1]));
    }
}

if (Environment.GetEnvironmentVariable("VEGA_VPN_TRIAL_E2E") == "1")
{
    using var noSaveLearner = new SmartRouteLearner(persistRules: false);
    var localRules = new List<RouteRule>();
    int changed = 0;
    noSaveLearner.Learned += _ => changed++;
    await noSaveLearner.InspectAsync("chatgpt.com", localRules, true, nodes,
        nodes.FirstOrDefault()?.Name ?? "", force: true);
    var found = localRules.SingleOrDefault(x => x.Domain == "chatgpt.com");
    if (found?.Route != "VEGA" || !found.Provisional || !found.ExpiresUtc.HasValue)
        throw new Exception("No tentative VPN rule created for ambiguous 403.");
    if (changed != 1) throw new Exception("Expected exactly one route update.");
    var liveConfig = VpnCore.BuildConfig(nodes, localRules,
        new AppSettings { SelectedNode = nodes.First().Name, Mode = "smart" });
    var builtRuleLines = (List<string>)liveConfig["rules"]!;
    int autoIndex = builtRuleLines.IndexOf("DOMAIN-SUFFIX,chatgpt.com,VEGA");
    int defaultIndex = builtRuleLines.IndexOf("MATCH,DIRECT");
    if (autoIndex < 0 || defaultIndex < 0 || autoIndex >= defaultIndex)
        throw new Exception("Trial rule must precede MATCH,DIRECT for auto-routing");
    Console.WriteLine("E2E_ROUTE_PRECEDENCE=PASS");
    await noSaveLearner.InspectAsync("chatgpt.com", localRules, true, nodes,
        nodes.FirstOrDefault()?.Name ?? "", force: false);
    if (changed != 1 || localRules.Count != 1)
        throw new Exception("Learned route got unnecessarily rechecked.");
    Console.WriteLine("E2E_STABLE_RULE_NOT_RECHECKED=PASS");
    Console.WriteLine("E2E_TRIAL_CHATGPT=PASS, PROVISIONAL=True, TTL_HOURS="
        + Math.Round((found.ExpiresUtc.Value - DateTime.UtcNow).TotalHours, 1));
}

if (Environment.GetEnvironmentVariable("VEGA_VPN_STALE_TEST") == "1")
{
    var rules = new List<RouteRule> { new() { Domain = "chatgpt.com", Route = "VEGA", Manual = false } };
    void Check(bool actual, string label)
    {
        if (!actual) throw new Exception("STALE ROUTE FAILURE: " + label);
        Console.WriteLine("PASS: " + label);
    }
    Check(VpnCore.IsStaleConnection("chatgpt.com", new[] { "DIRECT" }, rules, "smart"), "Existing DIRECT connection must be refreshed");
    Check(VpnCore.IsStaleConnection("sub.chatgpt.com", new[] { "DIRECT" }, rules, "smart"), "Subdomain old DIRECT refreshed");
    Check(!VpnCore.IsStaleConnection("chatgpt.com", new[] { "VEGA", "Server 1" }, rules, "smart"), "Already proxied is not closed");
    Check(!VpnCore.IsStaleConnection("unrelated.example", new[] { "DIRECT" }, rules, "smart"), "Unrelated domains untouched");
    Check(!VpnCore.IsStaleConnection("chatgpt.com", new[] { "DIRECT" }, rules, "vpn"), "Smart auto rule not applied outside smart mode");
    rules.Add(new RouteRule { Domain = "chatgpt.com", Route = "DIRECT", Manual = true });
    Check(!VpnCore.IsStaleConnection("chatgpt.com", new[] { "DIRECT" }, rules, "smart"), "Manual DIRECT takes priority");
    Check(VpnCore.IsStaleConnection("chatgpt.com", new[] { "VEGA", "Server 1" }, rules, "smart"), "Manual DIRECT switches old VPN session");
}

if (Environment.GetEnvironmentVariable("VEGA_VPN_MULTICORE_TEST") == "1")
{
    Console.WriteLine("AUTOMATIC_CORE_SELECTION:");
    for (int i = 0; i < nodes.Count; i++)
        Console.WriteLine($"CORE_{i}={EngineSelector.Choose(nodes[i])}");
    foreach (int i in new[] { 2, 3, 7 })
    {
        if (i >= nodes.Count || EngineSelector.Choose(nodes[i]) != TunnelEngine.SingBox)
            continue;
        using var sing = new SingBoxSidecar();
        sing.OnLog += msg => { if (msg.Contains("error", StringComparison.OrdinalIgnoreCase) || msg.Contains("warn", StringComparison.OrdinalIgnoreCase)) Console.WriteLine("SING_LOG_"+i+"="+msg[..Math.Min(msg.Length,240)]); };
        Console.WriteLine("TEST_SING_NODE_INDEX=" + i);
        try
        {
            await sing.StartAsync(nodes[i]);
            using var handler = new HttpClientHandler {
                Proxy = new WebProxy("socks5://127.0.0.1:" + SingBoxSidecar.SocksPort),
                UseProxy = true,
                AllowAutoRedirect = false
            };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
            using var response = await http.GetAsync("https://www.cloudflare.com/cdn-cgi/trace",
                HttpCompletionOption.ResponseHeadersRead);
            Console.WriteLine("SING_NODE_"+i+"_HTTP="+(int)response.StatusCode);
        }
        catch (Exception error)
        {
            Console.WriteLine("SING_NODE_"+i+"_ERROR="+error.GetType().Name);
            Console.WriteLine("SING_NODE_"+i+"_ERROR_SUMMARY="+error.ToString()[..Math.Min(450,error.ToString().Length)]);
        }
    }
}

if (Environment.GetEnvironmentVariable("VEGA_VPN_TRI_CORE_E2E") == "1")
{
    foreach (int index in new[] { 2, 3 })
    {
        if (index >= nodes.Count) continue;
        var chosen = nodes[index];
        if (EngineSelector.Choose(chosen) != TunnelEngine.SingBox)
            throw new Exception("Wrong engine selected");
        using var sidecar = new SingBoxSidecar();
        Console.WriteLine("TRI_CORE_TEST_INDEX=" + index);
        try
        {
            await sidecar.StartAsync(chosen);
            var settings = new AppSettings { Mode = "vpn", SelectedNode = chosen.Name };
            var built = VpnCore.BuildConfig(new[] { chosen }, new List<RouteRule>(), settings,
                useSingBoxSidecar: true);
            built["mixed-port"] = 18890;
            built["external-controller"] = "127.0.0.1:19991";
            built["secret"] = "tri-core-only";
            foreach (var listener in (object[])built["listeners"]!)
                ((Dictionary<string, object?>)listener)["port"] =
                    Convert.ToInt32(((Dictionary<string, object?>)listener)["port"]) + 1000;
            string file = Path.Combine(UserStore.Home, "tri-core-isolated.yaml");
            await File.WriteAllTextAsync(file, YamlWriter.Serialize(built));
            using var mihomo = Process.Start(new ProcessStartInfo(VpnCore.CoreExecutable)
            {
                Arguments = $"-f \"{file}\" -d \"{UserStore.Home}\"",
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            })!;
            try
            {
                await Task.Delay(1200);
                using var handler = new HttpClientHandler { Proxy = new WebProxy("http://127.0.0.1:18892"), UseProxy = true };
                using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(12) };
                using var response = await http.GetAsync("https://www.cloudflare.com/cdn-cgi/trace",
                    HttpCompletionOption.ResponseHeadersRead);
                Console.WriteLine("TRI_CORE_"+index+"_HTTP="+(int)response.StatusCode);
                if ((int)response.StatusCode != 200) throw new Exception("Test request via Mihomo/sing-box failed");
            }
            finally
            {
                if (!mihomo.HasExited) { mihomo.Kill(true); await mihomo.WaitForExitAsync(); }
                File.Delete(file);
            }
        }
        catch (Exception error) { Console.WriteLine("TRI_CORE_"+index+"_ERROR="+error.GetType().Name); }
    }
}

if (Environment.GetEnvironmentVariable("VEGA_VPN_STABILITY_TEST") == "1")
{
    void Assert(bool expression, string message)
    {
        if (!expression) throw new Exception("STABILITY TEST FAILED: " + message);
        Console.WriteLine("STABILITY_PASS=" + message);
    }
    Assert(VpnCore.IsDiagnosticConnection("InName", "vega-probe-direct"), "DIRECT probe ignored");
    Assert(VpnCore.IsDiagnosticConnection("InName", "vega-probe-vpn"), "VPN probe ignored");
    Assert(VpnCore.IsDiagnosticConnection("InName", "vega-probe-node-3"), "Per-server probe ignored");
    Assert(!VpnCore.IsDiagnosticConnection("DomainSuffix", "chatgpt.com"), "Real browser connection preserved");
    Assert(!VpnCore.IsDiagnosticConnection("Match", null), "Normal unmatched traffic preserved");
    Assert(!VpnCore.IsDiagnosticConnection("InName", "user-custom-listener"), "Third-party local listener preserved");
}

if (Environment.GetEnvironmentVariable("VEGA_VPN_SERVICE_TEST") == "1")
{
    void Assert(bool pass, string label)
    {
        if (!pass) throw new Exception("SERVICE_TEST_FAILED: " + label);
        Console.WriteLine("PASS: " + label);
    }

    var current = UserStore.LoadRules();
    int originalCount = current.Count;
    var originals = current.Select(x => (x.Domain, x.Route, x.Manual, x.Provisional)).ToArray();
    int merged = ServiceRuleGrouper.CompactStableRules(current);
    Console.WriteLine("GROUP_BEFORE=" + originalCount + " GROUP_AFTER=" + current.Count + " COLLAPSED=" + merged);
    Assert(merged > 4, "Repeated YouTube/GoogleVideo rules consolidated");
    Assert(current.Any(r => r.Domain == "googlevideo.com" && r.Route == "VEGA"), "GoogleVideo service root is preserved");
    Assert(current.Any(r => r.Domain == "youtube.com" && r.Route == "VEGA"), "YouTube service root is preserved");
    Assert(originals.Where(x => x.Manual || x.Provisional).All(x =>
       current.Any(r => r.Domain == x.Domain && r.Route == x.Route
          && r.Manual == x.Manual && r.Provisional == x.Provisional)),
       "Manual and provisional domain rules are unchanged");
    Assert(ServiceRuleGrouper.CompactStableRules(current) == 0, "Compaction is idempotent");
    Assert(ServiceRuleGrouper.ServiceName("rr5---sn-123.googlevideo.com") == "YouTube / Google Video",
       "GoogleVideo belongs to YouTube");
    Assert(ServiceRuleGrouper.ServiceName("ws.chatgpt.com") == "ChatGPT / OpenAI",
       "ChatGPT service group");
    Assert(ServiceRuleGrouper.ServiceName("array815.prod.do.dsp.mp.microsoft.com") == "Microsoft",
       "Microsoft service group");
    var telegram = new AppSettings { SelectedNode = nodes[2].Name, Mode = "smart", Tun = true };
    var config = VpnCore.BuildConfig(nodes, current, telegram);
    var ruleLines = (List<string>)config["rules"]!;
    int matchIndex = ruleLines.IndexOf("MATCH,DIRECT");
    Assert(matchIndex >= 0, "Default Smart direct rule exists");
    Assert(ruleLines.IndexOf("PROCESS-NAME,Telegram.exe,VEGA") < matchIndex,
       "Telegram process route precedes DIRECT fallback");
    Assert(ruleLines.IndexOf("IP-CIDR,149.154.160.0/20,VEGA,no-resolve") < matchIndex,
       "Telegram MTProto IP route precedes DIRECT fallback");
    Assert(ruleLines.IndexOf("DOMAIN-SUFFIX,t.me,VEGA") < matchIndex,
       "Telegram web route precedes DIRECT fallback");
    Assert(TelegramRouting.IsTelegramAddress("149.154.167.91"), "Observed TG DC1 mapped to Telegram");
    Assert(TelegramRouting.IsTelegramAddress("149.154.175.100"), "Observed TG DC2 mapped to Telegram");
    Assert(!TelegramRouting.IsTelegramAddress("8.8.8.8"), "Non-Telegram IP not misclassified");
    Assert(VpnCore.IsStaleConnection("", new[] { "DIRECT" }, current, "smart",
        "149.154.167.91", "Telegram.exe"), "Existing Telegram DIRECT session is stale");
    Assert(!VpnCore.IsStaleConnection("", new[] { "VEGA", "Selected" }, current, "smart",
        "149.154.167.91", "Telegram.exe"), "Existing Telegram VPN session preserved");
    Assert(!VpnCore.IsStaleConnection("", new[] { "DIRECT" }, current, "direct",
        "149.154.167.91", "Telegram.exe"), "Direct mode disables Telegram overrides");
    var testConfig = new Dictionary<string, object?>(config);
    testConfig["mixed-port"] = 18890;
    testConfig["external-controller"] = "127.0.0.1:19990";
    foreach(var listener in (object[])testConfig["listeners"]!)
        ((Dictionary<string, object?>)listener)["port"] =
            Convert.ToInt32(((Dictionary<string, object?>)listener)["port"]) + 1000;
    var path = Path.Combine(UserStore.Home, "test-telegram-grouping.yaml");
    File.WriteAllText(path, YamlWriter.Serialize(testConfig));
    using (var check = Process.Start(new ProcessStartInfo(VpnCore.CoreExecutable)
    {
        Arguments = $"-t -f \"{path}\" -d \"{UserStore.Home}\"",
        UseShellExecute=false, CreateNoWindow=true, RedirectStandardError=true, RedirectStandardOutput=true
    })!)
    {
        string err = await check.StandardError.ReadToEndAsync();
        string stdout = await check.StandardOutput.ReadToEndAsync();
        await check.WaitForExitAsync();
        Console.WriteLine("COMPACT_TG_MIHOMO_EXIT=" + check.ExitCode);
        if (check.ExitCode != 0) throw new Exception("Mihomo rejected TG routing: " + (err + stdout));
    }
    File.Delete(path);
}

if (Environment.GetEnvironmentVariable("VEGA_VPN_TELEGRAM_ROUTE_E2E") == "1")
{
    var example = nodes.FirstOrDefault(n => Convert.ToString(n.Values["type"]) == "hysteria2") ?? nodes[0];
    var setting = new AppSettings { Mode = "smart", Tun = false, SelectedNode = example.Name };
    var cfg = VpnCore.BuildConfig(nodes, UserStore.LoadRules(), setting);
    cfg["mixed-port"] = 18890;
    cfg["external-controller"] = "127.0.0.1:19991";
    cfg["secret"] = "telegram-test-only";
    foreach(var listener in (object[])cfg["listeners"]!)
        ((Dictionary<string, object?>)listener)["port"] =
            Convert.ToInt32(((Dictionary<string, object?>)listener)["port"]) + 1000;
    string cfgPath = Path.Combine(UserStore.Home, "tg-e2e-isolated.yaml");
    File.WriteAllText(cfgPath, YamlWriter.Serialize(cfg));
    using var mihomo = Process.Start(new ProcessStartInfo(VpnCore.CoreExecutable)
    {
        Arguments = $"-f \"{cfgPath}\" -d \"{UserStore.Home}\"",
        CreateNoWindow=true,UseShellExecute=false,
        RedirectStandardOutput=true,RedirectStandardError=true
    }) ?? throw new InvalidOperationException("Could not start isolated Mihomo");
    try
    {
        await Task.Delay(1200);
        if(mihomo.HasExited) throw new Exception("Isolated Mihomo core failed to start");
        using var tcp = new System.Net.Sockets.TcpClient();
        using var deadline = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(7));
        await tcp.ConnectAsync("127.0.0.1", 18890, deadline.Token);
        using var stream = tcp.GetStream();
        byte[] reqBytes = System.Text.Encoding.ASCII.GetBytes(
            "CONNECT 149.154.167.91:443 HTTP/1.1\r\nHost: 149.154.167.91:443\r\n\r\n");
        await stream.WriteAsync(reqBytes, deadline.Token);
        await stream.FlushAsync(deadline.Token);
        await Task.Delay(1300);
        using var api = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        using var get = new HttpRequestMessage(HttpMethod.Get, "http://127.0.0.1:19991/connections");
        get.Headers.TryAddWithoutValidation("Authorization", "Bearer telegram-test-only");
        using var response = await api.SendAsync(get);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var conns = doc.RootElement.GetProperty("connections").EnumerateArray()
            .Where(x => x.GetProperty("metadata").TryGetProperty("destinationIP", out var v) &&
                        v.GetString() == "149.154.167.91").ToList();
        if (conns.Count == 0) throw new Exception("No Telegram connection observed in isolated Mihomo API");
        foreach(var conn in conns)
        {
            string rule = conn.GetProperty("rule").GetString() ?? "";
            string[] chain = conn.GetProperty("chains").EnumerateArray()
                .Select(x => x.GetString() ?? "").ToArray();
            Console.WriteLine("TG_IP_CONNECTION_RULE="+rule+" CHAIN="+string.Join(">",chain.TakeLast(2)));
            if (rule != "IP-CIDR" && rule != "IPCIDR" && rule != "IpCidr" && rule != "Process")
                throw new Exception("IP was not classified as Telegram: " + rule);
            if (chain.Any(x=>x=="DIRECT")) throw new Exception("Telegram routed via DIRECT");
        }
        Console.WriteLine("TG_REAL_IP_RULE_E2E=PASS");
    }
    finally
    {
        try {if(!mihomo.HasExited){mihomo.Kill(true);await mihomo.WaitForExitAsync();}}catch{}
        File.Delete(cfgPath);
    }
}
