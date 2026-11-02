using System;
using System.Collections.Generic;
using System.Linq;

namespace ProstoKVN;

/// <summary>
/// Two independent adaptive service profiles:
/// OpenAI: use VPN from first request so blocked auth / websocket endpoints
/// cannot leak to the DIRECT fallback while HTTP probes are inconclusive.
/// Claude: prefer DIRECT unless a separate observed host proves that DIRECT
/// fails and the VPN succeeds. Some Claude nodes fail via foreign VPN IPs.
/// No shared auth/CDN services of unrelated companies are blanket routed.
/// </summary>
public static class AiServiceRouting
{
    public static readonly string[] OpenAiDomains =
    {
        "chatgpt.com", "openai.com", "oaistatic.com",
        "oaiusercontent.com", "oaistatsig.com", "cdn.openaimerge.com"
    };

    public static readonly string[] ClaudeDomains =
    {
        "claude.ai", "claude.com", "claude.app",
        "anthropic.com", "claudeusercontent.com", "claudemcpcontent.com"
    };

    private static bool IsWithin(string? host, IEnumerable<string> roots)
    {
        if (string.IsNullOrWhiteSpace(host)) return false;
        string normalized = host.Trim().TrimEnd('.');
        return roots.Any(root => ServiceRuleGrouper.IsWithin(normalized, root));
    }

    public static bool IsOpenAiDomain(string? host) => IsWithin(host, OpenAiDomains);
    public static bool IsClaudeDomain(string? host) => IsWithin(host, ClaudeDomains);
    public static bool Matches(string? host) => IsOpenAiDomain(host) || IsClaudeDomain(host);

    /// <summary>
    /// Populate visible service defaults BEFORE starting the core. OpenAI gets
    /// VPN due to unreliable anti-bot/OAuth probes. Claude defaults to DIRECT
    /// and remains eligible for observation-based per-host auto switching.
    /// Never overwrite a manual rule, or an already learned stable Claude VPN.
    /// </summary>
    public static int EnsureDefaults(List<RouteRule> rules)
    {
        int changed = 0;
        foreach (string domain in OpenAiDomains.Concat(ClaudeDomains))
        {
            if (rules.Any(x => x.Manual
                && x.Domain.Equals(domain, StringComparison.OrdinalIgnoreCase)))
                continue;
            var entry = rules.FirstOrDefault(x => !x.Manual
                && x.Domain.Equals(domain, StringComparison.OrdinalIgnoreCase));
            bool openAi = IsOpenAiDomain(domain);
            if (entry == null)
            {
                rules.Add(new RouteRule
                {
                    Domain = domain,
                    Route = openAi ? "VEGA" : "DIRECT",
                    Manual = false,
                    Label = openAi ? "ChatGPT / OpenAI" : "Claude / Anthropic",
                    Provisional = false,
                    ExpiresUtc = null
                });
                changed++;
            }
            else if (openAi && (entry.Route != "VEGA" || entry.Provisional || entry.ExpiresUtc.HasValue))
            {
                entry.Route = "VEGA";
                entry.Provisional = false;
                entry.ExpiresUtc = null;
                entry.UpdatedUtc = DateTime.UtcNow;
                changed++;
            }
            // Existing Claude auto routes (DIRECT or confirmed VPN) are not
            // reset at startup; the learner adjusts them on observed failures.
        }
        return changed;
    }

    /// <summary>
    /// Guaranteed routing before any learned-rule persistence is available.
    /// Manual exceptions are assembled above this fallback by VpnCore.
    /// </summary>
    public static IEnumerable<string> BuildFallbackRules() =>
        OpenAiDomains.Select(root => $"DOMAIN-SUFFIX,{root},VEGA");
}
