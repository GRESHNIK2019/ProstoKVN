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
using System.Threading;
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
    private readonly int controllerPort;
    public VpnCore(int controllerPort = ApiPort, HttpMessageHandler? apiHandler = null)
    {
        if (controllerPort is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(controllerPort));
        this.controllerPort = controllerPort;
        api = new HttpClient(apiHandler ?? new HttpClientHandler()) { Timeout = TimeSpan.FromSeconds(7) };
        xray.OnLog += line => OnLog?.Invoke(line);
        singBox.OnLog += line => OnLog?.Invoke(line);
    }
    private readonly HttpClient api;
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
    public static string NormalizeTunStack(string? stack)
        => stack is "system" or "gvisor" ? stack : "mixed";

    public static Dictionary<string, object?> BuildConfig(IReadOnlyList<ProxyNode> nodes, IEnumerable<RouteRule> routeRules, AppSettings settings, bool useXraySidecar = false, bool useSingBoxSidecar = false, bool hotRules = false, HotRulePlan? preparedHotRules = null)
    {
        if (nodes.Count == 0) throw new InvalidOperationException("Нет доступных серверов.");
        settings = RoutingPolicy.Snapshot(settings);
        routeRules = routeRules.ToList();
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
        // Mihomo rules use comma-delimited fields, so a user-supplied proxy
        // name (e.g. "Netherlands, Amsterdam") cannot be a rule target.
        // Route each diagnostic listener through a safe single-node group.
        // Keep the original display names unchanged in the proxy list.
        var reservedGroupNames = new HashSet<string>(nodes.Select(n => n.Name),
            StringComparer.Ordinal) { "VEGA" };
        var probeGroups = nodes.Take(32).Select((_, index) =>
        {
            string alias = "__prostokvn-probe-" + index;
            while (!reservedGroupNames.Add(alias)) alias += "-";
            return alias;
        }).ToArray();
        for (int i = 0; i < probeGroups.Length; i++)
            rules.Add($"IN-NAME,vega-probe-node-{i},{probeGroups[i]}");
        var chosenNode = nodes.FirstOrDefault(x => x.Name == settings.SelectedNode) ?? nodes[0];
        // IPv6 traffic is enabled only if the user explicitly adds an IPv6
        // address/network exception. Existing IPv4-only installations stay unchanged.
        bool enableIpv6 = RoutingPolicy.Select(routeRules, settings.Mode, true).Any(r =>
            RouteTarget.TryParse(r.Domain, out var t)
            && t.Kind is RouteTargetKind.IPv6 or RouteTargetKind.IPv6Subnet);
        var hotPlan = hotRules && settings.Mode != "direct"
            ? preparedHotRules ?? HotRuleProviders.Create(nodes, routeRules.ToList(), settings) : null;
        if (hotPlan != null)
        {
            rules.AddRange(hotPlan.Rules.Take(hotPlan.ManualRuleCount));
            rules.AddRange(RoutingPolicy.DiscordRules(settings.Mode));
            rules.AddRange(hotPlan.Rules.Skip(hotPlan.ManualRuleCount));
        }
        else if (settings.Mode != "direct")
        {
            // User exceptions always precede auto-learning, Telegram networks and MATCH.
            // Exact addresses precede CIDRs; more-specific networks precede broad ones.
            var manualRules = RoutingPolicy.Select(routeRules, settings.Mode, manual: true);
            var autoRules = RoutingPolicy.Select(routeRules, settings.Mode, manual: false);
            void AddUserRules(IEnumerable<RouteRule> source)
            {
                foreach (var r in source)
                {
                    if (!RouteTarget.TryParse(r.Domain, out var routeTarget)) continue;
                    string target = RoutingPolicy.Target(r, nodes, settings);
                    rules.Add(routeTarget.ToRule(target));
                }
            }
            // Explicit user overrides win. Then match Discord's desktop process so
            // IP-only WebRTC voice/video UDP uses VPN instead of smart-mode DIRECT.
            // Auto-learned HTTP domain routes cannot override this media policy.
            AddUserRules(manualRules);
            rules.AddRange(RoutingPolicy.DiscordRules(settings.Mode));
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
        if (settings.Mode is "smart" or "vpn") rules.AddRange(TelegramRouting.BuildRules());
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
                ["enable"] = settings.Tun || RoutingPolicy.RequiresTun(settings.Mode),
                ["stack"] = NormalizeTunStack(settings.TunStack),
                ["auto-route"] = settings.Tun || RoutingPolicy.RequiresTun(settings.Mode),
                ["auto-detect-interface"] = true,
                ["dns-hijack"] = settings.TunDnsHijack ? new[] { "any:53" } : Array.Empty<string>(),
                ["strict-route"] = settings.TunStrictRoute
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
            }.Concat(probeGroups.Select((alias, index) => (object)new Dictionary<string, object?>
            {
                ["name"] = alias, ["type"] = "select", ["proxies"] = new[] { nodes[index].Name }
            })).Concat(nodes.Select((node, index) => (node, index))
                .Where(x => RoutingPolicy.NodeTarget(nodes, x.index) != x.node.Name)
                .Select(x => (object)new Dictionary<string, object?> {
                    ["name"] = RoutingPolicy.NodeTarget(nodes, x.index), ["type"] = "select",
                    ["proxies"] = new[] { x.node.Name }
                })).ToArray(),
            ["rules"] = rules
        };
        if (hotPlan != null)
            result["rule-providers"] = hotPlan.Providers;
        return result;
    }
    public async Task StartAsync(IReadOnlyList<ProxyNode> nodes, List<RouteRule> rules, AppSettings settings)
    {
        settings = RoutingPolicy.Snapshot(settings);
        rules = rules.Select(r => r.Snapshot()).ToList();
        if (nodes.Count == 0) throw new InvalidOperationException("Нет доступных серверов.");
        if (!File.Exists(CoreExecutable)) throw new FileNotFoundException("Не найдено сетевое ядро Mihomo.", CoreExecutable);
        if ((settings.Tun || RoutingPolicy.RequiresTun(settings.Mode)) && !IsAdmin()) throw new InvalidOperationException("Для TUN запусти ProstoKVN от имени администратора.");
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
            hotRules: lastHotPlan != null, preparedHotRules: lastHotPlan);
        config["secret"] = secret;
        config["external-controller"] = $"127.0.0.1:{controllerPort}";
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
        if (!settings.Tun && !RoutingPolicy.RequiresTun(settings.Mode) && settings.Mode != "direct") EnableSystemProxy();
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
        using var req = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{controllerPort}/connections");
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
    public async Task<RouteReloadResult> ReloadRoutesAsync(IReadOnlyList<ProxyNode> nodes, List<RouteRule> rules, AppSettings settings)
    {
        if (!IsRunning) return RouteReloadResult.Stopped;
        settings = RoutingPolicy.Snapshot(settings);
        rules = rules.Select(r => r.Snapshot()).ToList();
        if (settings.Mode == "direct" && lastHotPlan == null) return RouteReloadResult.Unchanged;
        // Only rule-provider files change. Mihomo's TUN, sockets and active
        // proxy connections stay alive; no PUT /configs?force=true on routine edits.
        var next = settings.Mode == "direct" ? null
            : HotRuleProviders.Create(nodes, rules, settings, previousPlan: lastHotPlan);
        var config = BuildConfig(nodes, rules, settings,
            useXraySidecar: activeEngine == TunnelEngine.Xray,
            useSingBoxSidecar: activeEngine == TunnelEngine.SingBox,
            hotRules: next != null, preparedHotRules: next);
        config["secret"] = secret;
        config["external-controller"] = $"127.0.0.1:{controllerPort}";
        string payload = YamlWriter.Serialize(config);
        if (payload != lastAppliedConfig || lastHotPlan == null || next == null)
        {
            OnLog?.Invoke("Для структурного изменения требуется переподключение. Текущий VPN не прерываю.");
            return RouteReloadResult.ReconnectRequired;
        }
        var changed = next.Files.Where(x => !lastHotPlan.Files.TryGetValue(x.Key, out string? previous)
                || previous != x.Value).ToList();
        if (changed.Count == 0) return RouteReloadResult.Unchanged;
        try
        {
            foreach (var (name, filePayload) in changed)
            {
                string path = Path.Combine(UserStore.Home, "hot-rules", name + ".yaml");
                HotRuleProviders.WriteAtomic(path, filePayload);
                using var request = new HttpRequestMessage(HttpMethod.Put,
                    $"http://127.0.0.1:{controllerPort}/providers/rules/{Uri.EscapeDataString(name)}");
                request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + secret);
                using var response = await api.SendAsync(request);
                response.EnsureSuccessStatusCode();
            }
            int moved = await CloseStaleConnectionsAsync(nodes, rules, settings);
            OnLog?.Invoke($"Горячее обновление {changed.Count} наборов правил без сброса VPN"
                + (moved > 0 ? $"; переподключено {moved} затронутых соединений." : "."));
            lastHotPlan = next;
            return RouteReloadResult.Applied;
        }
        catch (Exception applyError)
        {
            bool rollbackFailed = false;
            foreach (var (name, _) in changed)
            {
                if (!lastHotPlan.Files.TryGetValue(name, out var oldPayload)) continue;
                try
                {
                    HotRuleProviders.WriteAtomic(Path.Combine(UserStore.Home, "hot-rules", name + ".yaml"), oldPayload);
                    using var rollback = new HttpRequestMessage(HttpMethod.Put,
                        $"http://127.0.0.1:{controllerPort}/providers/rules/{Uri.EscapeDataString(name)}");
                    rollback.Headers.TryAddWithoutValidation("Authorization", "Bearer " + secret);
                    using var response = await api.SendAsync(rollback);
                    response.EnsureSuccessStatusCode();
                }
                catch { rollbackFailed = true; }
            }
            if (rollbackFailed)
            {
                // Never report a later no-op as success after a partial rollback.
                lastAppliedConfig = null;
                throw new InvalidOperationException("Не удалось полностью восстановить прежние правила. Требуется переподключение.", applyError);
            }
            throw;
        }
    }

    public async Task<MonitorSnapshot> ReadMonitorAsync(ConnectionMetrics meter, bool measureDelay)
    {
        if (!IsTransportHealthy) throw new InvalidOperationException("Сетевое ядро недоступно.");
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"http://127.0.0.1:{controllerPort}/connections");
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
                    $"http://127.0.0.1:{controllerPort}/proxies/VEGA/delay?url={url}&timeout=3500");
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

    // On-demand quality probe, separate from recovery health checks. Uses
    // Mihomo's HTTPS delay endpoint via VEGA and does not measure Discord UDP.
    public async Task<int?> ProbeVpnHttpsDelayAsync(CancellationToken cancellation = default)
    {
        if (!IsTransportHealthy) return null;
        try
        {
            string url = Uri.EscapeDataString("https://www.gstatic.com/generate_204");
            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"http://127.0.0.1:{controllerPort}/proxies/VEGA/delay?url={url}&timeout=3500");
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + secret);
            using var response = await api.SendAsync(request, cancellation);
            response.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellation));
            return json.RootElement.TryGetProperty("delay", out var value)
                && value.TryGetInt32(out var ms) && ms > 0 ? ms : null;
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    public static bool IsStaleConnection(string host, string[] chains,
        IReadOnlyList<RouteRule> rules, string mode, string? destinationIp = null, string? processName = null,
        string? processPath = null, IReadOnlyList<ProxyNode>? nodes = null, AppSettings? settings = null)
    {
        if (chains.Length == 0) return false;
        string expected = RoutingPolicy.ExpectedRoute(rules, mode, host, destinationIp, processName, processPath, nodes, settings);
        if (expected == "VEGA")
            return chains.Any(x => x.Equals("DIRECT", StringComparison.OrdinalIgnoreCase)
                || x.Equals("REJECT", StringComparison.OrdinalIgnoreCase));
        return !chains.Any(x => x.Equals(expected, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<int> CloseStaleConnectionsAsync(
        IReadOnlyList<ProxyNode> nodes, IReadOnlyList<RouteRule> rules, AppSettings settings)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"http://127.0.0.1:{controllerPort}/connections");
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
                if (conn.TryGetProperty("rule", out var ruleType)
                    && conn.TryGetProperty("rulePayload", out var rulePayload)
                    && IsDiagnosticConnection(ruleType.GetString(), rulePayload.GetString())) continue;
                string id = connectionId.GetString() ?? "";
                string host = metadata.TryGetProperty("host", out var domain)
                    ? domain.GetString() ?? "" : "";
                string ip = metadata.TryGetProperty("destinationIP", out var address)
                    ? address.GetString() ?? "" : "";
                string owner = metadata.TryGetProperty("process", out var processName)
                    ? processName.GetString() ?? "" : "";
                if (!Guid.TryParse(id, out _)) continue;
                string[] chain = chainData.EnumerateArray().Select(x => x.GetString() ?? "").ToArray();
                string ownerPath = metadata.TryGetProperty("processPath", out var path)
                    ? path.GetString() ?? "" : "";
                if (IsStaleConnection(host, chain, rules, settings.Mode, ip, owner, ownerPath, nodes, settings))
                    ids.Add(id);
            }
            int closed = 0, failed = 0;
            await Parallel.ForEachAsync(ids, new ParallelOptions { MaxDegreeOfParallelism = 8 }, async (id, cancellation) =>
            {
                try
                {
                    using var closeRequest = new HttpRequestMessage(HttpMethod.Delete,
                        $"http://127.0.0.1:{controllerPort}/connections/{id}");
                    closeRequest.Headers.TryAddWithoutValidation("Authorization", "Bearer " + secret);
                    using var closeResponse = await api.SendAsync(closeRequest, cancellation);
                    if (closeResponse.IsSuccessStatusCode) Interlocked.Increment(ref closed);
                    else Interlocked.Increment(ref failed);
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
                { Interlocked.Increment(ref failed); }
            });
            if (failed > 0) OnLog?.Invoke($"Не удалось закрыть {failed} старых соединений. Перезапустите затронутые приложения.");
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
