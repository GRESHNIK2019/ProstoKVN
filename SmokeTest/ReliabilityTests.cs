using System;
using System.IO;
using System.Linq;
using ProstoKVN;

public static class ReliabilityTests
{
    private static void Check(bool yes, string label)
    {
        if (!yes) throw new Exception("RELIABILITY_TEST_FAILED: " + label);
        Console.WriteLine("PASS " + label);
    }

    public static void Run()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PROSTOKVN_TEST_HOME")))
            throw new Exception("Reliability tests require an isolated PROSTOKVN_TEST_HOME.");

        Check(!SystemProxyRecovery.IsOwned(VpnCore.MainPort, "otherproxy:3128",
                    "localhost;127.*;10.*;192.168.*;<local>")
              && SystemProxyRecovery.IsOwned(VpnCore.MainPort, "127.0.0.1:17890",
                    "localhost;127.*;10.*;192.168.*;<local>"),
            "Proxy recovery checks ownership and does not touch other proxies");

        var first = new AppSettings { Mode = "vpn", SelectedNode = "ONE", AutoLearn = true };
        var second = new AppSettings { Mode = "smart", SelectedNode = "TWO", AutoLearn = false };
        UserStore.SaveSettings(first);
        UserStore.SaveSettings(second);
        Check(UserStore.LoadSettings().SelectedNode == "TWO", "Atomic settings write");
        File.WriteAllText(Path.Combine(UserStore.Home, "settings.json"), "{broken");
        Check(UserStore.LoadSettings().SelectedNode == "ONE", "Settings recovered from valid backup");

        UserStore.SaveRules(new[] { new RouteRule { Domain = "example.com", Route = "VEGA" } });
        UserStore.SaveRules(new[] { new RouteRule { Domain = "openai.com", Route = "DIRECT" } });
        File.WriteAllText(Path.Combine(UserStore.Home, "routes.json"), "[broken");
        Check(UserStore.LoadRules().Single().Domain == "example.com",
            "Routing rules recovered from valid backup");

        string updates = Path.Combine(UserStore.Home, "updates");
        Directory.CreateDirectory(updates);
        string completed = Path.Combine(updates, "stage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(completed);
        File.WriteAllText(Path.Combine(completed, "update-result.log"),
            "Updated ProstoKVN started.");
        Directory.SetCreationTimeUtc(completed, DateTime.UtcNow.AddDays(-2));
        string fresh = Path.Combine(updates, "stage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fresh);
        string unrelated = Path.Combine(updates, "stage-not-a-guid");
        Directory.CreateDirectory(unrelated);
        Directory.SetCreationTimeUtc(unrelated, DateTime.UtcNow.AddDays(-30));
        GitHubUpdateService.CleanupOldStages();
        Check(!Directory.Exists(completed) && Directory.Exists(fresh) && Directory.Exists(unrelated),
            "Only safe, completed, aged update directories are cleaned");

        Check(SubscriptionTransport.MaxResponseBytes == 8 * 1024 * 1024,
            "Subscription download size cap is configured");
        Console.WriteLine("ALL_RELIABILITY_TESTS_PASS");
    }
}