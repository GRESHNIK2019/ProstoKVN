using System.Net;

namespace ProstoKVN;

// One selection and precedence policy for startup, live providers and open flows.
public static class RoutingPolicy
{
    public static string NormalizeMode(string? mode) => mode is "vpn" or "direct" or "apps" or "mine" ? mode : "smart";
    public static bool RequiresTun(string mode) => mode is "mine" or "apps";
    public static AppSettings Snapshot(AppSettings s) => new() {
        Mode = NormalizeMode(s.Mode), Tun = s.Tun || RequiresTun(s.Mode),
        TunStack = VpnCore.NormalizeTunStack(s.TunStack), TunDnsHijack = s.TunDnsHijack,
        TunStrictRoute = s.TunStrictRoute, AutoLearn = s.AutoLearn,
        SelectedNode = s.SelectedNode, ProtectedUrl = s.ProtectedUrl, Theme = s.Theme
    };
    public static bool IsCurrent(RouteRule rule) => rule.Manual || !rule.ExpiresUtc.HasValue || rule.ExpiresUtc > DateTime.UtcNow;
    public static bool IsTunBypass(RouteRule rule) => rule.Manual && rule.BypassTun && rule.Route == "DIRECT"
        && RouteTarget.TryParse(rule.Domain, out var target) && target.IsAddress;
    public static string[] TunExclusions(IEnumerable<RouteRule> rules, string mode)
        => Select(rules, mode, true).Where(IsTunBypass)
            .Select(r => { RouteTarget.TryParse(r.Domain, out var target); return target.MihomoValue; })
            .Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray();
    public static IEnumerable<RouteRule> Select(IEnumerable<RouteRule> rules, string mode, bool manual)
    {
        mode = NormalizeMode(mode);
        if (mode == "direct") return Enumerable.Empty<RouteRule>();
        return rules.Where(r => IsCurrent(r) && RouteTarget.TryParse(r.Domain, out _)
                && (r.Route is "DIRECT" or "VEGA" or "REJECT")
                && (manual ? (mode == "mine" ? r.IncludedInMine : r.Manual)
                    : mode == "smart" && !r.Manual && (r.Route is "DIRECT" or "VEGA")
                        && !RuDomainPolicy.IsRuDomain(r.Domain)))
            .OrderBy(r => IsTunBypass(r) ? -1 : string.IsNullOrEmpty(r.Preset) ? 0 : 1)
            .ThenByDescending(r => { RouteTarget.TryParse(r.Domain, out var t); return t.Priority; })
            .ThenByDescending(r => r.UpdatedUtc);
    }
    public static IEnumerable<string> DiscordRules(string mode) => NormalizeMode(mode) switch
    {
        "mine" or "direct" => Enumerable.Empty<string>(),
        "apps" => DiscordRouting.BuildRules().Where(r => r.StartsWith("PROCESS-NAME,")),
        _ => DiscordRouting.BuildRules()
    };
    public static string NodeTarget(IReadOnlyList<ProxyNode> nodes, int index)
    {
        string name = nodes[index].Name;
        if (!name.Contains(',') && name.IndexOfAny(new[] {'\r', '\n'}) < 0) return name;
        string alias = "__prostokvn-route-" + index;
        while (nodes.Any(n => n.Name == alias)) alias += "-";
        return alias;
    }
    public static string Target(RouteRule rule, IReadOnlyList<ProxyNode> nodes, AppSettings settings)
    {
        if (rule.Route != "VEGA") return rule.Route == "REJECT" ? "REJECT" : "DIRECT";
        var chosen = nodes.FirstOrDefault(n => n.Name == settings.SelectedNode) ?? nodes.FirstOrDefault();
        for (int i = 0; i < nodes.Count; i++)
            if (nodes[i].Name == rule.PreferredNode
                && (nodes[i] == chosen || EngineSelector.Choose(nodes[i]) != TunnelEngine.Xray))
                return NodeTarget(nodes, i);
        return "VEGA";
    }
    public static string ExpectedRoute(IReadOnlyList<RouteRule> rules, string mode, string host,
        string? ip = null, string? process = null, string? path = null,
        IReadOnlyList<ProxyNode>? nodes = null, AppSettings? settings = null)
    {
        mode = NormalizeMode(mode);
        string owner = !string.IsNullOrEmpty(path) ? path : process ?? "";
        string filename = System.IO.Path.GetFileName(owner.Replace('/', '\\'));
        if (filename.Equals("xray.exe", StringComparison.OrdinalIgnoreCase)
            || filename.Equals("sing-box.exe", StringComparison.OrdinalIgnoreCase)
            || filename.Equals("mihomo.exe", StringComparison.OrdinalIgnoreCase)) return "DIRECT";
        if (IPAddress.TryParse(string.IsNullOrWhiteSpace(ip) ? host : ip, out var address) && address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            if (b[0] == 127 || b[0] == 10 || b[0] == 192 && b[1] == 168
                || b[0] == 172 && b[1] >= 16 && b[1] <= 31 || b[0] == 169 && b[1] == 254) return "DIRECT";
        }
        RouteRule? Match(bool manual) => Select(rules, mode, manual).FirstOrDefault(r =>
            RouteTarget.TryParse(r.Domain, out var t) && t.Matches(host, ip, process, path));
        string Destination(RouteRule r) => nodes != null && settings != null ? Target(r, nodes, settings)
            : r.Route == "VEGA" && !string.IsNullOrWhiteSpace(r.PreferredNode) ? r.PreferredNode : r.Route;
        var selected = Match(true);
        if (selected != null) return Destination(selected);
        if (DiscordRules(mode).Any() && (DiscordRouting.IsDiscordProcess(process) || DiscordRouting.IsDiscordProcess(path)
            || mode != "apps" && DiscordRouting.IsDiscordDomain(host))) return "VEGA";
        selected = Match(false);
        if (selected != null) return Destination(selected);
        if (mode == "smart" && AiServiceRouting.Matches(host)) return "VEGA";
        if (mode == "smart" && RuDomainPolicy.IsRuDomain(host)) return "DIRECT";
        if (mode is "smart" or "vpn" && (TelegramRouting.IsTelegramAddress(ip)
            || TelegramRouting.IsTelegramAddress(host) || TelegramRouting.IsTelegramProcess(process)
            || TelegramRouting.IsTelegramProcess(path) || TelegramRouting.Domains.Any(d =>
                host.Equals(d, StringComparison.OrdinalIgnoreCase) || host.EndsWith("." + d, StringComparison.OrdinalIgnoreCase)))) return "VEGA";
        return mode == "vpn" ? "VEGA" : "DIRECT";
    }
}

public enum RouteReloadResult { Applied, Unchanged, ReconnectRequired, Stopped }
