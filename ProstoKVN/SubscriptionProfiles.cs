using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace ProstoKVN;

public sealed class SubscriptionProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Новая подписка";
    public string ProtectedUrl { get; set; } = "";
    public string Format { get; set; } = "auto";
    public string DetectedFormat { get; set; } = "Не определён";
    public int NodeCount { get; set; }
    public DateTime? LastUpdatedUtc { get; set; }
    public bool Enabled { get; set; } = true;
    [System.Text.Json.Serialization.JsonIgnore]
    public string Url => UserStore.DecryptUrl(ProtectedUrl);
    public override string ToString() => $"{Name}   ·   {NodeCount} серверов   ·   {DetectedFormat}";
}

/// <summary>
/// Subscription list contains only DPAPI-encrypted URLs; no tokens or links
/// appear in JSON, logs or in the multi-subscription selection list.
/// Existing settings.json is read once as a non-destructive migration.
/// </summary>
public static class SubscriptionProfileStore
{
    private static string PathToFile => Path.Combine(UserStore.Home, "subscriptions.json");
    private static readonly JsonSerializerOptions Options =
        new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    public static List<SubscriptionProfile> Load(AppSettings settings)
    {
        if (!File.Exists(PathToFile))
        {
            string legacy = UserStore.DecryptUrl(settings.ProtectedUrl);
            if (!SubscriptionImporter.IsValidUrl(legacy)) return new();
            var initial = new SubscriptionProfile {
                Name = "ProstoKVN — основная",
                ProtectedUrl = settings.ProtectedUrl,
                Format = "auto",
                DetectedFormat = "3x-ui / Base64"
            };
            return new() { initial };
        }
        try
        {
            if (new FileInfo(PathToFile).Length > 256_000) return new();
            var parsed = JsonSerializer.Deserialize<List<SubscriptionProfile>>(
                File.ReadAllText(PathToFile), Options) ?? new();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            return parsed.Where(x => x != null && x.Id.Length is > 5 and < 80
                    && ids.Add(x.Id)
                    && x.Name.Length is > 0 and <= 96
                    && SubscriptionImporter.IsValidUrl(x.Url)
                    && x.Format is "auto" or "base64" or "mihomo" or "singbox" or "xray")
                .Take(30).ToList();
        }
        catch { return new(); }
    }

    public static void Save(IEnumerable<SubscriptionProfile> input)
    {
        var subscriptions = input.ToList();
        if (subscriptions.Count > 30 || subscriptions.Any(p =>
            !SubscriptionImporter.IsValidUrl(p.Url) || p.Name.Length is < 1 or > 96))
            throw new InvalidOperationException("Некорректный список подписок.");
        UserStore.EnsureHome();
        string temp = PathToFile + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(subscriptions, Options),
                Encoding.UTF8);
            File.Move(temp, PathToFile, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    public static List<ProxyNode> Merge(
        IEnumerable<(SubscriptionProfile Profile, IReadOnlyList<ProxyNode> Nodes)> entries)
    {
        var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var names = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<ProxyNode>();
        foreach (var (profile, source) in entries)
        {
            if (!profile.Enabled) continue;
            foreach (var node in source)
            {
                if (result.Count >= 2000) return result;
                var v = node.Values;
                string Read(string key) => v.TryGetValue(key, out var x) ?
                    Convert.ToString(x) ?? "" : "";
                string identity = string.Join("|", Read("type"), Read("server"), Read("port"),
                    Read("uuid"), Read("password"), Read("network"), Read("servername"));
                if (!unique.Add(identity)) continue;
                string baseName = profile.Name + " · " + node.Name;
                string display = baseName;
                int suffix = 1;
                while (!names.Add(display)) display = baseName + " #" + ++suffix;
                var copied = new ProxyNode {
                    Name = display,
                    NativeXrayConfig = node.NativeXrayConfig,
                    Values = new Dictionary<string, object?>(node.Values) { ["name"] = display }
                };
                result.Add(copied);
            }
        }
        return result;
    }
}
