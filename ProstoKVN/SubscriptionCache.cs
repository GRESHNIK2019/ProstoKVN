using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ProstoKVN;

/// <summary>
/// Caches working nodes encrypted with Windows DPAPI for offline startup.
/// Cache is bound to the exact subscription URL, has a maximum age, and
/// never stores proxy passwords/UUIDs in plaintext.
/// </summary>
public static class SubscriptionCache
{
    private sealed record Envelope(string UrlHash, DateTime SavedUtc, string ProtectedNodes);
    private static string FilePath => Path.Combine(UserStore.Home, "subscription-cache.json");
    private static string MultiCacheDir => Path.Combine(UserStore.Home, "subscription-caches");
    private static string CachePath(string url) => Path.Combine(MultiCacheDir, Fingerprint(url) + ".json");
    private static string Fingerprint(string url)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url.Trim())));
    public static bool Save(string url, IReadOnlyList<ProxyNode> nodes)
    {
        if (!SubscriptionImporter.IsValidUrl(url) || nodes.Count == 0) return false;
        try
        {
            UserStore.EnsureHome();
            Directory.CreateDirectory(MultiCacheDir);
            byte[] plain = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(nodes));
            byte[] encrypted = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser);
            var envelope = new Envelope(Fingerprint(url), DateTime.UtcNow,
                Convert.ToBase64String(encrypted));
            string path = CachePath(url);
            string temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                File.WriteAllText(temp, JsonSerializer.Serialize(envelope), Encoding.UTF8);
                File.Move(temp, path, overwrite: true);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
            CryptographicOperations.ZeroMemory(plain);
            return true;
        }
        catch { return false; }
    }

    public static bool TryLoad(string url, out List<ProxyNode> nodes, out DateTime savedUtc)
    {
        nodes = new();
        savedUtc = DateTime.MinValue;
        if (!SubscriptionImporter.IsValidUrl(url)) return false;
        try
        {
            string path = CachePath(url);
            if (!File.Exists(path)) path = FilePath; // Legacy single-subscription cache.
            if (!File.Exists(path) || new FileInfo(path).Length > 5_000_000)
                return false;
            var envelope = JsonSerializer.Deserialize<Envelope>(File.ReadAllText(path));
            if (envelope == null || envelope.UrlHash != Fingerprint(url)
                || DateTime.UtcNow - envelope.SavedUtc > TimeSpan.FromDays(30)
                || envelope.SavedUtc > DateTime.UtcNow.AddMinutes(5)) return false;
            byte[] plain = ProtectedData.Unprotect(
                Convert.FromBase64String(envelope.ProtectedNodes), null,
                DataProtectionScope.CurrentUser);
            try
            {
                using var data = JsonDocument.Parse(plain);
                foreach (var item in data.RootElement.EnumerateArray())
                {
                    if (!item.TryGetProperty("Name", out var name) ||
                        !item.TryGetProperty("Values", out var values)
                        || values.ValueKind != JsonValueKind.Object) continue;
                    var node = new ProxyNode
                    {
                        Name = name.GetString() ?? "",
                        NativeXrayConfig = item.TryGetProperty("NativeXrayConfig", out var native)
                            && native.ValueKind == JsonValueKind.String ? native.GetString() : null,
                        Values = ReadMap(values)
                    };
                    if (node.Name.Length > 0
                        && node.Values.TryGetValue("server", out var server)
                        && !string.IsNullOrWhiteSpace(Convert.ToString(server)))
                        nodes.Add(node);
                }
            }
            finally { CryptographicOperations.ZeroMemory(plain); }
            if (nodes.Count == 0) return false;
            savedUtc = envelope.SavedUtc;
            return true;
        }
        catch { nodes.Clear(); return false; }
    }

    private static Dictionary<string, object?> ReadMap(JsonElement value)
        => value.EnumerateObject().ToDictionary(x => x.Name,
            x => ConvertElement(x.Value), StringComparer.Ordinal);

    private static object? ConvertElement(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.Object => ReadMap(v),
        JsonValueKind.Array => v.EnumerateArray().Select(ConvertElement).ToArray(),
        JsonValueKind.String => v.GetString(),
        JsonValueKind.Number when v.TryGetInt64(out long integer) => integer,
        JsonValueKind.Number => v.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null
    };
}
