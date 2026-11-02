using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace ProstoKVN;

public sealed record MonitorSnapshot(
    int UdpConnections, int DiscordUdpConnections,
    long UdpUpBytesPerSecond, long UdpDownBytesPerSecond,
    int? HttpsLatencyMs);

public sealed class ConnectionMetrics
{
    private readonly Dictionary<string, (long up, long down)> prior = new();
    private DateTime? previousAt;
    public MonitorSnapshot Parse(string json, DateTime now, int? latency)
    {
        using var doc = JsonDocument.Parse(json);
        var next = new Dictionary<string, (long up, long down)>();
        long upDiff = 0, downDiff = 0;
        int udp = 0, discord = 0;
        var elapsed = previousAt.HasValue
            ? Math.Max(1, (now - previousAt.Value).TotalSeconds) : 0;
        if (doc.RootElement.TryGetProperty("connections", out var connections)
            && connections.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in connections.EnumerateArray())
            {
                if (!item.TryGetProperty("metadata", out var meta)) continue;
                string network = GetString(meta, "network");
                if (!network.Equals("udp", StringComparison.OrdinalIgnoreCase)) continue;
                udp++;
                if (DiscordRouting.IsDiscordProcess(GetString(meta, "process"))) discord++;
                string id = GetString(item, "id");
                if (id.Length == 0) continue;
                long up = GetLong(item, "upload"), down = GetLong(item, "download");
                next[id] = (up, down);
                if (prior.TryGetValue(id, out var before))
                {
                    upDiff += Math.Max(0, up - before.up);
                    downDiff += Math.Max(0, down - before.down);
                }
            }
        }
        prior.Clear();
        foreach (var (id, value) in next) prior[id] = value;
        previousAt = now;
        return new MonitorSnapshot(udp, discord,
            elapsed > 0 ? (long)(upDiff / elapsed) : 0,
            elapsed > 0 ? (long)(downDiff / elapsed) : 0, latency);
    }
    public void Reset() { prior.Clear(); previousAt = null; }
    private static string GetString(JsonElement obj, string key) =>
        obj.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? "" : "";
    private static long GetLong(JsonElement obj, string key) =>
        obj.TryGetProperty(key, out var v) && v.TryGetInt64(out long n) ? n : 0;
}

public sealed class RecoveryPolicy
{
    private readonly int[] backoffSeconds = { 5, 10, 20, 40, 80 };
    public bool Desired { get; private set; }
    public int Attempts { get; private set; }
    public DateTime RetryAfterUtc { get; private set; }
    private int unhealthySamples;
    private DateTime? stableSinceUtc;
    public void Enable() { Desired = true; Attempts = 0; unhealthySamples = 0; stableSinceUtc = null; RetryAfterUtc = DateTime.MinValue; }
    public void Disable() { Desired = false; Attempts = 0; unhealthySamples = 0; stableSinceUtc = null; }
    public void Healthy() { Attempts = 0; unhealthySamples = 0; stableSinceUtc = null; RetryAfterUtc = DateTime.MinValue; }
    // A process that starts and dies repeatedly must not reset its retry budget.
    // Only one minute of stable operation resets the exponential backoff.
    public void ObserveHealthy(DateTime now)
    {
        if (!Desired) return;
        stableSinceUtc ??= now;
        if ((now - stableSinceUtc.Value).TotalSeconds >= 60) Healthy();
    }
    public bool ShouldRecover(bool healthy, DateTime now)
    {
        if (!Desired) return false;
        if (healthy) { ObserveHealthy(now); return false; }
        stableSinceUtc = null;
        unhealthySamples++;
        return unhealthySamples >= 2 && Attempts < backoffSeconds.Length && now >= RetryAfterUtc;
    }
    public void Failed(DateTime now)
    {
        Attempts++;
        RetryAfterUtc = now.AddSeconds(backoffSeconds[Math.Min(Attempts - 1, backoffSeconds.Length - 1)]);
    }
    public bool Exhausted => Attempts >= backoffSeconds.Length;
}
