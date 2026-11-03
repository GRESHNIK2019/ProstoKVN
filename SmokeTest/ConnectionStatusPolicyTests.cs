using System;
using ProstoKVN;

public static class ConnectionStatusPolicyTests
{
    private static void Check(bool ok, string name)
    {
        if (!ok) throw new Exception("VERIFY_RETRY_TEST_FAILED: " + name);
        Console.WriteLine("PASS " + name);
    }

    public static void Run()
    {
        var now = new DateTime(2026, 11, 3, 8, 30, 0, DateTimeKind.Utc);
        var failedAt = now.AddSeconds(-31);
        Check(ConnectionStatusPolicy.ShouldRetry(true, true, false, false, failedAt, now),
            "Failed initial exit verification is retried without restarting network cores");
        Check(!ConnectionStatusPolicy.ShouldRetry(true, true, false, false, now.AddSeconds(-10), now),
            "Failed checks are throttled for thirty seconds");
        Check(!ConnectionStatusPolicy.ShouldRetry(true, true, true, false, failedAt, now),
            "Verified VPN does not trigger unnecessary repeat checks");
        Check(!ConnectionStatusPolicy.ShouldRetry(true, true, false, true, failedAt, now),
            "Overlapping verification is blocked");
        Check(!ConnectionStatusPolicy.ShouldRetry(false, true, false, false, failedAt, now),
            "Manual disconnect disables rechecks");
        Check(!ConnectionStatusPolicy.ShouldRetry(true, false, false, false, failedAt, now),
            "Unhealthy local transport is handled by recovery, not by an HTTP probe");
        Check(ConnectionStatusPolicy.ShouldRetry(true, true, false, false, DateTime.MinValue, now),
            "First check is allowed if no previous check exists");
        Check(ConnectionStatusPolicy.MonitorText(false).Contains("не подтверждён"),
            "An unverified exit is not labelled VPN working");
        Check(ConnectionStatusPolicy.MonitorText(true).Contains("подтверждён")
            && !ConnectionStatusPolicy.MonitorText(true).Contains("не подтверждён"),
            "Confirmed VPN exit is shown as connected");
        Check(ConnectionStatusPolicy.MonitorText(false, true).Contains("Прямое"),
            "Direct mode never reports VPN verified");
        Console.WriteLine("ALL_CONNECTION_STATUS_POLICY_TESTS_PASS");
    }
}