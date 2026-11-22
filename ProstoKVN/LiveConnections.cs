using System.Globalization;
using System.IO;
using System.Text.Json;

namespace ProstoKVN;

public sealed record LiveConnection(string Id, string Process, string Host, string Ip, string Port,
    string Network, string Route, string Rule, long UpRate, long DownRate)
{
    public string Target => (Host.Length > 0 ? Host : Ip) + (Port.Length > 0 ? ":" + Port : "");
    public string Owner => (Process.Length > 0 ? Process : "Процесс неизвестен") + " · " + Network;
    public string Address => Host.Length > 0 && Host != Ip && Ip.Length > 0 ? Ip : "";
    public string Speed => $"↑ {FormatRate(UpRate)}  ↓ {FormatRate(DownRate)}";
    private static string FormatRate(long value) => value < 0 ? "—" : value >= 1048576
        ? $"{value / 1048576.0:F1} МБ/с" : $"{value / 1024.0:F1} КБ/с";
}

// A sample contains only active connections. Never infer an outbound from
// the selected UI mode: the actual Mihomo chain is authoritative.
public sealed class LiveConnectionMonitor
{
    private Dictionary<string, (long Up, long Down)> previous = new();
    private DateTime? previousAt;
    public void Reset() { previous.Clear(); previousAt = null; }
    public IReadOnlyList<LiveConnection> Read(string json, DateTime now,
        IReadOnlyDictionary<string, string>? providers = null, IEnumerable<string>? vpnNames = null)
    {
        using var doc = JsonDocument.Parse(json);
        var rows = new List<LiveConnection>();
        var next = new Dictionary<string, (long Up, long Down)>();
        var known = (vpnNames ?? Array.Empty<string>()).Append("VEGA").ToHashSet(StringComparer.OrdinalIgnoreCase);
        double elapsed = previousAt.HasValue ? (now - previousAt.Value).TotalSeconds : 0;
        if (!doc.RootElement.TryGetProperty("connections", out var connections)
            || connections.ValueKind is not (JsonValueKind.Array or JsonValueKind.Null))
            throw new JsonException("Список соединений отсутствует.");
        IEnumerable<JsonElement> entries = connections.ValueKind == JsonValueKind.Array
            ? connections.EnumerateArray() : Array.Empty<JsonElement>();
        foreach (var item in entries)
        {
            if (!item.TryGetProperty("metadata", out var meta)) continue;
            string rule = Text(item, "rule"), payload = Text(item, "rulePayload");
            string owner = Text(meta, "process"), path = Text(meta, "processPath");
            string filename = Path.GetFileName((path.Length > 0 ? path : owner).Replace('/', '\\'));
            if (VpnCore.IsDiagnosticConnection(rule, payload) || filename.Equals("mihomo.exe", StringComparison.OrdinalIgnoreCase)
                || filename.Equals("xray.exe", StringComparison.OrdinalIgnoreCase) || filename.Equals("sing-box.exe", StringComparison.OrdinalIgnoreCase)) continue;
            string id = Text(item, "id");
            if (id.Length == 0) continue;
            string host = Text(meta, "host"), ip = Text(meta, "destinationIP");
            string[] chains = item.TryGetProperty("chains", out var c) && c.ValueKind == JsonValueKind.Array
                ? c.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString() ?? "").ToArray() : Array.Empty<string>();
            bool direct = chains.Contains("DIRECT", StringComparer.OrdinalIgnoreCase);
            bool reject = chains.Contains("REJECT", StringComparer.OrdinalIgnoreCase);
            bool vpn = chains.Any(known.Contains);
            string route = (direct ? 1 : 0) + (reject ? 1 : 0) + (vpn ? 1 : 0) != 1
                ? "Неизвестно" : direct ? "DIRECT" : reject ? "Блокировка" : "VPN";
            long up = Number(item, "upload"), down = Number(item, "download");
            next[id] = (up, down);
            long upRate = -1, downRate = -1;
            if (elapsed > 0 && previous.TryGetValue(id, out var before))
            {
                upRate = (long)(Math.Max(0, up - before.Up) / elapsed);
                downRate = (long)(Math.Max(0, down - before.Down) / elapsed);
            }
            string description = rule == "Match" ? "По умолчанию" : rule + (payload.Length > 0 ? ": " + payload : "");
            if (rule == "RuleSet" && providers != null && providers.TryGetValue(payload, out var file))
            {
                foreach (string line in file.Split('\n'))
                {
                    string pattern = line.Trim();
                    if (!pattern.StartsWith("- '", StringComparison.Ordinal) || !pattern.EndsWith('\'')) continue;
                    var parts = pattern[3..^1].Replace("''", "'").Split(',');
                    if (parts.Length < 2) continue;
                    string value = parts[0] switch { "DOMAIN" => "domain-exact:" + parts[1],
                        "PROCESS-NAME" => "process-name:" + parts[1], "PROCESS-PATH" => "process-path:" + parts[1], _ => parts[1] };
                    if (RouteTarget.TryParse(value, out var target) && target.Matches(host, ip, owner, path))
                    { description = target.DisplayKind + ": " + target.DisplayValue; break; }
                }
            }
            rows.Add(new(id, owner.Length > 0 ? owner : filename, host, ip, Text(meta,"destinationPort"),
                Text(meta,"network").ToUpperInvariant(), route, description, upRate, downRate));
        }
        previous = next; previousAt = now;
        return rows.OrderBy(x => x.Process, StringComparer.CurrentCultureIgnoreCase).ThenBy(x => x.Target, StringComparer.OrdinalIgnoreCase).ToArray();
    }
    private static string Text(JsonElement e, string key) => e.TryGetProperty(key, out var v)
        ? v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ValueKind == JsonValueKind.Number ? v.GetRawText() : "" : "";
    private static long Number(JsonElement e, string key) => e.TryGetProperty(key, out var v) && v.TryGetInt64(out long n) ? n : 0;
}
