using System;
using System.Collections.Generic;

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
            TunnelEngine.Xray => "Xray-core (REALITY/XHTTP)",
            TunnelEngine.SingBox => "sing-box (" + Value(node, "type") + " / " + reason + ")",
            _ => "Mihomo (совместимый транспорт или резерв)"
        };
    }
}
