using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Principal;
using System.Text.Json;
using System.Threading.Tasks;

namespace ProstoKVN;

public sealed class VpnCore : IDisposable
{
    public const int MainPort = 17890, DirectProbePort = 17891, VpnProbePort = 17892, ApiPort = 19090, NodeProbePortBase = 17920;
    private Process? process;
    private readonly XraySidecar xray = new();
    private readonly SingBoxSidecar singBox = new();
    private TunnelEngine activeEngine = TunnelEngine.Mihomo;
    public TunnelEngine ActiveEngine => activeEngine;
    private readonly string secret = Guid.NewGuid().ToString("N");
    private string? lastAppliedConfig;
    private HotRulePlan? lastHotPlan;
    public VpnCore()
    {
        xray.OnLog += line => OnLog?.Invoke(line);
        singBox.OnLog += line => OnLog?.Invoke(line);
    }
    private static bool WantsXray(ProxyNode? node) => node?.NativeXrayConfig != null && node.Values.ContainsKey("reality-opts");
    private readonly HttpClient api = new() { Timeout = TimeSpan.FromSeconds(7) };
    private bool proxyChanged;
    public bool IsRunning => process is { HasExited: false };
    public bool IsTransportHealthy => IsRunning
        && (activeEngine != TunnelEngine.Xray || xray.IsRunning)
        && (activeEngine != TunnelEngine.SingBox || singBox.IsRunning);
    public event Action<string>? OnLog;
    public static string CoreExecutable
    {
        get
        {
            string local = Path.Combine(AppContext.BaseDirectory, "core", "mihomo.exe");
            return local;
        }
    }
    public static bool IsAdmin()
    {
        using var id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }
    public static bool IsDomainSafe(string domain)
    {
        domain = domain.Trim().TrimEnd('.').ToLowerInvariant();
        if (domain.Length < 4 || domain.Length > 253 || !domain.Contains('.')) return false;
        if (domain == "localhost" || IPAddress.TryParse(domain, out _)) return false;
        return domain.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-')
            && domain.Split('.').All(label => label.Length is > 0 and <= 63 && label[0] != '-' && label[^1] != '-');
    }
    private static int RulePriority(string value)
    {
        if (!RouteTarget.TryParse(value, out var target)) return -1;
        if (target.IsAddress) return 3000 + target.PrefixBits;
        if (target.IsSubnet) return 2000 + target.PrefixBits;
        return 1000 + target.Value.Length;
    }
    public static Dictionary<string, object?> BuildConfig(IReadOnlyList<ProxyNode> nodes, IEnumerable<RouteRule> routeRules, AppSettings settings, bool useXraySidecar = false, bool useSingBoxSidecar = false, bool hotRules = false)
    {
        var rules = new List<string>
        {
            "IN-NAME,vega-probe-direct,DIRECT",
            "IN-NAME,vega-probe-vpn,VEGA",
            // Outer sidecar connections must never re-enter their own tunnel.
            // In all-VPN mode MATCH,VEGA would otherwise route the server
            // transport back to the same local SOCKS listener through TUN.
            "PROCESS-NAME,xray.exe,DIRECT",
            "PROCESS-NAME,sing-box.exe,DIRECT",
            "PROCESS-NAME,mihomo.exe,DIRECT",
            "IP-CIDR,127.0.0.0/8,DIRECT,no-resolve",
            "IP-CIDR,10.0.0.0/8,DIRECT,no-resolve",
            "IP-CIDR,172.16.0.0/12,DIRECT,no-resolve",
            "IP-CIDR,192.168.0.0/16,DIRECT,no-resolve",
            "IP-CIDR,169.254.0.0/16,DIRECT,no-resolve"
        };
        for (int i = 0; i < nodes.Count && i < 32; i++)
            rules.Add($"IN-NAME,vega-probe-node-{i},{nodes[i].Name}");
        var chosenNode = nodes.FirstOrDefault(x => x.Name == settings.SelectedNode) ?? nodes[0];
        // IPv6 traffic is enabled only if the user explicitly adds an IPv6
        // address/network exception. Existing IPv4-only installations stay unchanged.
        bool enableIpv6 = settings.Mode != "direct" && routeRules.Any(r =>
            r.Manual && RouteTarget.TryParse(r.Domain, out var t)
            && t.Kind is RouteTargetKind.IPv6 or RouteTargetKind.IPv6Subnet);
        if (hotRules && settings.Mode != "direct")
        {
            var plan = HotRuleProviders.Create(nodes, routeRules.ToList(), settings);
            rules.AddRange(plan.Rules.Take(plan.ManualRuleCount));
            rules.AddRange(DiscordRouting.BuildRules());
            rules.AddRange(plan.Rules.Skip(plan.ManualRuleCount));
        }
        else if (settings.Mode != "direct")
        {
            // User exceptions always precede auto-learning, Telegram networks and MATCH.
            // Exact addresses precede CIDRs; more-specific networks precede broad ones.
            var manualRules = routeRules.Where(x => x.Manual)
                .OrderByDescending(x => RulePriority(x.Domain))
                .ToList();
            var autoRules = settings.Mode == "smart"
                ? routeRules.Where(x => !x.Manual && (x.Route == "VEGA" || x.Route == "DIRECT")
                    && !RuDomainPolicy.IsRuDomain(x.Domain))
                .OrderByDescending(x => RulePriority(x.Domain))
                : Enumerable.Empty<RouteRule>();
            void AddUserRules(IEnumerable<RouteRule> source)
            {
                foreach (var r in source)
                {
                    if (!RouteTarget.TryParse(r.Domain, out var routeTarget)) continue;
                    string target = r.Route == "REJECT" ? "REJECT" : r.Route == "VEGA" ? "VEGA" : "DIRECT";
                    if (target == "VEGA" && !string.IsNullOrEmpty(r.PreferredNode))
                    {
                        var preferred = nodes.FirstOrDefault(n => n.Name == r.PreferredNode);
                        if (preferred != null && (preferred.Name == chosenNode.Name || !WantsXray(preferred)))
                            target = preferred.Name;
                    }
                    rules.Add(routeTarget.ToRule(target));
                }
            }
            // Explicit user overrides win. Then match Discord's desktop process so
            // IP-only WebRTC voice/video UDP uses VPN instead of smart-mode DIRECT.
            // Auto-learned HTTP domain routes cannot override this media policy.
            AddUserRules(manualRules);
            rules.AddRange(DiscordRouting.BuildRules());
            AddUserRules(autoRules);
        }
        // Dedicated AI service domains must use VPN on the very first request,
        // before the adaptive learner can sample connections. Manual exceptions
        // above this block remain authoritative, including explicit DIRECT.
        if (settings.Mode == "smart")
            rules.AddRange(AiServiceRouting.BuildFallbackRules());
        // Smart mode defaults all .ru sites to DIRECT, after explicit manual
        // overrides and before global fallback. VPN mode remains all-VPN by choice.
        if (settings.Mode == "smart")
            rules.Add("DOMAIN-SUFFIX,ru,DIRECT");
        // Telegram desktop uses IP-only MTProto sessions. Domain-only training
        // never sees those destinations, so route by process and official DC CIDRs.
        // Explicit manual domain rules remain above the built-in Telegram rules.
        if (settings.Mode != "direct") rules.AddRange(TelegramRouting.BuildRules());
        rules.Add("MATCH," + (settings.Mode == "vpn" ? "VEGA" : "DIRECT"));
        var names = nodes.Select(n => n.Name).ToArray();
        var selected = names.Contains(settings.SelectedNode) ? settings.SelectedNode : names.First();
        var result = new Dictionary<string, object?>
        {
            ["mixed-port"] = MainPort,
            ["allow-lan"] = false,
            ["bind-address"] = "127.0.0.1",
            ["mode"] = "rule",
            ["log-level"] = "info",
            ["ipv6"] = enableIpv6,
            ["find-process-mode"] = "strict",
            ["external-controller"] = $"127.0.0.1:{ApiPort}",
            ["secret"] = "runtime-only",
            ["tun"] = new Dictionary<string, object?>
            {
                ["enable"] = settings.Tun,
                ["stack"] = "mixed",
                ["auto-route"] = settings.Tun,
                ["auto-detect-interface"] = true,
                ["dns-hijack"] = new[] { "any:53" },
                ["strict-route"] = false
            },
            ["dns"] = new Dictionary<string, object?>
            {
                ["enable"] = true,
                ["ipv6"] = enableIpv6,
                ["enhanced-mode"] = "fake-ip",
                ["fake-ip-range"] = "198.18.0.1/16",
                ["nameserver"] = new[] { "https://1.1.1.1/dns-query", "https://8.8.8.8/dns-query" },
                ["fake-ip-filter"] = new[] { "*.local", "*.lan", "localhost", "time.windows.com" }
            },
            ["sniffer"] = new Dictionary<string, object?>
            {
                ["enable"] = true,
                ["parse-pure-ip"] = true,
                ["sniff"] = new Dictionary<string, object?> { ["HTTP"] = new Dictionary<string, object?> { ["ports"] = new[] { "80", "8080-8880" } }, ["TLS"] = new Dictionary<string, object?> { ["ports"] = new[] { "443", "8443" } }, ["QUIC"] = new Dictionary<string, object?> { ["ports"] = new[] { "443" } } }
            },
            ["listeners"] = new object[]
            {
                new Dictionary<string, object?> { ["name"] = "vega-probe-direct", ["type"] = "http", ["listen"] = "127.0.0.1", ["port"] = DirectProbePort },
                new Dictionary<string, object?> { ["name"] = "vega-probe-vpn", ["type"] = "http", ["listen"] = "127.0.0.1", ["port"] = VpnProbePort }
            }.Concat(nodes.Take(32).Select((node, index) => (object)new Dictionary<string, object?>
            {
                ["name"] = "vega-probe-node-" + index,
                ["type"] = "http",
                ["listen"] = "127.0.0.1",
                ["port"] = NodeProbePortBase + index
            })).ToArray(),
            ["proxies"] = nodes.Select(n => (useXraySidecar || useSingBoxSidecar) && n.Name == selected
                ? new Dictionary<string, object?> { ["name"] = n.Name, ["type"] = "socks5", ["server"] = "127.0.0.1", ["port"] = useXraySidecar ? XraySidecar.SocksPort : SingBoxSidecar.SocksPort, ["udp"] = true }
                : n.Values).ToArray(),
            ["proxy-groups"] = new object[]
            {
                new Dictionary<string, object?> { ["name"] = "VEGA", ["type"] = "select", ["proxies"] = new[] { selected }.Concat(names.Where(n => n != selected)).ToArray() }
            },
            ["rules"] = rules
        };
        if (hotRules && settings.Mode != "direct")
            result["rule-providers"] = HotRuleProviders.Create(nodes, routeRules.ToList(), settings).Providers;
        return result;
    }
    public async Task StartAsync(IReadOnlyList<ProxyNode> nodes, List<RouteRule> rules, AppSettings settings)
    {
        if (nodes.Count == 0) throw new InvalidOperationException("Нет доступных серверов.");
        if (!File.Exists(CoreExecutable)) throw new FileNotFoundException("Не найдено сетевое ядро Mihomo.", CoreExecutable);
        if (settings.Tun && !IsAdmin()) throw new InvalidOperationException("Для TUN запусти ProstoKVN от имени администратора.");
        Stop();
        try
        {
        UserStore.EnsureHome();
        var chosen = nodes.FirstOrDefault(n => n.Name == settings.SelectedNode) ?? nodes[0];
        activeEngine = EngineSelector.Choose(chosen);
        OnLog?.Invoke("Автовыбор ядра: " + EngineSelector.Display(chosen));
        if (activeEngine == TunnelEngine.Xray) await xray.StartAsync(chosen.NativeXrayConfig!);
        if (activeEngine == TunnelEngine.SingBox) await singBox.StartAsync(chosen);
        lastHotPlan = settings.Mode == "direct" ? null : HotRuleProviders.Create(nodes, rules, settings);
        if (lastHotPlan != null) HotRuleProviders.Stage(lastHotPlan);
        var config = BuildConfig(nodes, rules, settings,
            useXraySidecar: activeEngine == TunnelEngine.Xray,
            useSingBoxSidecar: activeEngine == TunnelEngine.SingBox,
            hotRules: lastHotPlan != null);
        config["secret"] = secret;
        string initialConfig = YamlWriter.Serialize(config);
        File.WriteAllText(UserStore.ConfigPath, initialConfig);
        var sanity = Process.Start(new ProcessStartInfo(CoreExecutable)
        {
            Arguments = $"-t -f \"{UserStore.ConfigPath}\" -d \"{UserStore.Home}\"",
            CreateNoWindow = true, UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true
        }) ?? throw new InvalidOperationException("Не удалось проверить конфигурацию.");
        string error = await sanity.StandardError.ReadToEndAsync();
        string output = await sanity.StandardOutput.ReadToEndAsync();
        await sanity.WaitForExitAsync();
        if (sanity.ExitCode != 0) throw new InvalidOperationException("Проверка конфигурации Mihomo не пройдена. " + (error + output)[..Math.Min(700, (error + output).Length)]);
        process = new Process { StartInfo = new ProcessStartInfo(CoreExecutable)
        {
            Arguments = $"-f \"{UserStore.ConfigPath}\" -d \"{UserStore.Home}\"",
            WorkingDirectory = UserStore.Home,
            CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
        }, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => { if (e.Data != null) OnLog?.Invoke(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) OnLog?.Invoke(e.Data); };
        var startedProcess = process;
        process.Exited += (_, _) =>
        {
            // Ignore a late callback from a previous core after reconnect.
            if (!ReferenceEquals(process, startedProcess)) return;
            RestoreSystemProxy();
            OnLog?.Invoke("Ядро Mihomo завершилось.");
        };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await Task.Delay(1300);
        if (process.HasExited) throw new InvalidOperationException("Сетевое ядро завершилось при запуске — смотри журнал.");
        lastAppliedConfig = initialConfig;
        if (!settings.Tun && settings.Mode != "direct") EnableSystemProxy();
        OnLog?.Invoke("ProstoKVN запущен. Режим: " + settings.Mode + (settings.Tun ? " (TUN)" : " (системный прокси)"));
        }
        catch
        {
            // No orphan Xray / sing-box sidecar after config or startup failures.
            Stop();
            throw;
        }
    }
    public void Stop()
    {
        RestoreSystemProxy();
        try { if (IsRunning) { process!.Kill(entireProcessTree: true); process.WaitForExit(3000); } }
        catch { }
        finally { process?.Dispose(); process = null; xray.Stop(); singBox.Stop(); activeEngine = TunnelEngine.Mihomo; lastAppliedConfig = null; lastHotPlan = null; }
    }
    private void EnableSystemProxy()
    {
        SystemProxyRecovery.Enable(MainPort);
        proxyChanged = true;
    }
    private void RestoreSystemProxy()
    {
        if (!proxyChanged) return;
        try
        {
            SystemProxyRecovery.RestoreIfOwned(MainPort);
            proxyChanged = false;
        }
        catch (Exception ex)
        {
            // Preserve recovery state for the next start instead of discarding it.
            OnLog?.Invoke("Не удалось восстановить системный прокси: " + ex.GetType().Name);
        }
    }
    public static bool IsDiagnosticConnection(string? rule, string? payload) =>
        rule == "InName"
        && (payload ?? "").StartsWith("vega-probe-", StringComparison.Ordinal);
    public async Task<List<string>> ActiveDomainsAsync()
    {
        if (!IsRunning) return new();
        using var req = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{ApiPort}/connections");
        req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + secret);
        using var resp = await api.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        if (!doc.RootElement.TryGetProperty("connections", out var conns)) return new();
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var conn in conns.EnumerateArray())
        {
            // Do not let the learner rediscover its own DIRECT/VPN probes.
            if (conn.TryGetProperty("rule", out var rule)
                && conn.TryGetProperty("rulePayload", out var payload)
                && IsDiagnosticConnection(rule.GetString(), payload.GetString()))
                continue;
            if (!conn.TryGetProperty("metadata", out var meta)) continue;
            if (!meta.TryGetProperty("host", out var host)) continue;
            string domain = host.GetString() ?? "";
            if (IsDomainSafe(domain)) set.Add(domain.ToLowerInvariant());
        }
        return set.Take(128).ToList();
    }
    public async Task ReloadRoutesAsync(IReadOnlyList<ProxyNode> nodes, List<RouteRule> rules, AppSettings settings)
    {
        if (!IsRunning) return;
        // Only rule-provider files change. Mihomo's TUN, sockets and active
        // proxy connections stay alive; no PUT /configs?force=true on routine edits.
        var next = settings.Mode == "direct" ? null
            : HotRuleProviders.Create(nodes, rules, settings);
        var config = BuildConfig(nodes, rules, settings,
            useXraySidecar: activeEngine == TunnelEngine.Xray,
            useSingBoxSidecar: activeEngine == TunnelEngine.SingBox,
            hotRules: next != null);
        config["secret"] = secret;
        string payload = YamlWriter.Serialize(config);
        if (payload != lastAppliedConfig || lastHotPlan == null || next == null)
        {
            OnLog?.Invoke("Для структурного изменения требуется переподключение. Текущий VPN не прерываю.");
            return;
        }
        var changed = next.Files.Where(x => !lastHotPlan.Files.TryGetValue(x.Key, out string? previous)
                || previous != x.Value).ToList();
        if (changed.Count == 0) return;
        var applied = new List<string>();
        try
        {
            foreach (var (name, filePayload) in changed)
            {
                string path = Path.Combine(UserStore.Home, "hot-rules", name + ".yaml");
                HotRuleProviders.WriteAtomic(path, filePayload);
                using var request = new HttpRequestMessage(HttpMethod.Put,
                    $"http://127.0.0.1:{ApiPort}/providers/rules/{Uri.EscapeDataString(name)}");
                request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + secret);
                using var response = await api.SendAsync(request);
                response.EnsureSuccessStatusCode();
                applied.Add(name);
            }
            lastHotPlan = next;
            int moved = await CloseStaleConnectionsAsync(rules, settings.Mode);
            OnLog?.Invoke($"Горячее обновление {changed.Count} наборов правил без сброса VPN"
                + (moved > 0 ? $"; переподключено {moved} затронутых соединений." : "."));
        }
        catch
        {
            // Roll back files, including the one whose API update failed.
            foreach (var (name, _) in changed)
            {
                if (!lastHotPlan.Files.TryGetValue(name, out var oldPayload)) continue;
                HotRuleProviders.WriteAtomic(
                    Path.Combine(UserStore.Home, "hot-rules", name + ".yaml"), oldPayload);
                try
                {
                    using var rollback = new HttpRequestMessage(HttpMethod.Put,
                        $"http://127.0.0.1:{ApiPort}/providers/rules/{Uri.EscapeDataString(name)}");
                    rollback.Headers.TryAddWithoutValidation("Authorization", "Bearer " + secret);
                    using var response = await api.SendAsync(rollback);
                }
                catch { }
            }
            throw;
        }
    }

    public async Task<MonitorSnapshot> ReadMonitorAsync(ConnectionMetrics meter, bool measureDelay)
    {
        if (!IsTransportHealthy) throw new InvalidOperationException("Сетевое ядро недоступно.");
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"http://127.0.0.1:{ApiPort}/connections");
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + secret);
        using var response = await api.SendAsync(request);
        response.EnsureSuccessStatusCode();
        string raw = await response.Content.ReadAsStringAsync();
        int? latency = null;
        if (measureDelay)
        {
            try
            {
                string url = Uri.EscapeDataString("https://www.gstatic.com/generate_204");
                using var delayRequest = new HttpRequestMessage(HttpMethod.Get,
                    $"http://127.0.0.1:{ApiPort}/proxies/VEGA/delay?url={url}&timeout=3500");
                delayRequest.Headers.TryAddWithoutValidation("Authorization", "Bearer " + secret);
                using var delayResponse = await api.SendAsync(delayRequest);
                delayResponse.EnsureSuccessStatusCode();
                using var result = JsonDocument.Parse(await delayResponse.Content.ReadAsStringAsync());
                if (result.RootElement.TryGetProperty("delay", out var value)
                    && value.TryGetInt32(out var ms) && ms > 0) latency = ms;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
                or JsonException or InvalidOperationException) { }
        }
        return meter.Parse(raw, DateTime.UtcNow, latency);
    }

    private static bool DomainMatches(string host, string suffix) =>
        host.Equals(suffix, StringComparison.OrdinalIgnoreCase)
        || host.EndsWith("." + suffix, StringComparison.OrdinalIgnoreCase);

    public static bool IsStaleConnection(string host, string[] chains,
        IReadOnlyList<RouteRule> rules, string mode, string? destinationIp = null, string? processName = null)
    {
        if (chains.Length == 0 || mode == "direct") return false;
        // Manual IP/CIDR exceptions must take precedence even over the built-in
        // Telegram process and IP rules, including previously-opened connections.
        var effectiveRule = rules.Where(r => r.Manual
                && RouteTarget.TryParse(r.Domain, out var target)
                && target.Matches(host, destinationIp))
            .OrderByDescending(r => RulePriority(r.Domain))
            .FirstOrDefault();
        // Discord desktop WebRTC sessions have no DNS hostname; they must be
        // reconnected when routing changes from DIRECT to the process VPN rule.
        if (effectiveRule == null && DiscordRouting.IsDiscordProcess(processName))
            return chains.Any(x => x.Equals("DIRECT", StringComparison.OrdinalIgnoreCase));
        if (effectiveRule == null && mode == "smart")
        {
            effectiveRule = rules.Where(r => !r.Manual && (r.Route == "VEGA" || r.Route == "DIRECT")
                    && RouteTarget.TryParse(r.Domain, out var target)
                    && target.Matches(host, destinationIp))
                .OrderByDescending(r => RulePriority(r.Domain))
                .FirstOrDefault();
        }
        if (effectiveRule == null)
        {
            if (TelegramRouting.IsTelegramAddress(destinationIp)
                || TelegramRouting.IsTelegramAddress(host)
                || TelegramRouting.IsTelegramProcess(processName))
                return chains.Any(x => x.Equals("DIRECT", StringComparison.OrdinalIgnoreCase));
            return false;
        }

        string last = chains.Last();
        if (effectiveRule.Route == "DIRECT")
            return chains.Any(x => x.Equals("VEGA", StringComparison.OrdinalIgnoreCase))
                || !last.Equals("DIRECT", StringComparison.OrdinalIgnoreCase);
        if (effectiveRule.Route == "REJECT")
            return !last.Equals("REJECT", StringComparison.OrdinalIgnoreCase);
        if (effectiveRule.Route == "VEGA")
            return chains.Any(x => x.Equals("DIRECT", StringComparison.OrdinalIgnoreCase))
                || (chains.Length == 1 && last.Equals("REJECT", StringComparison.OrdinalIgnoreCase));
        return false;
    }

    private async Task<int> CloseStaleConnectionsAsync(
        IReadOnlyList<RouteRule> rules, string mode)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"http://127.0.0.1:{ApiPort}/connections");
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + secret);
        try
        {
            using var response = await api.SendAsync(request);
            response.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            if (!doc.RootElement.TryGetProperty("connections", out var connections)
                || connections.ValueKind != JsonValueKind.Array)
                return 0;
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var conn in connections.EnumerateArray())
            {
                if (!conn.TryGetProperty("id", out var connectionId)
                    || !conn.TryGetProperty("metadata", out var metadata)
                    || !conn.TryGetProperty("chains", out var chainData)
                    || chainData.ValueKind != JsonValueKind.Array)
                    continue;
                string id = connectionId.GetString() ?? "";
                string host = metadata.TryGetProperty("host", out var domain)
                    ? domain.GetString() ?? "" : "";
                string ip = metadata.TryGetProperty("destinationIP", out var address)
                    ? address.GetString() ?? "" : "";
                string owner = metadata.TryGetProperty("process", out var processName)
                    ? processName.GetString() ?? "" : "";
                if (!Guid.TryParse(id, out _)) continue;
                string[] chain = chainData.EnumerateArray().Select(x => x.GetString() ?? "").ToArray();
                if (IsStaleConnection(host, chain, rules, mode, ip, owner))
                    ids.Add(id);
            }
            int closed = 0;
            foreach (string id in ids.Take(60))
            {
                using var closeRequest = new HttpRequestMessage(HttpMethod.Delete,
                    $"http://127.0.0.1:{ApiPort}/connections/{id}");
                closeRequest.Headers.TryAddWithoutValidation("Authorization", "Bearer " + secret);
                using var closeResponse = await api.SendAsync(closeRequest);
                if (closeResponse.IsSuccessStatusCode) closed++;
            }
            return closed;
        }
        catch (Exception error)
        {
            OnLog?.Invoke("Не удалось переподключить старые соединения после изменения правил: "
                + error.GetType().Name);
            return 0;
        }
    }
    public void Dispose() { Stop(); xray.Dispose(); singBox.Dispose(); api.Dispose(); }
}