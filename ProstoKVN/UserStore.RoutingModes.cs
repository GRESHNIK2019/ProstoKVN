using System.IO;
using System.Text.Json;

namespace ProstoKVN;

public static partial class UserStore
{
    public static RoutingModeUpgrade UpgradeRoutingModes(AppSettings settings, List<RouteRule> rules)
    {
        var upgrade = RoutingModeMigration.Plan(settings, rules);
        if (!upgrade.Changed) return upgrade;

        EnsureHome();
        string backup = Path.Combine(Home, $"routing-modes-backup-{DateTime.UtcNow:yyyyMMdd-HHmmssfff}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(backup);
        foreach (var (name, json) in new[] {
            ("settings.json", JsonSerializer.Serialize(settings, JsonOptions)),
            ("routes.json", JsonSerializer.Serialize(rules, JsonOptions))
        })
        {
            string source = Path.Combine(Home, name);
            string destination = Path.Combine(backup, name);
            if (File.Exists(source)) File.Copy(source, destination);
            else File.WriteAllText(destination, json);
            if (File.Exists(source + ".bak")) File.Copy(source + ".bak", destination + ".bak");
        }

        // Commit rules first: if interrupted before settings are committed,
        // planning the same legacy mode again is safe and creates no duplicates.
        SaveRules(upgrade.Rules);
        try { SaveSettings(upgrade.Settings); }
        catch (Exception commitError)
        {
            try { SaveRules(rules); }
            catch (Exception rollbackError)
            {
                throw new InvalidOperationException("Не удалось сохранить перенос режимов и восстановить правила. Резервная копия: " + backup,
                    new AggregateException(commitError, rollbackError));
            }
            throw;
        }
        return upgrade with { BackupDirectory = backup };
    }
}
