using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace ProstoKVN;

/// <summary>
/// Universal HTTPS subscription import. Detects the returned format instead
/// of trusting file extensions or a particular panel. The special 3x-ui
/// /sub/ and /json/ endpoints remain supported for backward compatibility.
/// </summary>
public static class SubscriptionImporter
{
    public const int MaxServersPerSubscription = 100;
    public sealed record Result(string SubscriptionUrl, List<ProxyNode> Nodes,
        string Summary, string DetectedFormat = "auto", bool ServerLimitReached = false);

    public static bool IsValidUrl(string input)
    {
        if (string.IsNullOrWhiteSpace(input) || input.Length > 8192
            || !Uri.TryCreate(input.Trim(), UriKind.Absolute, out var uri))
            return false;
        if (uri.Scheme.Equals("vless", StringComparison.OrdinalIgnoreCase))
            return !string.IsNullOrWhiteSpace(uri.Host) && uri.Port is > 0 and <= 65535
                && Guid.TryParse(Uri.UnescapeDataString(uri.UserInfo), out _);
        return uri.Scheme == Uri.UriSchemeHttps
            && !string.IsNullOrWhiteSpace(uri.Host)
            && string.IsNullOrEmpty(uri.UserInfo)
            && string.IsNullOrEmpty(uri.Fragment);
    }

    private static string? Alternate3xEndpoint(string url)
    {
        var builder = new UriBuilder(url);
        var segments = builder.Path.Trim('/').Split('/');
        if (segments.Length < 2) return null;
        if (segments[0].Equals("sub", StringComparison.OrdinalIgnoreCase))
            segments[0] = "json";
        else if (segments[0].Equals("json", StringComparison.OrdinalIgnoreCase))
            segments[0] = "sub";
        else return null;
        builder.Path = "/" + string.Join("/", segments);
        return builder.Uri.AbsoluteUri;
    }

    public static async Task<Result> LoadAsync(string rawUrl, string format = "auto")
    {
        string url = rawUrl.Trim();
        if (!IsValidUrl(url))
            throw new InvalidOperationException("Нужна корректная HTTPS-подписка или VLESS-ссылка.");
        if (url.StartsWith("vless://", StringComparison.OrdinalIgnoreCase))
        {
            var single = SubscriptionReader.ParseContent(url).Nodes;
            if (single.Count != 1 || single[0].Values["type"]?.ToString() != "vless")
                throw new InvalidOperationException("Некорректная VLESS-ссылка.");
            SubscriptionAccessException.CheckPlaceholderNodes(single);
            return new(url, single, "1 сервер • VLESS", "VLESS");
        }
        string[] endpoints = format == "auto" && Alternate3xEndpoint(url) is string alt
            ? new[] { url, alt } : new[] { url };
        var nodes = new List<ProxyNode>();
        var formats = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool limitReached = false;
        foreach (string endpoint in endpoints.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                string content = await SubscriptionTransport.GetStringAsync(endpoint);
                // HWID-enabled Remnawave subscriptions expose native Xray JSON
                // at /json. Keep this client's own UA and the same device ID.
                var uri = new Uri(endpoint);
                if (format is "auto" or "xray" && SubscriptionTransport.RequiresHardwareIdentity(endpoint)
                    && uri.AbsolutePath.Trim('/').Split('/').Length == 1
                    && !uri.AbsolutePath.EndsWith("/json", StringComparison.OrdinalIgnoreCase)
                    && !content.TrimStart().StartsWith("[") && !content.TrimStart().StartsWith("{"))
                {
                    var jsonUri = new UriBuilder(uri) { Path = uri.AbsolutePath.TrimEnd('/') + "/json" };
                    try
                    {
                        string native = await SubscriptionTransport.GetStringAsync(jsonUri.Uri.AbsoluteUri);
                        if (native.TrimStart().StartsWith("[") || native.TrimStart().StartsWith("{"))
                        {
                            var nativeParsed = UniversalSubscriptionParser.Parse(native, "xray");
                            if (nativeParsed.Nodes.Count > 0) content = native;
                        }
                    }
                    catch (SubscriptionAccessException) { throw; }
                    catch (Exception) { /* Keep a usable base subscription if /json is unavailable. */ }
                }
                var parsed = UniversalSubscriptionParser.Parse(content, format);
                SubscriptionAccessException.CheckPlaceholderNodes(parsed.Nodes);
                formats.Add(parsed.Format);
                foreach (var node in parsed.Nodes)
                {
                    if (!keys.Add(NodeKey(node))) continue;
                    if (nodes.Count >= MaxServersPerSubscription)
                    {
                        limitReached = true;
                        break;
                    }
                    nodes.Add(node);
                }
            }
            catch (SubscriptionAccessException) { throw; }
            catch (Exception) // One valid endpoint is sufficient for 3x-ui.
            {
                if (endpoints.Length == 1) throw new InvalidOperationException(
                    "Подписка недоступна или не содержит совместимых серверов. Проверь HTTPS-ссылку и формат.");
            }
            if (limitReached) break;
        }
        if (nodes.Count == 0)
            throw new InvalidOperationException(
                "Не удалось получить совместимые серверы. Проверь ссылку, формат и срок действия подписки.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in nodes)
        {
            string baseName = node.Name;
            int n = 1;
            while (!names.Add(node.Name)) node.Name = baseName + " #" + ++n;
            node.Values["name"] = node.Name;
        }
        string detected = string.Join(" + ", formats.OrderBy(x => x));
        return new(url, nodes, $"{nodes.Count} серверов • {detected}"
            + (limitReached ? $" • добавлены первые {MaxServersPerSubscription}" : ""), detected, limitReached);
    }

    internal static string NodeKey(ProxyNode node)
    {
        string Read(string key) => node.Values.TryGetValue(key, out var v)
            ? Convert.ToString(v) ?? "" : "";
        string native = string.IsNullOrWhiteSpace(node.NativeXrayConfig) ? ""
            : node.Name + "|" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(node.NativeXrayConfig)));
        return string.Join("|", Read("type"), Read("server"), Read("port"), Read("uuid"),
            Read("password"), Read("network"), Read("servername"), native);
    }
}
