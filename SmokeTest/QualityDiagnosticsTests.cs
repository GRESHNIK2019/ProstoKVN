using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ProstoKVN;

public static class QualityDiagnosticsTests
{
    private static void Check(bool condition, string description)
    {
        if (!condition) throw new Exception("QUALITY_DIAGNOSTICS_FAILED: " + description);
        Console.WriteLine("PASS " + description);
    }

    public static void Run()
    {
        Check(!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PROSTOKVN_TEST_HOME")),
            "No access to real VPN configuration or settings");
        var h = new ConnectionQualityHistory();
        var time = new DateTime(2026, 11, 20, 9, 0, 0, DateTimeKind.Utc);
        foreach (int? sample in new int?[] { 40, 45, null, 100, 110 })
            h.Add(time = time.AddSeconds(5), sample);
        Check(h.Attempts == 5 && h.Successful == 4 && h.Failures == 1
                && h.FailurePercent == 20, "Counts HTTPS failures, not UDP packet loss");
        Check(h.MaximumMs == 110 && h.AverageMs == 74
                && h.LatestMs == 110 && h.VariationMs == 8,
            "Calculates peak, mean and variation only between adjacent successful probes");
        h.Clear();
        h.Add(time, null);
        h.Add(time.AddSeconds(5), null);
        Check(h.MaximumMs == null && h.AverageMs == null && h.VariationMs == null
                && h.FailurePercent == 100, "All failed probes have no fabricated delay or jitter");
        h.Clear();
        for (int i = 0; i < 145; i++) h.Add(time.AddSeconds(5 * i), i + 10);
        Check(h.Samples.Count == ConnectionQualityHistory.MaximumSamples
                && h.Samples[0].HttpsLatencyMs == 35
                && h.LatestMs == 154, "Keeps only last 10 minutes of quality samples");

        const string json = """
            {"connections":[
                {"id":"one","metadata":{"network":"udp","process":"Discord.exe"},"chains":["TEST","VEGA"],"upload":50,"download":45},
                {"id":"two","metadata":{"network":"udp","process":"Discord.exe"},"chains":["DIRECT"],"upload":40,"download":20},
                {"id":"three","metadata":{"network":"udp","process":"Discord.exe"},"upload":10,"download":10},
                {"id":"four","metadata":{"network":"udp","process":"browser.exe"},"chains":["DIRECT"],"upload":5,"download":6},
                {"id":"five","metadata":{"network":"tcp","process":"Discord.exe"},"chains":["VEGA"],"upload":25,"download":25}
            ]}
            """;
        var metrics = new ConnectionMetrics().Parse(json, time, 42);
        Check(metrics.UdpConnections == 4 && metrics.DiscordUdpConnections == 3
                && metrics.DiscordVpnUdpConnections == 1
                && metrics.DiscordDirectUdpConnections == 1
                && metrics.DiscordUnknownUdpConnections == 1,
            "Classifies Discord UDP chains without mistaking TCP or unknown paths for VPN");
        Check(metrics.HttpsLatencyMs == 42, "Existing connection monitoring is preserved");

        Exception? failure = null;
        var thread = new Thread(() =>
        {
            MainWindow? window = null;
            try
            {
                window = new MainWindow
                {
                    Left = -2100, Top = -2100, ShowInTaskbar = false, ShowActivated = false
                };
                T Find<T>(string name) where T : FrameworkElement => (T)window.FindName(name);
                window.Show();
                typeof(MainWindow).GetMethod("HideOnboarding",
                    BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, null);
                Find<TabControl>("MainTabs").SelectedIndex = 4;
                var frame = new DispatcherFrame();
                Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,
                    new Action(() => frame.Continue = false));
                Dispatcher.PushFrame(frame);
                window.UpdateLayout();
                Check(Find<Button>("QualityStartButton").Content?.ToString()
                        == "Начать диагностику" && !Find<Button>("QualityStartButton").IsEnabled,
                    "The diagnostic probe is opt-in and unavailable while disconnected");
                Check(Find<TextBlock>("QualityUdpRoutesText").Text.Contains("Discord UDP"),
                    "Discord UDP route information is on the help screen");
                var history = (ConnectionQualityHistory)typeof(MainWindow)
                    .GetField("qualityHistory", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .GetValue(window)!;
                history.Add(time, 42);
                history.Add(time.AddSeconds(5), 75);
                typeof(MainWindow).GetMethod("UpdateQualityUi",
                    BindingFlags.NonPublic | BindingFlags.Instance)!
                    .Invoke(window, new object[] { true });
                Check(Find<TextBlock>("QualityCurrentText").Text == "75 мс"
                        && Find<TextBlock>("QualityVariationText").Text == "33 мс"
                        && Find<TextBlock>("QualityPeakText").Text == "75 мс",
                    "UI renders measured HTTPS delay, estimated variation and peak");
                Check(Find<TextBlock>("QualityGraphHint").Text.Contains("2/2"),
                    "UI renders probe success totals");
                window.Close();
                Console.WriteLine("ALL_QUALITY_DIAGNOSTICS_UI_PASS");
            }
            catch (Exception ex)
            {
                failure = ex;
                try { window?.Close(); } catch { }
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(35)))
            throw new Exception("Quality diagnostic UI test timed out");
        if (failure != null) throw new Exception("Quality diagnostic UI test failed", failure);
        Console.WriteLine("ALL_QUALITY_DIAGNOSTICS_TESTS_PASS");
    }
}
