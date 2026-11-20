using System;
using System.Collections.Generic;
using System.Linq;

namespace ProstoKVN;

// HTTPS latency is measured through Mihomo's selected VEGA proxy.
// This is NOT Discord voice RTT, UDP jitter, or packet loss.
public sealed record QualitySample(DateTime MeasuredUtc, int? HttpsLatencyMs);

public sealed class ConnectionQualityHistory
{
    public const int MaximumSamples = 120; // 10 minutes at five-second intervals.
    private readonly List<QualitySample> history = new();
    public IReadOnlyList<QualitySample> Samples => history;
    public int Attempts => history.Count;
    public int Successful => history.Count(sample => sample.HttpsLatencyMs.HasValue);
    public int Failures => Attempts - Successful;
    public int FailurePercent => Attempts == 0 ? 0
        : (int)Math.Round(100.0 * Failures / Attempts, MidpointRounding.AwayFromZero);
    public int? LatestMs => history.LastOrDefault()?.HttpsLatencyMs;
    public int? MaximumMs => Successful == 0 ? null
        : history.Where(x => x.HttpsLatencyMs.HasValue)
            .Max(x => x.HttpsLatencyMs!.Value);
    public int? AverageMs => Successful == 0 ? null
        : (int)Math.Round(history.Where(x => x.HttpsLatencyMs.HasValue)
            .Average(x => x.HttpsLatencyMs!.Value));
    // Mean absolute adjacent change, not packet-delay variation from the voice stream.
    public int? VariationMs
    {
        get
        {
            var changes = new List<int>();
            for (int i = 1; i < history.Count; i++)
                if (history[i - 1].HttpsLatencyMs is int left
                    && history[i].HttpsLatencyMs is int right)
                    changes.Add(Math.Abs(right - left));
            return changes.Count == 0 ? null : (int)Math.Round(changes.Average());
        }
    }
    public void Add(DateTime utc, int? delayMs)
    {
        if (delayMs is <= 0 or > 60000) delayMs = null;
        history.Add(new QualitySample(utc, delayMs));
        if (history.Count > MaximumSamples)
            history.RemoveRange(0, history.Count - MaximumSamples);
    }
    public void Clear() => history.Clear();
}
