using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace ProstoKVN;

/// <summary>
/// Convert a single, user-supplied VLESS share URI to a restricted Xray outbound.
/// Never import arbitrary routing, DNS or listener settings from the share link.
/// </summary>
public static class VlessShareConfig
{
    public static string Build(Uri uri, IReadOnlyDictionary<string, string> query)
    {
        string Get(string key, string fallback = "") =>
            query.TryGetValue(key, out var value) ? value : fallback;

        string id = Uri.UnescapeDataString(uri.UserInfo);
        if (!Guid.TryParse(id, out _) || uri.Port is < 1 or > 65535
            || string.IsNullOrWhiteSpace(uri.Host))
            throw new FormatException("VLESS: требуется корректный UUID и адрес сервера.");

        string security = Get("security", "none").ToLowerInvariant();
        if (security is not ("none" or "tls" or "reality"))
            throw new FormatException("VLESS: неподдерживаемый тип безопасности.");
        string transport = Get("type", "tcp").ToLowerInvariant();
        if (transport is not ("tcp" or "ws" or "grpc" or "xhttp" or "http" or "httpupgrade"))
            throw new FormatException("VLESS: неподдерживаемый транспорт " + transport);

        var user = new JsonObject { ["id"] = id, ["encryption"] = "none" };
        string flow = Get("flow");
        if (flow.Length > 0) user["flow"] = flow;
        var endpoint = new JsonObject
        {
            ["address"] = uri.Host, ["port"] = uri.Port,
            ["users"] = new JsonArray(user)
        };
        var stream = new JsonObject { ["network"] = transport, ["security"] = security };
        string sni = Get("sni", uri.Host);
        string fingerprint = Get("fp", "chrome");

        if (security == "reality")
        {
            string publicKey = Get("pbk");
            if (string.IsNullOrWhiteSpace(publicKey) || string.IsNullOrWhiteSpace(Get("sni")))
                throw new FormatException("VLESS Reality: отсутствует public key или SNI.");
            stream["realitySettings"] = new JsonObject
            {
                ["show"] = false, ["serverName"] = sni,
                ["fingerprint"] = fingerprint, ["publicKey"] = publicKey,
                ["shortId"] = Get("sid"), ["spiderX"] = Get("spx", "/")
            };
        }
        else if (security == "tls")
        {
            var tls = new JsonObject { ["serverName"] = sni };
            if (Get("fp").Length > 0) tls["fingerprint"] = fingerprint;
            var alpn = Get("alpn");
            if (alpn.Length > 0)
            {
                var values = new JsonArray();
                foreach (string item in alpn.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    values.Add(item);
                tls["alpn"] = values;
            }
            stream["tlsSettings"] = tls;
        }

        if (transport == "ws")
        {
            var headers = new JsonObject();
            if (Get("host").Length > 0) headers["Host"] = Get("host");
            stream["wsSettings"] = new JsonObject
            {
                ["path"] = Get("path", "/"),
                ["headers"] = headers
            };
        }
        else if (transport == "grpc")
            stream["grpcSettings"] = new JsonObject { ["serviceName"] = Get("serviceName") };
        else if (transport == "xhttp")
        {
            var xhttp = new JsonObject
            {
                ["path"] = Get("path", "/"), ["mode"] = Get("mode", "auto")
            };
            if (Get("host").Length > 0) xhttp["host"] = Get("host");
            stream["xhttpSettings"] = xhttp;
        }
        else if (transport == "http")
        {
            var hosts = new JsonArray();
            if (Get("host").Length > 0) hosts.Add(Get("host"));
            stream["httpSettings"] = new JsonObject
            {
                ["path"] = Get("path", "/"), ["host"] = hosts
            };
        }
        else if (transport == "httpupgrade")
        {
            var upgrade = new JsonObject { ["path"] = Get("path", "/") };
            if (Get("host").Length > 0) upgrade["host"] = Get("host");
            stream["httpupgradeSettings"] = upgrade;
        }

        var outbound = new JsonObject
        {
            ["tag"] = "proxy", ["protocol"] = "vless",
            ["settings"] = new JsonObject { ["vnext"] = new JsonArray(endpoint) },
            ["streamSettings"] = stream
        };
        return new JsonObject { ["outbounds"] = new JsonArray(outbound) }.ToJsonString();
    }
}
