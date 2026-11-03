using System;

namespace ProstoKVN;

/// <summary>
/// Distinguishes a running local proxy from a verified VPN exit.
/// Read-only status logic: never starts or restarts any networking core.
/// </summary>
public static class ConnectionStatusPolicy
{
    public static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(30);

    public static bool ShouldRetry(bool desired, bool transportHealthy,
        bool exitVerified, bool verificationBusy, DateTime lastCheckUtc, DateTime nowUtc)
    {
        return desired && transportHealthy && !exitVerified && !verificationBusy
            && (lastCheckUtc == DateTime.MinValue
                || nowUtc - lastCheckUtc >= RetryInterval);
    }

    public static string MonitorText(bool exitVerified, bool directMode = false)
    {
        if (directMode) return "Прямое соединение • контроль каждые 12 секунд";
        return exitVerified
            ? "VPN-выход подтверждён • контроль каждые 12 секунд"
            : "Ядра запущены • VPN-выход не подтверждён, проверю повторно";
    }
}
