namespace ProstoKVN;

public sealed record RoutingModeOption(string Id, string Title)
{
    public override string ToString() => Title;
}

public static class RoutingModes
{
    public static IReadOnlyList<RoutingModeOption> Options { get; } = Array.AsReadOnly(new[] {
        new RoutingModeOption("vpn", "Весь трафик"),
        new RoutingModeOption("mine", "Только выбранное"),
        new RoutingModeOption("smart", "Умный")
    });

    public static bool IsPrimary(string? mode) => mode is "vpn" or "mine" or "smart";
    public static string Title(string mode) => Options.FirstOrDefault(x => x.Id == mode)?.Title ?? "Умный";
}

public sealed record RoutingModeUpgrade(AppSettings Settings, List<RouteRule> Rules,
    string Notice = "", string BackupDirectory = "")
{
    public bool Changed => Notice.Length > 0;
}

public static class RoutingModeMigration
{
    public static RoutingModeUpgrade Plan(AppSettings settings, IReadOnlyList<RouteRule> rules)
    {
        if (settings.Mode is not ("apps" or "direct"))
            return new(settings, rules.ToList());

        var nextSettings = RoutingPolicy.Snapshot(settings);
        var nextRules = rules.Select(r => r.Snapshot()).ToList();
        bool discordMode = settings.Mode == "apps";
        // The old apps mode applied every manual exception, ignoring its mine
        // checkbox. Automatically learned entries did not participate at all.
        foreach (var rule in nextRules) rule.MyRuleEnabled = discordMode && rule.Manual;

        if (discordMode)
        {
            foreach (string process in DiscordRouting.Processes)
            {
                RouteTarget.TryParseApplication(process, out var target);
                if (nextRules.Any(r => r.Manual && RouteTarget.TryParse(r.Domain, out var existing)
                    && existing.Kind == target.Kind && existing.Value.Equals(target.Value, StringComparison.OrdinalIgnoreCase)))
                    continue;
                nextRules.Add(new RouteRule {
                    Domain = target.Value, Route = "VEGA", Label = "Discord", Preset = "Discord",
                    Manual = true, MyRuleEnabled = true
                });
            }
        }

        nextSettings.Mode = "mine";
        nextSettings.Tun = true;
        string notice = discordMode
            ? "Режим «Правила + Discord» перенесён в «Только выбранное». Правила Discord теперь видны в списке приложений; ваши исключения сохранены."
            : "Режим «Без VPN» заменён на «Только выбранное» с выключенными правилами. Трафик остаётся прямым. Чтобы полностью выключить клиент, нажмите «Отключиться».";
        return new(nextSettings, nextRules, notice);
    }
}
