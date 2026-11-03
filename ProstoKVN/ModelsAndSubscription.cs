using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace ProstoKVN;

public sealed class ProxyNode
{
    public string Name { get; set; } = "";
    public string? NativeXrayConfig { get; set; }
    public Dictionary<string, object?> Values { get; set; } = new();
    public override string ToString() => Name;
}
public sealed class RouteRule
{
    public string Domain { get; set; } = "";
    // Optional user-defined name, e.g. Telegram, Hetzner, work subnet.
    public string Label { get; set; } = "";
    [System.Text.Json.Serialization.JsonIgnore]
    public string ServiceName => ServiceRuleGrouper.ServiceName(Domain);
    public string Route { get; set; } = "DIRECT";
    public string PreferredNode { get; set; } = "";
    public bool Manual { get; set; }
    public bool Provisional { get; set; }
    public DateTime? ExpiresUtc { get; set; }
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    public int FailedDirect { get; set; }
    public override string ToString()
    {
        string kind = RouteTarget.TryParse(Domain, out var target) ? target.DisplayKind : "Домен";
        string route = Route == "VEGA" ? "СЕРВЕР" : Route == "REJECT" ? "BLOCK" : Route;
        string caption = string.IsNullOrWhiteSpace(Label) ? Domain : Label.Trim() + "  ·  " + Domain;
        return $"{(Manual ? "✎ Ручное" : Provisional ? "◷ AUTO (предположительно)" : "◉ AUTO")}  [{kind}] {caption}   →   {route}{(string.IsNullOrEmpty(PreferredNode) ? "" : " через " + PreferredNode)}   ({UpdatedUtc.ToLocalTime():dd.MM HH:mm})";
    }
}
public sealed class AppSettings
{
    public string Mode { get; set; } = "smart";
    public bool Tun { get; set; }
    public bool AutoLearn { get; set; } = true;
    public string SelectedNode { get; set; } = "";
    public string ProtectedUrl { get; set; } = "";
    public string Theme { get; set; } = "light";
}
public static class UserStore
{
    // An isolated data directory can be supplied for GUI smoke tests so
    // an already running VPN session never shares the test's routes.json.
    private static readonly string? TestHome =
        Environment.GetEnvironmentVariable("PROSTOKVN_TEST_HOME");

    public static readonly string Home = string.IsNullOrWhiteSpace(TestHome)
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ProstoKVN")
        : Path.GetFullPath(TestHome);

    // Retain this single legacy location only to import existing settings.
    private static readonly string LegacyHome = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VEGA VPN");

    static UserStore()
    {
        if (!string.IsNullOrWhiteSpace(TestHome)) return;
        // Never fail application startup if an old location is inaccessible.
        try { ImportLegacyInto(LegacyHome, Home); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    public static void ImportLegacyInto(string oldHome, string newHome)
    {
        if (!Directory.Exists(oldHome)) return;
        // Copy rather than move. The previous client / rollback can continue
        // using its original data. Never overwrite newer user settings.
        Directory.CreateDirectory(newHome);
        foreach (string name in new[] {
            "settings.json", "routes.json", "subscription-cache.json", "config.yaml"
        })
        {
            string source = Path.Combine(oldHome, name);
            string destination = Path.Combine(newHome, name);
            if (!File.Exists(source) || File.Exists(destination)) continue;
            try { File.Copy(source, destination, overwrite: false); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        string oldRules = Path.Combine(oldHome, "hot-rules");
        string newRules = Path.Combine(newHome, "hot-rules");
        if (!Directory.Exists(oldRules)) return;
        Directory.CreateDirectory(newRules);
        foreach (string file in Directory.EnumerateFiles(oldRules, "*.yaml"))
        {
            string destination = Path.Combine(newRules, Path.GetFileName(file));
            if (File.Exists(destination)) continue;
            try { File.Copy(file, destination, overwrite: false); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
    public static string ConfigPath => Path.Combine(Home, "config.yaml");
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    public static void EnsureHome() => Directory.CreateDirectory(Home);
    public static List<RouteRule> LoadRules()
    {
        try { return JsonSerializer.Deserialize<List<RouteRule>>(File.ReadAllText(Path.Combine(Home, "routes.json")), JsonOptions) ?? new(); }
        catch { return new(); }
    }
    public static void SaveRules(IEnumerable<RouteRule> rules)
    {
        EnsureHome();
        File.WriteAllText(Path.Combine(Home, "routes.json"), JsonSerializer.Serialize(rules.ToList(), JsonOptions));
    }
    // Keep an untouched copy before removing previously mislearned .ru rules.
    // This operation never deletes the source file or overwrites old backups.
    public static string BackupRoutesBeforeRuCleanup()
    {
        string source = Path.Combine(Home, "routes.json");
        if (!File.Exists(source)) return "";
        EnsureHome();
        string destination = Path.Combine(Home,
            $"routes.before-ru-cleanup-{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}.json");
        File.Copy(source, destination, overwrite: false);
        return destination;
    }
    public static string BackupRoutesBeforeServicePresets()
    {
        string source = Path.Combine(Home, "routes.json");
        if (!File.Exists(source)) return "";
        EnsureHome();
        string destination = Path.Combine(Home,
            $"routes.before-ai-presets-{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}.json");
        File.Copy(source, destination, overwrite: false);
        return destination;
    }
    public static int PruneExpiredRules(List<RouteRule> rules)
    {
        int expired = rules.RemoveAll(r => !r.Manual && r.Provisional
            && r.ExpiresUtc.HasValue && r.ExpiresUtc.Value <= DateTime.UtcNow);
        if (expired > 0) SaveRules(rules);
        return expired;
    }
    public static AppSettings LoadSettings()
    {
        try { return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Path.Combine(Home, "settings.json")), JsonOptions) ?? new(); }
        catch { return new(); }
    }
    public static void SaveSettings(AppSettings settings)
    {
        EnsureHome();
        File.WriteAllText(Path.Combine(Home, "settings.json"), JsonSerializer.Serialize(settings, JsonOptions));
    }
    public static string EncryptUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "";
        byte[] encrypted = ProtectedData.Protect(Encoding.UTF8.GetBytes(url), null, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(encrypted);
    }
    public static string DecryptUrl(string input)
    {
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(input), null, DataProtectionScope.CurrentUser)); }
        catch { return ""; }
    }
}

public static class SubscriptionReader
{
    // The same HTTPS routing path must be used for both /json/ and /sub/ feeds.
    public static async Task<(List<ProxyNode> Nodes, List<string> Skipped)> LoadAsync(string url)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var address) || address.Scheme != "https")
            throw new InvalidOperationException("Укажи действительную HTTPS-ссылку подписки.");
        string body = await SubscriptionTransport.GetStringAsync(address.AbsoluteUri);
        return ParseContent(body);
    }

    public static (List<ProxyNode> Nodes, List<string> Skipped) ParseContent(string response)
    {
        if (response.Length > UniversalSubscriptionParser.MaxResponseChars)
            throw new InvalidOperationException("Подписка слишком большая.");
        string body = response.Trim();
        if (!body.Contains("://", StringComparison.Ordinal))
        {
            try { body = Encoding.UTF8.GetString(Convert.FromBase64String(PadBase64(body))); }
            catch (FormatException) { throw new InvalidOperationException("Ответ не похож на поддерживаемую подписку /sub/."); }
        }
        var nodes = new List<ProxyNode>();
        var skipped = new List<string>();
        foreach (string line in body.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try { var node = ParseLink(line); if (node != null) nodes.Add(node); else skipped.Add("Неизвестный тип ссылки"); }
            catch (Exception ex) { skipped.Add(ex.Message); }
        }
        var used = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < nodes.Count; i++)
        {
            string name = nodes[i].Name;
            int n = 1;
            while (!used.Add(nodes[i].Name)) nodes[i].Name = name + " #" + (++n);
            nodes[i].Values["name"] = nodes[i].Name;
        }
        if (nodes.Count == 0) throw new InvalidOperationException("В подписке нет распознанных серверов. " + string.Join("; ", skipped.Take(3)));
        return (nodes, skipped);
    }
    static string PadBase64(string value)
    {
        string s = value.Trim().Replace('-', '+').Replace('_', '/').Replace("\r", "").Replace("\n", "");
        return s.PadRight(s.Length + (4 - s.Length % 4) % 4, '=');
    }
    static string Decode64(string s) => Encoding.UTF8.GetString(Convert.FromBase64String(PadBase64(s)));
    static Dictionary<string, string> Query(string query)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int i = part.IndexOf('=');
            string key = Uri.UnescapeDataString(i < 0 ? part : part[..i]);
            dict[key] = Uri.UnescapeDataString(i < 0 ? "" : part[(i + 1)..].Replace("+", "%20"));
        }
        return dict;
    }
    static string Get(Dictionary<string, string> d, string key, string def = "") => d.TryGetValue(key, out string? v) ? v : def;
    static ProxyNode? ParseLink(string link)
    {
        if (link.StartsWith("vpn://", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("AmneziaWG vpn://: требуется отдельный конвертер .conf (узел не импортирован)");
        if (link.StartsWith("vmess://", StringComparison.OrdinalIgnoreCase))
        {
            using var doc = JsonDocument.Parse(Decode64(link[8..]));
            var v = doc.RootElement;
            string Read(string k) => v.TryGetProperty(k, out var x) ? x.ToString() : "";
            var n = Node(Read("ps"), "vmess", Read("add"), int.Parse(Read("port")));
            n.Values["uuid"] = Read("id"); n.Values["alterId"] = int.TryParse(Read("aid"), out int aid) ? aid : 0;
            n.Values["cipher"] = string.IsNullOrWhiteSpace(Read("scy")) ? "auto" : Read("scy");
            string net = Read("net");
            if (!string.IsNullOrEmpty(net)) SetTransport(n, net, Read("path"), Read("host"), "", "");
            if (Read("tls") == "tls") { n.Values["tls"] = true; n.Values["servername"] = Read("sni"); }
            return n;
        }
        if (!Uri.TryCreate(link, UriKind.Absolute, out var u)) return null;
        var q = Query(u.Query);
        string title = Uri.UnescapeDataString(u.Fragment.TrimStart('#'));
        var scheme = u.Scheme.ToLowerInvariant();
        if (scheme is "vless" or "trojan" or "hysteria2" or "hy2" or "tuic")
        {
            string name = string.IsNullOrWhiteSpace(title) ? $"{scheme.ToUpperInvariant()} {u.Host}" : title;
            var n = Node(name, scheme is "hy2" ? "hysteria2" : scheme, u.Host, u.Port);
            string userInfo = Uri.UnescapeDataString(u.UserInfo);
            if (scheme == "vless")
            {
                n.Values["uuid"] = userInfo; n.Values["udp"] = true;
                n.Values["packet-encoding"] = "xudp";
                string sec = Get(q, "security");
                if (sec is "tls" or "reality") n.Values["tls"] = true;
                if (!string.IsNullOrEmpty(Get(q, "sni"))) n.Values["servername"] = Get(q, "sni");
                if (!string.IsNullOrEmpty(Get(q, "fp"))) n.Values["client-fingerprint"] = Get(q, "fp");
                if (!string.IsNullOrEmpty(Get(q, "flow"))) n.Values["flow"] = Get(q, "flow");
                if (sec == "reality")
                    n.Values["reality-opts"] = new Dictionary<string, object?> { ["public-key"] = Get(q, "pbk"), ["short-id"] = Get(q, "sid") };
                SetTransport(n, Get(q, "type", "tcp"), Get(q, "path"), Get(q, "host"), Get(q, "serviceName"), Get(q, "mode"));
                if (!string.IsNullOrEmpty(Get(q, "alpn"))) n.Values["alpn"] = Get(q, "alpn").Split(',');
            }
            else if (scheme == "trojan")
            {
                n.Values["password"] = userInfo; n.Values["udp"] = true;
                n.Values["sni"] = Get(q, "sni"); n.Values["skip-cert-verify"] = Get(q, "allowInsecure") == "1";
                SetTransport(n, Get(q, "type", "tcp"), Get(q, "path"), Get(q, "host"), Get(q, "serviceName"), Get(q, "mode"));
            }
            else if (scheme is "hysteria2" or "hy2")
            {
                n.Values["password"] = userInfo;
                n.Values["sni"] = Get(q, "sni", u.Host);
                n.Values["skip-cert-verify"] = Get(q, "insecure") == "1";
                if (!string.IsNullOrEmpty(Get(q, "obfs"))) n.Values["obfs"] = Get(q, "obfs");
                if (!string.IsNullOrEmpty(Get(q, "obfs-password"))) n.Values["obfs-password"] = Get(q, "obfs-password");
            }
            else if (scheme == "tuic")
            {
                var parts = userInfo.Split(':', 2);
                n.Values["uuid"] = parts[0]; n.Values["password"] = parts.Length > 1 ? parts[1] : "";
                n.Values["sni"] = Get(q, "sni", u.Host);
                n.Values["alpn"] = Get(q, "alpn", "h3").Split(',');
                n.Values["udp-relay-mode"] = Get(q, "udp_relay_mode", "native");
                n.Values["congestion-controller"] = Get(q, "congestion_control", "bbr");
            }
            return n;
        }
        if (scheme == "ss")
        {
            string text = link[5..].Split('#')[0].Split('?')[0];
            string titleSs = string.IsNullOrWhiteSpace(title) ? "Shadowsocks" : title;
            string method, pass, host; int port;
            if (!text.Contains('@')) text = Decode64(text);
            int at = text.LastIndexOf('@');
            if (at < 0) throw new FormatException("Shadowsocks: отсутствует адрес");
            string credentials = Uri.UnescapeDataString(text[..at]);
            if (!credentials.Contains(':')) credentials = Decode64(credentials);
            int colon = credentials.IndexOf(':');
            method = credentials[..colon]; pass = credentials[(colon + 1)..];
            var addr = new Uri("ss://" + text[(at + 1)..]);
            host = addr.Host; port = addr.Port;
            var n = Node(titleSs, "ss", host, port);
            n.Values["cipher"] = method; n.Values["password"] = pass; n.Values["udp"] = true;
            return n;
        }
        return null;
    }
    static ProxyNode Node(string name, string type, string host, int port)
    {
        if (string.IsNullOrWhiteSpace(host) || port is < 1 or > 65535) throw new FormatException("В ссылке некорректный host/port");
        string label = string.IsNullOrWhiteSpace(name) ? $"{type} {host}" : name;
        return new ProxyNode { Name = label, Values = new() { ["name"] = label, ["type"] = type, ["server"] = host, ["port"] = port } };
    }
    static void SetTransport(ProxyNode n, string network, string path, string host, string service, string mode)
    {
        if (network == "tcp" || string.IsNullOrEmpty(network)) return;
        n.Values["network"] = network;
        if (network == "ws") n.Values["ws-opts"] = new Dictionary<string, object?> { ["path"] = string.IsNullOrWhiteSpace(path) ? "/" : path, ["headers"] = new Dictionary<string, string> { ["Host"] = host } };
        if (network == "grpc") n.Values["grpc-opts"] = new Dictionary<string, object?> { ["grpc-service-name"] = service };
        if (network == "xhttp") n.Values["xhttp-opts"] = new Dictionary<string, object?> { ["path"] = string.IsNullOrWhiteSpace(path) ? "/" : path, ["host"] = host, ["mode"] = string.IsNullOrWhiteSpace(mode) ? "auto" : mode };
    }
}