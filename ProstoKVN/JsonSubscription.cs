using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace ProstoKVN;

public static class JsonSubscription
{
    // Keep validated HTTPS; SubscriptionTransport selects the VPN-bound listener when available.
    private static JsonElement Prop(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? v : default;
    private static string Str(JsonElement e, string name, string fallback = "") { var v = Prop(e, name); return v.ValueKind is JsonValueKind.String or JsonValueKind.Number ? v.ToString() : fallback; }
    private static bool Has(JsonElement e, string name) => Prop(e, name).ValueKind != JsonValueKind.Undefined;
    private static string? Nonempty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
    public static async Task<List<ProxyNode>> LoadAsync(string url)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("JSON-подписка должна быть по HTTPS.");
        string response = await SubscriptionTransport.GetStringAsync(uri.AbsoluteUri);
        using var doc = JsonDocument.Parse(response);
        var root = doc.RootElement;
        IEnumerable<JsonElement> configurations = root.ValueKind == JsonValueKind.Array ? root.EnumerateArray().ToArray() : new[] { root };
        var list = new List<ProxyNode>();
        foreach (var config in configurations)
        {
            var outbounds = Prop(config, "outbounds");
            if (outbounds.ValueKind != JsonValueKind.Array) continue;
            var proxy = outbounds.EnumerateArray().FirstOrDefault(o => Str(o, "tag") == "proxy");
            if (proxy.ValueKind != JsonValueKind.Object) proxy = outbounds.EnumerateArray().FirstOrDefault(o => !new[] { "freedom", "blackhole", "dns" }.Contains(Str(o, "protocol")));
            if (proxy.ValueKind != JsonValueKind.Object) continue;
            var node = ConvertOutbound(config, proxy);
            if (node != null) { node.NativeXrayConfig = config.GetRawText(); list.Add(node); }
        }
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in list)
        {
            string original = node.Name;
            int index = 1;
            while (!used.Add(node.Name)) node.Name = original + " #" + ++index;
            node.Values["name"] = node.Name;
        }
        return list;
    }
    private static ProxyNode? ConvertOutbound(JsonElement config, JsonElement outbound)
    {
        var settings = Prop(outbound, "settings");
        var stream = Prop(outbound, "streamSettings");
        string protocol = Str(outbound, "protocol");
        string server = Str(settings, "address");
        int.TryParse(Str(settings, "port"), out int port);
        if (protocol == "hysteria") protocol = "hysteria2";
        if (protocol is not ("vless" or "vmess" or "trojan" or "shadowsocks" or "hysteria2")) return null;
        if (string.IsNullOrWhiteSpace(server) || port <= 0) return null;
        var name = Nonempty(Str(config, "remarks")) ?? protocol.ToUpperInvariant() + " " + server;
        var n = new ProxyNode { Name = name, Values = new() { ["name"] = name, ["type"] = protocol == "shadowsocks" ? "ss" : protocol, ["server"] = server, ["port"] = port } };
        if (protocol is "vless" or "vmess")
        {
            n.Values["uuid"] = Str(settings, "id");
            n.Values["udp"] = true;
            if (protocol == "vless")
            {
                n.Values["packet-encoding"] = "xudp";
                if (Nonempty(Str(settings, "flow")) is string flow) n.Values["flow"] = flow;
            }
            else { n.Values["alterId"] = 0; n.Values["cipher"] = "auto"; }
        }
        else if (protocol == "hysteria2")
        {
            var hy = Prop(stream, "hysteriaSettings");
            n.Values["password"] = Str(hy, "auth");
            var tls = Prop(stream, "tlsSettings");
            n.Values["sni"] = Str(tls, "serverName", server);
            var alpn = Prop(tls, "alpn");
            if (alpn.ValueKind == JsonValueKind.Array) n.Values["alpn"] = alpn.EnumerateArray().Select(x => x.ToString()).ToArray();
        }
        else if (protocol == "trojan") n.Values["password"] = Str(settings, "password");
        else if (protocol == "shadowsocks") { n.Values["cipher"] = Str(settings, "method"); n.Values["password"] = Str(settings, "password"); }
        string security = Str(stream, "security");
        if (security is "tls" or "reality" && protocol is "vless" or "vmess")
        {
            n.Values["tls"] = true;
            var tls = Prop(stream, security == "reality" ? "realitySettings" : "tlsSettings");
            if (Nonempty(Str(tls, "serverName")) is string sni) n.Values["servername"] = sni;
            if (Nonempty(Str(tls, "fingerprint")) is string fp) n.Values["client-fingerprint"] = fp;
            if (security == "reality")
            {
                var reality = new Dictionary<string, object?> { ["public-key"] = Str(tls, "publicKey"), ["short-id"] = Str(tls, "shortId") };
                if (Nonempty(Str(tls, "mldsa65Verify")) is string mldsa) reality["mldsa65-verify"] = mldsa;
                n.Values["reality-opts"] = reality;
            }
            else
            {
                var alpns = Prop(tls, "alpn");
                if (alpns.ValueKind == JsonValueKind.Array) n.Values["alpn"] = alpns.EnumerateArray().Select(x => x.ToString()).ToArray();
            }
        }
        string network = Str(stream, "network", "tcp");
        if (network is "ws" or "grpc" or "xhttp")
        {
            n.Values["network"] = network;
            if (network == "ws")
            {
                var w = Prop(stream, "wsSettings");
                var host = Str(w, "host");
                if (string.IsNullOrWhiteSpace(host)) host = Str(Prop(w, "headers"), "Host");
                n.Values["ws-opts"] = new Dictionary<string, object?> { ["path"] = Str(w, "path", "/"), ["headers"] = new Dictionary<string, string> { ["Host"] = host } };
            }
            else if (network == "grpc")
            {
                var g = Prop(stream, "grpcSettings");
                n.Values["grpc-opts"] = new Dictionary<string, object?> { ["grpc-service-name"] = Str(g, "serviceName") };
            }
            else
            {
                var x = Prop(stream, "xhttpSettings");
                var opt = new Dictionary<string, object?> { ["path"] = Str(x, "path", "/"), ["host"] = Str(x, "host"), ["mode"] = Str(x, "mode", "auto") };
                if (Has(x, "xPaddingBytes")) opt["x-padding-bytes"] = Str(x, "xPaddingBytes");
                n.Values["xhttp-opts"] = opt;
            }
        }
        return n;
    }
}