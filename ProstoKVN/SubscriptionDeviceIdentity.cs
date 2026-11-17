using Microsoft.Win32;
using System.IO;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace ProstoKVN;

public static class SubscriptionDeviceIdentity
{
    private static readonly Lazy<string> Identity = new(Create);
    public static string Id => Identity.Value;

    private static string Create()
    {
        try
        {
            string machine = Convert.ToString(Registry.GetValue(
                @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Cryptography", "MachineGuid", null)) ?? "";
            using var user = WindowsIdentity.GetCurrent();
            string sid = user.User?.Value ?? "";
            if (machine.Length > 0 && sid.Length > 0)
                return Hash("ProstoKVN|" + machine + "|" + sid);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException or IOException) { }

        // Registry access can be restricted. Keep one protected identifier for
        // that installation instead of creating a new device on every request.
        UserStore.EnsureHome();
        string path = Path.Combine(UserStore.Home, "subscription-device-id.dpapi");
        if (File.Exists(path))
        {
            byte[] plain = ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser);
            try
            {
                string restored = Encoding.UTF8.GetString(plain);
                if (restored.Length == 64 && restored.All(Uri.IsHexDigit)) return restored;
                throw new InvalidOperationException("Не удалось прочитать идентификатор устройства подписки.");
            }
            finally { CryptographicOperations.ZeroMemory(plain); }
        }
        string id = Hash(Guid.NewGuid().ToString("N"));
        byte[] bytes = Encoding.UTF8.GetBytes(id);
        try
        {
            File.WriteAllBytes(path, ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser));
            return id;
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    private static string Hash(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
