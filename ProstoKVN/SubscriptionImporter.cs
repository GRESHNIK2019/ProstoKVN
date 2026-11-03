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
    public sealed record Result(string SubscriptionUrl, List<ProxyNode> Nodes,
        string Summary, string DetectedFormat = "auto");

    public static bool IsValidUrl(string input)
    {
        return Uri.TryCreate(input?.Trim(), UriKind.Absolute, out var uri)
            && uri.Scheme == Uri.UriSchemeHttps
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
            throw new InvalidOperationException("Нужна корректная HTTPS-ссылка подписки.");
        string[] endpoints = format == "auto" && Alternate3xEndpoint(url) is string alt
            ? new[] { url, alt } : new[] { url };
        var nodes = new List<ProxyNode>();
        var formats = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string endpoint in endpoints.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                string content = await SubscriptionTransport.GetStringAsync(endpoint);
                var parsed = UniversalSubscriptionParser.Parse(content, format);
                formats.Add(parsed.Format);
                foreach (var node in parsed.Nodes)
                {
                    if (nodes.Count >= UniversalSubscriptionParser.MaxNodes) break;
                    if (keys.Add(Key(node))) nodes.Add(node);
                }
            }
            catch (Exception) // One valid endpoint is sufficient for 3x-ui.
            {
                if (endpoints.Length == 1) throw new InvalidOperationException(
                    "Подписка недоступна или не содержит совместимых серверов. Проверь HTTPS-ссылку и формат.");
            }
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
        return new(url, nodes, $"{nodes.Count} серверов • {detected}", detected);
    }

    private static string Key(ProxyNode node)
    {
        string Read(string key) => node.Values.TryGetValue(key, out var v)
            ? Convert.ToString(v) ?? "" : "";
        return string.Join("|", Read("type"), Read("server"), Read("port"), Read("uuid"),
            Read("password"), Read("network"), Read("servername"));
    }
}
