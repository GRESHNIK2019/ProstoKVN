using Microsoft.Win32;
using System;
using System.IO;
using System.Text.Json;

namespace ProstoKVN;

/// <summary>
/// Crash recovery for ONLY the proxy settings owned by this client.
/// A saved snapshot is never applied over proxy settings changed by another app.
/// </summary>
public static class SystemProxyRecovery
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";
    private const string OwnedOverride = "localhost;127.*;10.*;192.168.*;<local>";
    private static string StateFile => Path.Combine(UserStore.Home, "proxy-restore.json");
    private sealed record Snapshot(bool HadEnable, int OldEnable,
        bool HadServer, string? OldServer, bool HadOverride, string? OldOverride);

    public static bool IsOwned(int port, object? server, object? overrides)
        => string.Equals(Convert.ToString(server), $"127.0.0.1:{port}",
                StringComparison.OrdinalIgnoreCase)
            && string.Equals(Convert.ToString(overrides), OwnedOverride,
                StringComparison.OrdinalIgnoreCase);

    public static bool RecoverIfStale(int port)
    {
        if (!File.Exists(StateFile)) return false;
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true)
            ?? throw new InvalidOperationException("Cannot access Internet Settings registry key.");
        var saved = JsonSerializer.Deserialize<Snapshot>(File.ReadAllText(StateFile))
            ?? throw new InvalidOperationException("Invalid proxy-recovery snapshot.");
        bool owned = IsOwned(port, key.GetValue("ProxyServer"), key.GetValue("ProxyOverride"));
        if (owned)
        {
            RestoreValue(key, "ProxyEnable", saved.HadEnable, saved.OldEnable, RegistryValueKind.DWord);
            RestoreValue(key, "ProxyServer", saved.HadServer, saved.OldServer, RegistryValueKind.String);
            RestoreValue(key, "ProxyOverride", saved.HadOverride, saved.OldOverride, RegistryValueKind.String);
            NotifyInternetSettings();
        }
        // If another application changed the proxy, respect its configuration.
        File.Delete(StateFile);
        return owned;
    }

    public static void Enable(int port)
    {
        if (File.Exists(StateFile))
        {
            RecoverIfStale(port);
            if (File.Exists(StateFile))
                throw new InvalidOperationException("A previous proxy backup cannot be restored.");
        }
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true)
            ?? throw new InvalidOperationException("Cannot access Internet Settings registry key.");
        var oldEnable = key.GetValue("ProxyEnable");
        var oldServer = key.GetValue("ProxyServer");
        var oldOverride = key.GetValue("ProxyOverride");
        var snapshot = new Snapshot(oldEnable != null,
            oldEnable == null ? 0 : Convert.ToInt32(oldEnable),
            oldServer != null, Convert.ToString(oldServer),
            oldOverride != null, Convert.ToString(oldOverride));
        Directory.CreateDirectory(UserStore.Home);
        string temp = StateFile + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(snapshot));
            File.Move(temp, StateFile, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
        try
        {
            // Put ProxyEnable last: an interrupted startup cannot activate a half-written proxy.
            key.SetValue("ProxyOverride", OwnedOverride, RegistryValueKind.String);
            key.SetValue("ProxyServer", $"127.0.0.1:{port}", RegistryValueKind.String);
            key.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
            NotifyInternetSettings();
        }
        catch
        {
            RecoverIfStale(port);
            throw;
        }
    }

    public static void RestoreIfOwned(int port) => RecoverIfStale(port);

    private static void RestoreValue(RegistryKey key, string name, bool existed,
        object? previous, RegistryValueKind kind)
    {
        if (!existed) key.DeleteValue(name, throwOnMissingValue: false);
        else key.SetValue(name, previous ?? (kind == RegistryValueKind.DWord ? 0 : ""), kind);
    }

    [System.Runtime.InteropServices.DllImport("wininet.dll", SetLastError = true)]
    private static extern bool InternetSetOption(IntPtr hInternet, int option, IntPtr buffer, int length);

    private static void NotifyInternetSettings()
    {
        InternetSetOption(IntPtr.Zero, 39, IntPtr.Zero, 0);
        InternetSetOption(IntPtr.Zero, 37, IntPtr.Zero, 0);
    }
}