using System;
using System.Collections.Generic;
using System.Linq;

namespace ProstoKVN;

/// <summary>
/// Compact only unambiguous, stable automatic rules of a single service.
/// Manual overrides and tentative rules are never collapsed.
/// </summary>
public static class ServiceRuleGrouper
{
    private static readonly (string Root, string Service)[] Families =
    {
        ("googlevideo.com", "YouTube / Google Video"),
        ("youtube.com", "YouTube / Google Video"),
        ("ytimg.com", "YouTube / Google Video"),
        ("ggpht.com", "YouTube / Google Video"),
        ("youtube-nocookie.com", "YouTube / Google Video"),
        ("chatgpt.com", "ChatGPT / OpenAI"),
        ("openai.com", "ChatGPT / OpenAI"),
        ("oaistatic.com", "ChatGPT / OpenAI"),
        ("oaiusercontent.com", "ChatGPT / OpenAI"),
        ("oaistatsig.com", "ChatGPT / OpenAI"),
        ("cdn.openaimerge.com", "ChatGPT / OpenAI"),
        ("claude.ai", "Claude / Anthropic"),
        ("claude.com", "Claude / Anthropic"),
        ("claude.app", "Claude / Anthropic"),
        ("anthropic.com", "Claude / Anthropic"),
        ("claudeusercontent.com", "Claude / Anthropic"),
        ("claudemcpcontent.com", "Claude / Anthropic"),
        ("discord.com", "Discord"),
        ("discord.gg", "Discord"),
        ("discordapp.com", "Discord"),
        ("discordapp.net", "Discord"),
        ("prod.do.dsp.mp.microsoft.com", "Microsoft"),
        ("microsoft.com", "Microsoft"),
        ("doubleclick.net", "Google Ads"),
        ("pornhub.com", "Pornhub"),
        ("telegram.org", "Telegram"),
        ("telegram.me", "Telegram"),
        ("telegram.dog", "Telegram"),
        ("t.me", "Telegram"),
        ("telegra.ph", "Telegram"),
        ("telesco.pe", "Telegram"),
        ("google.com", "Google"),
        ("googleapis.com", "Google"),
        ("gstatic.com", "Google")
    };
    public static bool IsWithin(string domain, string suffix) =>
        domain.Equals(suffix, StringComparison.OrdinalIgnoreCase)
        || domain.EndsWith("." + suffix, StringComparison.OrdinalIgnoreCase);

    private static (string Root, string Service)? Find(string domain)
    {
        return Families
            .Where(x => IsWithin(domain, x.Root))
            .OrderByDescending(x => x.Root.Length)
            .Select(x => ((string Root, string Service)?)x)
            .FirstOrDefault();
    }
    public static string ServiceName(string domain)
    {
        if (RouteTarget.TryParse(domain, out var target))
        {
            if (target.IsAddress) return "IP-адреса";
            if (target.IsSubnet) return "Подсети";
        }
        return Find(domain)?.Service ?? "Другие сайты";
    }

    public static int CompactStableRules(List<RouteRule> rules)
    {
        int changed = 0;
        foreach (string root in Families.Select(x => x.Root).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var matching = rules.Where(r => !r.Manual && !r.Provisional
                && r.ExpiresUtc == null && RouteTarget.TryParse(r.Domain, out var t)
                && t.IsDomain && IsWithin(t.Value, root)).ToList();
            if (matching.Count < 2) continue;
            if (matching.Select(x => x.Route).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 1
                || matching.Select(x => x.PreferredNode).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 1)
                continue;
            // Never broaden a suffix if a manual rule is already pinned at the root.
            // Subdomain manual rules still take precedence in the compiled config.
            if (rules.Any(r => r.Manual && r.Domain.Equals(root, StringComparison.OrdinalIgnoreCase)))
                continue;
            // If any tentative auto rule shares this service root, keep separate
            // domain decisions; don't accidentally grant a provisional rule permanence.
            if (rules.Any(r => !r.Manual && r.Provisional && IsWithin(r.Domain, root)))
                continue;
            var canonical = matching.FirstOrDefault(r => r.Domain.Equals(root, StringComparison.OrdinalIgnoreCase));
            // Matching failures on two hosts do not prove the whole service
            // needs VPN. Compact only beneath a root that already has a rule.
            if (canonical == null) continue;
            foreach (var duplicate in matching.Where(r => !ReferenceEquals(r, canonical)).ToArray())
            {
                rules.Remove(duplicate);
                changed++;
            }
        }
        return changed;
    }
}
