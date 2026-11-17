using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace ProstoKVN;

// Reuse rule-set slots for routine updates without reloading TUN or listeners.
// Independent matches can share a run; overlapping matches retain their order.
public sealed class HotRulePlan
{
    public Dictionary<string, object?> Providers { get; } = new();
    public List<string> Rules { get; } = new();
    public int ManualRuleCount { get; set; }
    public Dictionary<string, string> Files { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, int> PhaseSlots { get; } = new(StringComparer.Ordinal);
}

public static class HotRuleProviders
{
    private const int InitialSlotsPerPhase = 16;
    private static readonly string[] BaseTargets = { "DIRECT", "VEGA", "REJECT" };
    private static string Name(string phase, int slot, int target) =>
        $"vega-{phase}-{slot:00}-{target:00}";

    public static HotRulePlan Create(IReadOnlyList<ProxyNode> nodes,
        IReadOnlyList<RouteRule> routeRules, AppSettings settings, HotRulePlan? previousPlan = null)
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
            return t.Priority;
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
            var runs = new List<(string target, List<string> patterns, List<RouteTarget> matches)>();
            foreach (var item in ordered)
            {
                RouteTarget.TryParse(item.rule.Domain, out var parsed);
                int destination = -1;
                for (int i = runs.Count - 1; i >= 0; i--)
                {
                    if (runs[i].target == item.target) { destination = i; break; }
                    // Moving this rule before a conflicting outbound would
                    // change which route wins. Unknown intersections count as conflicts.
                    if (runs[i].matches.Any(other => MayOverlap(parsed, other))) break;
                }
                if (destination < 0)
                {
                    destination = runs.Count;
                    runs.Add((item.target, new List<string>(), new List<RouteTarget>()));
                }
                // A classical provider owns the MATCHING CONDITION only;
                // the RULE-SET declaration supplies the outbound.
                string text = parsed.MihomoType + "," + parsed.MihomoValue
                    + (parsed.IsDomain || parsed.IsApplication ? "" : ",no-resolve");
                runs[destination].patterns.Add(text);
                runs[destination].matches.Add(parsed);
            }
            int slots = InitialSlotsPerPhase;
            if (previousPlan != null && previousPlan.PhaseSlots.TryGetValue(phase, out int previousSlots))
                slots = Math.Max(slots, previousSlots);
            while (slots < runs.Count) slots = checked(slots * 2);
            plan.PhaseSlots[phase] = slots;

            for (int slot = 0; slot < slots; slot++)
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

    private static bool MayOverlap(RouteTarget left, RouteTarget right)
    {
        if (left.IsDomain && right.IsDomain)
            return left.Matches(right.Value, null) || right.Matches(left.Value, null);
        if (left.IsApplication && right.IsApplication)
        {
            if (left.Kind == RouteTargetKind.ProcessName)
                return left.Matches(null, null, right.DisplayValue);
            if (right.Kind == RouteTargetKind.ProcessName)
                return right.Matches(null, null, left.DisplayValue);
            return left.DisplayValue.Equals(right.DisplayValue, StringComparison.OrdinalIgnoreCase);
        }
        if (!left.IsDomain && !left.IsApplication && !right.IsDomain && !right.IsApplication)
            return left.Matches(null, right.Value.Split('/')[0])
                || right.Matches(null, left.Value.Split('/')[0]);
        // A process can access any destination; domains can resolve to any IP.
        return true;
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
