namespace ProstoKVN;

// Opt-in service preset. A youtube.com suffix cannot match the separate
// googlevideo.com media hosts or the player API on googleapis.com.
public static class YouTubeRouting
{
    public const string ServiceName = "YouTube / Google Video";
    public static IReadOnlyList<string> Domains { get; } = Array.AsReadOnly(new[] {
        "youtube.com", "youtu.be", "youtube-nocookie.com", "googlevideo.com",
        "ytimg.com", "yt3.ggpht.com", "youtubei.googleapis.com", "youtube.googleapis.com"
    });

    public static List<RouteRule> Enable(IEnumerable<RouteRule> source)
    {
        var rules = source.Select(r => r.Snapshot()).ToList();
        // Use the same exit for the site and its media when a user pinned
        // YouTube to a server. Existing manual choices on other hosts still win.
        string preferred = rules.FirstOrDefault(r => r.Manual && r.Route == "VEGA"
            && RouteTarget.TryParse(r.Domain, out var t) && t.Kind == RouteTargetKind.Domain
            && t.Value == "youtube.com")?.PreferredNode ?? "";
        foreach (string domain in Domains)
        {
            var existing = rules.Where(r => RouteTarget.TryParse(r.Domain, out var t)
                && t.Kind == RouteTargetKind.Domain && t.Value == domain).ToList();
            var manual = existing.Where(r => r.Manual).ToList();
            if (manual.Count > 0)
            {
                // The button explicitly opts VPN rules in, but does not turn
                // a user's DIRECT/REJECT exception into a VPN rule.
                foreach (var rule in manual.Where(r => r.Route == "VEGA"))
                    rule.MyRuleEnabled = true;
                continue;
            }
            rules.RemoveAll(existing.Contains);
            rules.Add(new RouteRule {
                Domain = domain, Route = "VEGA", PreferredNode = preferred,
                Manual = true, MyRuleEnabled = true, Preset = "YouTube"
            });
        }
        return rules;
    }

    public static int ActiveDomainCount(IReadOnlyList<RouteRule> rules, string mode)
        => Domains.Count(domain => RoutingPolicy.ExpectedRoute(rules, mode, domain) is not ("DIRECT" or "REJECT")
            && RoutingPolicy.ExpectedRoute(rules, mode, "media-check." + domain) is not ("DIRECT" or "REJECT"));
}
