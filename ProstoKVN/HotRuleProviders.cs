using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace ProstoKVN;

// Fixed rule-set slots allow ordered, destination-aware updates without
// reloading Mihomo's TUN, DNS, listeners or proxy sessions.
public sealed class HotRulePlan
{
    public Dictionary<string, object?> Providers { get; } = new();
    public List<string> Rules { get; } = new();
    public int ManualRuleCount { get; set; }
    public Dictionary<string, string> Files { get; } = new(StringComparer.Ordinal);
}

public static class HotRuleProviders
{
    private const int SlotsPerPhase = 16;
    private static readonly string[] BaseTargets = { "DIRECT", "VEGA", "REJECT" };
    private static string Name(string phase, int slot, int target) =>
        $"vega-{phase}-{slot:00}-{target:00}";

    public static HotRulePlan Create(IReadOnlyList<ProxyNode> nodes,
        IReadOnlyList<RouteRule> routeRules, AppSettings settings)
    {
        var plan = new HotRulePlan();
        var chosen = nodes.FirstOrDefault(x => x.Name == settings.SelectedNode) ?? nodes[0];
        var targets = BaseTargets.Concat(routeRules
            .Where(r => r.Route == "VEGA" && !string.IsNullOrWhiteSpace(r.PreferredNode))
            .Select(r => r.PreferredNode)
            .Where(name => nodes.Any(n => n.Name == name)))
            .Distinct(StringComparer.Ordinal).ToArray();

        int Priority(string value)
        {
            if (!RouteTarget.TryParse(value, out var t)) return -1;
            if (t.IsAddress) return 3000 + t.PrefixBits;
            if (t.IsSubnet) return 2000 + t.PrefixBits;
            return 1000 + t.Value.Length;
        }

        string Target(RouteRule rule)
        {
            if (rule.Route == "REJECT") return "REJECT";
            if (rule.Route != "VEGA") return "DIRECT";
            var preferred = nodes.FirstOrDefault(n => n.Name == rule.PreferredNode);
            if (preferred != null && (preferred.Name == chosen.Name ||
                !(preferred.NativeXrayConfig != null && preferred.Values.ContainsKey("reality-opts"))))
                return preferred.Name;
            return "VEGA";
        }

        foreach (string phase in new[] { "manual", "auto" })
        {
            IEnumerable<RouteRule> candidates = phase == "manual"
                ? routeRules.Where(r => r.Manual)
                : settings.Mode == "smart"
                    ? routeRules.Where(r => !r.Manual && (r.Route == "VEGA" || r.Route == "DIRECT")
                        && !RuDomainPolicy.IsRuDomain(r.Domain))
                    : Enumerable.Empty<RouteRule>();
            var ordered = candidates.OrderByDescending(r => Priority(r.Domain))
                .Select(r => (rule: r, target: Target(r)))
                .Where(item => RouteTarget.TryParse(item.rule.Domain, out _))
                .ToList();
            var runs = new List<(string target, List<string> patterns)>();
            foreach (var item in ordered)
            {
                if (runs.Count == 0 || runs[^1].target != item.target)
                    runs.Add((item.target, new List<string>()));
                RouteTarget.TryParse(item.rule.Domain, out var parsed);
                // A classical provider owns the MATCHING CONDITION only;
                // the RULE-SET declaration supplies the outbound.
                string text = parsed.ToRule(item.target);
                int lastComma = text.LastIndexOf(',');
                if (text.EndsWith(",no-resolve", StringComparison.Ordinal))
                    text = text[..text.LastIndexOf(',', text.LastIndexOf(',') - 1)] + ",no-resolve";
                else text = text[..lastComma];
                runs[^1].patterns.Add(text);
            }
            if (runs.Count > SlotsPerPhase)
                throw new InvalidOperationException($"Слишком много групп {phase}: {runs.Count} (лимит {SlotsPerPhase}).");

            for (int slot = 0; slot < SlotsPerPhase; slot++)
            {
                for (int t = 0; t < targets.Length; t++)
                {
                    string provider = Name(phase, slot, t);
                    string path = "hot-rules/" + provider + ".yaml";
                    plan.Providers[provider] = new Dictionary<string, object?>
                    {
                        ["type"] = "file", ["behavior"] = "classical",
                        ["format"] = "yaml", ["path"] = "./" + path
                    };
                    plan.Rules.Add($"RULE-SET,{provider},{targets[t]}");
                    var entries = slot < runs.Count && runs[slot].target == targets[t]
                        ? runs[slot].patterns : new List<string>();
                    plan.Files[provider] = Render(entries);
                }
            }
            if (phase == "manual") plan.ManualRuleCount = plan.Rules.Count;
        }
        return plan;
    }

    public static string Render(IEnumerable<string> patterns)
    {
        var arr = patterns.ToList();
        if (arr.Count == 0) return "payload: []\n";
        var sb = new StringBuilder("payload:\n");
        foreach (string pattern in arr)
            sb.Append("  - '").Append(pattern.Replace("'", "''")).Append("'\n");
        return sb.ToString();
    }

    private static string PathFor(string name) =>
        Path.Combine(UserStore.Home, "hot-rules", name + ".yaml");

    public static void Stage(HotRulePlan plan)
    {
        Directory.CreateDirectory(Path.Combine(UserStore.Home, "hot-rules"));
        foreach (var (name, payload) in plan.Files)
            WriteAtomic(PathFor(name), payload);
    }

    public static void WriteAtomic(string path, string text)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temporary, text, new UTF8Encoding(false));
        try { File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
