using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace ProstoKVN;

/// <summary>
/// Imports an HTTPS 3x-ui subscription without modifying user settings.
/// The onboarding dialog commits only after usable nodes have been received.
/// </summary>
public static class SubscriptionImporter
{
    public sealed record Result(string SubscriptionUrl, List<ProxyNode> Nodes, string Summary);

    public static bool IsValidUrl(string input)
    {
        return Uri.TryCreate(input.Trim(), UriKind.Absolute, out var uri)
            && uri.Scheme == Uri.UriSchemeHttps
            && !string.IsNullOrWhiteSpace(uri.Host)
            && string.IsNullOrEmpty(uri.UserInfo)
            && string.IsNullOrEmpty(uri.Fragment);
    }

    private static string Endpoint(string url, string type)
    {
        var builder = new UriBuilder(url);
        string[] segments = builder.Path.Trim('/').Split('/');
        if (segments.Length >= 2
            && (segments[0].Equals("json", StringComparison.OrdinalIgnoreCase)
                || segments[0].Equals("sub", StringComparison.OrdinalIgnoreCase)))
            segments[0] = type;
        builder.Path = "/" + string.Join("/", segments);
        return builder.Uri.AbsoluteUri;
    }

    public static async Task<Result> LoadAsync(string rawUrl)
    {
        string url = rawUrl.Trim();
        if (!IsValidUrl(url))
            throw new InvalidOperationException("Нужна корректная HTTPS-ссылка подписки 3x-ui.");

        List<ProxyNode> nodes = new();
        bool jsonSuccess = false, subSuccess = false;
        try
        {
            var json = await JsonSubscription.LoadAsync(Endpoint(url, "json"));
            nodes.AddRange(json);
            jsonSuccess = json.Count > 0;
        }
        catch { /* Try the /sub/ format if JSON is unavailable. */ }

        try
        {
            var (subNodes, _) = await SubscriptionReader.LoadAsync(Endpoint(url, "sub"));
            var keys = new HashSet<string>(nodes.Select(Key), StringComparer.Ordinal);
            foreach (var node in subNodes)
                if (keys.Add(Key(node))) nodes.Add(node);
            subSuccess = subNodes.Count > 0;
        }
        catch { /* A successful /json/ response is sufficient. */ }

        if (nodes.Count == 0)
            throw new InvalidOperationException(
                "Серверы не получены. Проверь ссылку, интернет и срок действия подписки.");

        var uniqueNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in nodes)
        {
            string original = node.Name;
            int index = 1;
            while (!uniqueNames.Add(node.Name))
                node.Name = original + " #" + ++index;
            node.Values["name"] = node.Name;
        }
        string source = jsonSuccess && subSuccess ? "JSON + SUB" : jsonSuccess ? "JSON" : "SUB";
        return new Result(url, nodes, $"{nodes.Count} серверов • {source}");
    }

    private static string Key(ProxyNode node)
    {
        string Read(string name) => node.Values.TryGetValue(name, out var value)
            ? Convert.ToString(value) ?? "" : "";
        return string.Join("|", Read("type"), Read("server"), Read("port"),
            Read("uuid"), Read("network"));
    }
}
