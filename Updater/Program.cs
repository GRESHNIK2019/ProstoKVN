using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

internal static class Program
{
    // Runs outside the application folder after the user explicitly accepts an update.
    // No admin escalation, services, Defender exclusions, persistence or silent installation.
    private static int Main(string[] args)
    {
        if (args.Length != 4 || args[0] != "--replace")
            return 2;
        string stage = Path.GetFullPath(args[1]);
        string target = Path.GetFullPath(args[2]);
        if (!int.TryParse(args[3], out int oldPid) || oldPid < 1) return 2;

        // A uniquely named stage under %LOCALAPPDATA%\ProstoKVN\updates.
        string owner = Path.GetFullPath(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ProstoKVN", "updates")).TrimEnd('\\') + "\\";
        if (!stage.StartsWith(owner, StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(stage).StartsWith("stage-", StringComparison.Ordinal)
            || Path.GetPathRoot(target) == target || target.StartsWith(owner,
                StringComparison.OrdinalIgnoreCase)
            || !File.Exists(Path.Combine(target, "ProstoKVN.exe")))
            return 3;
        string payload = Path.Combine(stage, "payload");
        string manifest = Path.Combine(stage, "hashes.json");
        if (!Directory.Exists(payload) || !File.Exists(manifest)) return 3;

        string log = Path.Combine(stage, "update-result.log");
        void Report(string line)
        {
            try { File.AppendAllText(log, DateTime.UtcNow.ToString("O") + " " + line + "\n"); }
            catch { }
        }

        bool launched = false;
        string backup = Path.Combine(stage, "rollback");
        var created = new List<string>();
        var overwritten = new List<(string dest, string backup)>();
        try
        {
            Report("Update helper launched. Waiting for previous UI process.");
            try
            {
                using var old = Process.GetProcessById(oldPid);
                if (!old.WaitForExit(45_000))
                    throw new InvalidOperationException("Previous ProstoKVN is still running.");
            }
            catch (ArgumentException) { /* already exited */ }

            var hashes = JsonSerializer.Deserialize(File.ReadAllText(manifest),
                ManifestJsonContext.Default.DictionaryStringString)
                ?? throw new InvalidOperationException("Update file manifest missing.");
            if (hashes.Count is < 3 or > 2500)
                throw new InvalidOperationException("Unexpected update manifest length.");

            var sourceRoot = Path.GetFullPath(payload).TrimEnd('\\') + "\\";
            var destRoot = Path.GetFullPath(target).TrimEnd('\\') + "\\";
            var backupRoot = Path.GetFullPath(backup).TrimEnd('\\') + "\\";
            foreach (var (relative, hash) in hashes)
            {
                string normalized = relative.Replace('/', '\\');
                if (Path.IsPathRooted(normalized) ||
                    normalized.Split('\\').Any(part => part is "." or ".." || part.Length == 0) ||
                    !System.Text.RegularExpressions.Regex.IsMatch(hash, "^[a-fA-F0-9]{64}$"))
                    throw new InvalidOperationException("Unsafe update file manifest.");
                string source = Path.GetFullPath(Path.Combine(payload, normalized));
                if (!source.StartsWith(sourceRoot, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Unsafe update path.");
                if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("Symlink files are not permitted.");
                string actual = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source)));
                if (!hash.Equals(actual, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Update staging changed after download.");
            }
            if (!hashes.Keys.Any(k => k.Equals("ProstoKVN.exe", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("Update executable missing.");
            Directory.CreateDirectory(backup);
            foreach (var relative in hashes.Keys)
            {
                string normalized = relative.Replace('/', '\\');
                string source = Path.GetFullPath(Path.Combine(payload, normalized));
                string dest = Path.GetFullPath(Path.Combine(target, normalized));
                if (!dest.StartsWith(destRoot, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Unsafe installation path.");
                string parent = Path.GetDirectoryName(dest)!;
                if (Directory.Exists(parent)
                    && (new DirectoryInfo(parent).Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("Refusing to write into a junction.");
                Directory.CreateDirectory(parent);
                if (File.Exists(dest))
                {
                    string saved = Path.GetFullPath(Path.Combine(backup, normalized));
                    if (!saved.StartsWith(backupRoot, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Unsafe backup path.");
                    Directory.CreateDirectory(Path.GetDirectoryName(saved)!);
                    File.Copy(dest, saved, true);
                    overwritten.Add((dest, saved));
                }
                else created.Add(dest);
                File.Copy(source, dest, true);
            }
            Report($"Applied {hashes.Count} update files. Restarting ProstoKVN.");
            var result = Process.Start(new ProcessStartInfo(Path.Combine(target, "ProstoKVN.exe"))
            {
                WorkingDirectory = target,
                UseShellExecute = true
            });
            if (result == null)
                throw new InvalidOperationException("Updated program could not be started.");
            Thread.Sleep(3500);
            if (result.HasExited)
                throw new InvalidOperationException("Updated program terminated immediately.");
            launched = true;
            Report("Updated ProstoKVN started.");
            return 0;
        }
        catch (Exception ex)
        {
            Report("Install failed: " + ex.GetType().Name + " " + ex.Message);
            if (!launched)
            {
                try
                {
                    foreach (string path in created.AsEnumerable().Reverse())
                        if (File.Exists(path)) File.Delete(path);
                    foreach (var (dest, original) in overwritten.AsEnumerable().Reverse())
                        if (File.Exists(original)) File.Copy(original, dest, true);
                    Report("Previous files restored.");
                    Process.Start(new ProcessStartInfo(Path.Combine(target, "ProstoKVN.exe"))
                    {
                        WorkingDirectory = target, UseShellExecute = true
                    });
                }
                catch (Exception rollback)
                {
                    Report("Rollback error: " + rollback.Message);
                }
            }
            return 1;
        }
    }
}
