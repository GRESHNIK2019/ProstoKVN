using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using YamlDotNet.RepresentationModel;

namespace ProstoKVN;

/// <summary>
/// Parses subscriber-provided configurations as DATA, never as an executable
/// client config. No DNS, script, rule-provider or external include is loaded.
/// </summary>
public static class UniversalSubscriptionParser
{
    public sealed record Result(List<ProxyNode> Nodes, string Format);
    public const int MaxResponseChars = 2_500_000;
    public const int MaxNodes = 1500;
    private static readonly HashSet<string> Types = new(StringComparer.OrdinalIgnoreCase)
        { "vless", "vmess", "trojan", "ss", "shadowsocks", "hysteria2", "hy2", "tuic" };
    private static readonly HashSet<string> AllowedKeys = new(StringComparer.Ordinal)
    {
        "name", "type", "server", "port", "uuid", "password", "cipher", "alterId",
        "network", "udp", "tls", "sni", "servername", "server-name", "client-fingerprint",
        "fingerprint", "flow", "reality-opts", "ws-opts", "grpc-opts", "xhttp-opts",
        "h2-opts", "http-opts", "alpn", "obfs", "obfs-password", "auth-str",
        "up", "down", "congestion-controller", "skip-cert-verify", "udp-relay-mode",
        "packet-encoding", "plugin", "plugin-opts", "tfo", "ip-version",
        "ports", "ports-list", "hop-interval", "mport"
    };

    public static Result Parse(string body, string requestedFormat = "auto")
    {
        if (body.Length == 0 || body.Length > MaxResponseChars)
            throw new InvalidOperationException("Размер подписки недопустим.");
        string format = (requestedFormat ?? "auto").ToLowerInvariant().Trim();
        if (format is not ("auto" or "base64" or "mihomo" or "singbox" or "xray"))
            throw new InvalidOperationException("Неизвестный формат подписки.");

        if (format == "base64") return new(SubscriptionReader.ParseContent(body).Nodes, "Base64 / V2Ray");
        if (format == "mihomo") return new(ParseMihomo(body), "Mihomo YAML");
        if (format == "singbox") return new(ParseSingBox(body), "sing-box JSON");
        if (format == "xray") return new(JsonSubscription.ParseContent(body), "Xray JSON");

        string trimmed = body.TrimStart('\uFEFF', ' ', '\t', '\n', '\r');
        if (trimmed.StartsWith("{") || trimmed.StartsWith("["))
        {
            using var doc = JsonDocument.Parse(trimmed);
            JsonElement root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("proxies", out var proxies)
                && proxies.ValueKind == JsonValueKind.Array)
                return new(ParseMihomoJson(proxies), "Mihomo JSON");
            if (IsSingBox(root)) return new(ParseSingBox(trimmed), "sing-box JSON");
            return new(JsonSubscription.ParseContent(trimmed), "Xray JSON");
        }
        if (trimmed.StartsWith("proxies:", StringComparison.Ordinal)
            || trimmed.Contains("\nproxies:", StringComparison.Ordinal))
            return new(ParseMihomo(trimmed), "Mihomo YAML");
        return new(SubscriptionReader.ParseContent(body).Nodes, "Base64 / V2Ray");
    }

    private static bool IsSingBox(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("outbounds", out var array)
            || array.ValueKind != JsonValueKind.Array) return false;
        return array.EnumerateArray().Any(x => x.ValueKind == JsonValueKind.Object
            && x.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String);
    }
    private static string S(JsonElement node, string key, string fallback = "")
        => node.ValueKind == JsonValueKind.Object && node.TryGetProperty(key, out var v)
          && v.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
            ? v.ToString() : fallback;
    private static JsonElement P(JsonElement node, string key)
        => node.ValueKind == JsonValueKind.Object && node.TryGetProperty(key, out var v) ? v : default;

    private static object? ConvertJson(JsonElement element, int depth = 0)
    {
        if (depth > 8) return null;
        return element.ValueKind switch
        {
            JsonValueKind.Object => element.EnumerateObject().Take(80)
                .ToDictionary(x => x.Name, x => ConvertJson(x.Value, depth + 1)),
            JsonValueKind.Array => element.EnumerateArray().Take(80)
                .Select(x => ConvertJson(x, depth + 1)).ToArray(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number when element.TryGetInt64(out long number) => number,
            JsonValueKind.Number => element.GetDouble(),
            JsonValueKind.String => element.GetString(),
            _ => null
        };
    }

    private static List<ProxyNode> ParseSingBox(string body)
    {
        using var document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 32 });
        JsonElement root = document.RootElement;
        var outbounds = P(root, "outbounds");
        if (outbounds.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("Не найден массив outbounds sing-box.");
        var nodes = new List<ProxyNode>();
        foreach (var outbound in outbounds.EnumerateArray())
        {
            if (nodes.Count >= MaxNodes) break;
            string type = NormalizeType(S(outbound, "type"));
            if (!Types.Contains(type)) continue;
            string server = S(outbound, "server");
            if (!int.TryParse(S(outbound, "server_port"), out int port)
                || port is < 1 or > 65535 || string.IsNullOrWhiteSpace(server))
                continue;
            string name = S(outbound, "tag", type.ToUpperInvariant() + " " + server);
            var values = new Dictionary<string, object?>
            {
                ["name"] = name, ["type"] = type, ["server"] = server, ["port"] = port
            };
            void Set(string from, string to)
            {
                var value = P(outbound, from);
                if (value.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null))
                    values[to] = ConvertJson(value);
            }
            Set("uuid", "uuid");
            Set("password", "password");
            Set("method", "cipher");
            Set("alter_id", "alterId");
            Set("flow", "flow");
            Set("up_mbps", "up");
            Set("down_mbps", "down");
            Set("congestion_control", "congestion-controller");
            if (type is "vless" or "vmess") values["udp"] = true;
            if (type == "vmess" && !values.ContainsKey("cipher")) values["cipher"] = "auto";
            var tls = P(outbound, "tls");
            if (tls.ValueKind == JsonValueKind.Object
                && (!bool.TryParse(S(tls, "enabled"), out bool enabled) || enabled))
            {
                values["tls"] = true;
                if (!string.IsNullOrEmpty(S(tls, "server_name")))
                {
                    values["servername"] = S(tls, "server_name");
                    if (type is "hysteria2" or "trojan")
                        values["sni"] = S(tls, "server_name");
                }
                var alpn = P(tls, "alpn");
                if (alpn.ValueKind == JsonValueKind.Array)
                    values["alpn"] = ConvertJson(alpn);
                var utls = P(tls, "utls");
                if (S(utls, "enabled") == "True" || S(utls, "enabled") == "true")
                    values["client-fingerprint"] = S(utls, "fingerprint", "chrome");
                var reality = P(tls, "reality");
                if (S(reality, "enabled").Equals("true", StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(S(reality, "public_key")))
                    values["reality-opts"] = new Dictionary<string, object?>
                    {
                        ["public-key"] = S(reality, "public_key"),
                        ["short-id"] = S(reality, "short_id")
                    };
                if (S(tls, "insecure") == "True" || S(tls, "insecure") == "true")
                    values["skip-cert-verify"] = true;
            }
            var transport = P(outbound, "transport");
            string network = S(transport, "type");
            if (network is "ws" or "grpc" or "http" or "xhttp")
            {
                values["network"] = network;
                if (network == "ws")
                    values["ws-opts"] = new Dictionary<string, object?>
                    {
                        ["path"] = S(transport, "path", "/"),
                        ["headers"] = ConvertJson(P(transport, "headers")) ??
                            new Dictionary<string, object?>()
                    };
                if (network == "grpc")
                    values["grpc-opts"] = new Dictionary<string, object?> {
                        ["grpc-service-name"] = S(transport, "service_name")
                    };
                if (network == "xhttp")
                    values["xhttp-opts"] = new Dictionary<string, object?> {
                        ["path"] = S(transport, "path", "/"), ["host"] = S(transport, "host"),
                        ["mode"] = S(transport, "mode", "auto")
                    };
            }
            var obfs = P(outbound, "obfs");
            if (obfs.ValueKind == JsonValueKind.Object)
            {
                if (!string.IsNullOrEmpty(S(obfs, "type"))) values["obfs"] = S(obfs, "type");
                if (!string.IsNullOrEmpty(S(obfs, "password")))
                    values["obfs-password"] = S(obfs, "password");
            }
            if (type == "hysteria2" && !values.ContainsKey("password"))
                values["password"] = S(outbound, "password");
            nodes.Add(new ProxyNode { Name = name, Values = values });
        }
        if (nodes.Count == 0) throw new InvalidOperationException("В sing-box JSON нет совместимых серверов.");
        return nodes;
    }

    private static string NormalizeType(string type) => type.ToLowerInvariant() switch
    {
        "shadowsocks" => "ss",
        "hy2" => "hysteria2",
        _ => type.ToLowerInvariant()
    };

    private static List<ProxyNode> ParseMihomoJson(JsonElement proxies)
    {
        var nodes = new List<ProxyNode>();
        foreach (var item in proxies.EnumerateArray())
        {
            if (nodes.Count >= MaxNodes) break;
            var values = new Dictionary<string, object?>();
            if (item.ValueKind != JsonValueKind.Object) continue;
            foreach (var field in item.EnumerateObject())
                if (AllowedKeys.Contains(field.Name))
                    values[field.Name] = ConvertJson(field.Value);
            if (TryCreateMihomoNode(values, out var node)) nodes.Add(node!);
        }
        if (nodes.Count == 0) throw new InvalidOperationException("Нет совместимых прокси Mihomo.");
        return nodes;
    }

    private static List<ProxyNode> ParseMihomo(string body)
    {
        var yaml = new YamlStream();
        yaml.Load(new StringReader(body));
        if (yaml.Documents.Count != 1
            || yaml.Documents[0].RootNode is not YamlMappingNode root)
            throw new InvalidOperationException("Некорректный Mihomo YAML.");
        if (!root.Children.TryGetValue(new YamlScalarNode("proxies"), out var proxies)
            || proxies is not YamlSequenceNode list)
            throw new InvalidOperationException("Не найден список proxies в Mihomo YAML.");
        var nodes = new List<ProxyNode>();
        foreach (var entry in list.Children.Take(MaxNodes))
        {
            if (entry is not YamlMappingNode map) continue;
            var values = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var pair in map.Children)
            {
                if (pair.Key is not YamlScalarNode key
                    || key.Value == null || !AllowedKeys.Contains(key.Value)) continue;
                values[key.Value] = ConvertYaml(pair.Value);
            }
            if (TryCreateMihomoNode(values, out var node)) nodes.Add(node!);
        }
        if (nodes.Count == 0) throw new InvalidOperationException("Нет совместимых серверов Mihomo YAML.");
        return nodes;
    }

    private static object? ConvertYaml(YamlNode node, int depth = 0)
    {
        if (depth > 8) return null;
        if (node is YamlScalarNode s)
        {
            string value = s.Value ?? "";
            if (s.Style == YamlDotNet.Core.ScalarStyle.Plain)
            {
                if (bool.TryParse(value, out bool boolean)) return boolean;
                if (long.TryParse(value, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out long number))
                    return number;
            }
            return value;
        }
        if (node is YamlSequenceNode sequence)
            return sequence.Children.Take(80).Select(x => ConvertYaml(x, depth + 1)).ToArray();
        if (node is YamlMappingNode map)
            return map.Children.Take(80).Where(x => x.Key is YamlScalarNode)
                .ToDictionary(x => ((YamlScalarNode)x.Key).Value ?? "",
                    x => ConvertYaml(x.Value, depth + 1));
        return null;
    }

    private static bool TryCreateMihomoNode(Dictionary<string, object?> fields, out ProxyNode? node)
    {
        node = null;
        string Str(string key) => fields.TryGetValue(key, out var value)
            ? Convert.ToString(value) ?? "" : "";
        string type = NormalizeType(Str("type"));
        string server = Str("server");
        if (!Types.Contains(type)
            || string.IsNullOrWhiteSpace(server)
            || !int.TryParse(Str("port"), out int port) || port is < 1 or > 65535)
            return false;
        string name = Str("name");
        if (string.IsNullOrWhiteSpace(name)) name = type.ToUpperInvariant() + " " + server;
        fields["name"] = name;
        fields["type"] = type;
        fields["port"] = port;
        if (fields.TryGetValue("server-name", out var value)
            && !fields.ContainsKey("servername"))
            fields["servername"] = value;
        node = new ProxyNode { Name = name, Values = fields };
        return true;
    }
}
