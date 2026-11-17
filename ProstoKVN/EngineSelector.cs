using System;
using System.Collections.Generic;
using System.Text.Json;

namespace ProstoKVN;

public enum TunnelEngine { Mihomo, SingBox, Xray }

public static class EngineSelector
{
    private static string Value(ProxyNode node, string key)
        => node.Values.TryGetValue(key, out var v) ? Convert.ToString(v) ?? "" : "";
    public static TunnelEngine Choose(ProxyNode node)
    {
        string type = Value(node, "type").ToLowerInvariant();
        string transport = Value(node, "network").ToLowerInvariant();
        bool reality = node.Values.ContainsKey("reality-opts");
        // Direct VLESS links have an exact Xray outbound; use it instead
        // of reconstructing Reality or XHTTP in a different engine.
        if (type is "vless" or "trojan" && !string.IsNullOrWhiteSpace(node.NativeXrayConfig))
            return TunnelEngine.Xray;
        if (type == "hysteria2" && HasNativeHysteria(node)) return TunnelEngine.Xray;
        // XHTTP and REALITY use the original Xray-core whenever full 3x-ui
        // outbound configuration is available; no lossy reconstruction.
        if ((transport == "xhttp" || reality) && !string.IsNullOrEmpty(node.NativeXrayConfig))
            return TunnelEngine.Xray;
        // sing-box's limited VLESS sidecar does not yet reconstruct REALITY.
        // Use Mihomo for imported sing-box REALITY nodes without native Xray.
        if (reality) return TunnelEngine.Mihomo;
        if (type == "hysteria2" || type == "ss") return TunnelEngine.SingBox;
        if (type == "vless" && transport == "ws" && node.Values.ContainsKey("tls"))
            return TunnelEngine.SingBox;
        if (type == "trojan" && (transport is "" or "tcp" or "ws"))
            return TunnelEngine.SingBox;
        return TunnelEngine.Mihomo;
    }
    public static string Display(ProxyNode node)
    {
        var engine = Choose(node);
        string reason = Value(node, "network");
        if (string.IsNullOrEmpty(reason)) reason = "TCP/UDP";
        return engine switch
        {
            TunnelEngine.Xray => "Xray-core (" + Value(node, "type") + " / " + reason + ")",
            TunnelEngine.SingBox => "sing-box (" + Value(node, "type") + " / " + reason + ")",
            _ => "Mihomo (совместимый транспорт или резерв)"
        };
    }

    private static bool HasNativeHysteria(ProxyNode node)
    {
        if (string.IsNullOrWhiteSpace(node.NativeXrayConfig)) return false;
        try
        {
            using var json = JsonDocument.Parse(node.NativeXrayConfig);
            return json.RootElement.TryGetProperty("outbounds", out var outbounds)
                && outbounds.ValueKind == JsonValueKind.Array
                && outbounds.EnumerateArray().Any(o => o.TryGetProperty("protocol", out var protocol)
                    && protocol.GetString() == "hysteria");
        }
        catch (JsonException) { return false; }
    }
}
