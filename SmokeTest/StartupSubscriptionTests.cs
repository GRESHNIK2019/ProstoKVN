using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ProstoKVN;

public static class StartupSubscriptionTests
{
    private static void Check(bool ok, string message)
    {
        if (!ok) throw new Exception("STARTUP_SUBSCRIPTION_FAILED: " + message);
        Console.WriteLine("PASS " + message);
    }
    private static void PumpUntil(Func<bool> predicate, string label)
    {
        var sw = Stopwatch.StartNew();
        while (!predicate() && sw.Elapsed < TimeSpan.FromSeconds(5))
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,
                new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
            Thread.Sleep(5);
        }
        Check(predicate(), label);
    }
    private static ProxyNode Node(string name)
        => new() { Name = name, Values = new Dictionary<string, object?> {
            ["name"] = name, ["type"] = "hysteria2",
            ["server"] = name == "Preferred B" ? "127.0.0.2" : "127.0.0.1",
            ["port"] = 24443
        }};
    private static MainWindow NewWindow() => new() {
        WindowStartupLocation = WindowStartupLocation.Manual,
        Left = -1850, Top = -1850, ShowActivated = false, ShowInTaskbar = false
    };
    private static string Url = "https://unit-test.invalid/sub/sample-token";

    public static void Run()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                UserStore.EnsureHome();
                UserStore.SaveSettings(new AppSettings {
                    Mode = "smart", Tun = true, AutoLearn = false,
                    SelectedNode = "Preferred B",
                    ProtectedUrl = UserStore.EncryptUrl(Url), Theme = "dark"
                });
                var window = NewWindow();
                int calls = 0;
                window.StartupSubscriptionLoader = input =>
                {
                    Check(input == Url, "Only decrypted saved subscription passed to loader");
                    calls++;
                    return Task.FromResult(new SubscriptionImporter.Result(input,
                        new List<ProxyNode> { Node("Fallback A"), Node("Preferred B") },
                        "2 сервера • SUB"));
                };
                window.Show();
                var combo = (ComboBox)window.FindName("ServerBox");
                try { PumpUntil(() => combo.Items.Count == 2, "Servers appear automatically after window opens"); }
                catch
                {
                    Console.WriteLine("DEBUG server-count=" + combo.Items.Count);
                    Console.WriteLine("DEBUG summary=" + ((TextBlock)window.FindName("ImportSummary")).Text);
                    Console.WriteLine("DEBUG log=" + ((TextBox)window.FindName("LogBox")).Text[^Math.Min(2500, ((TextBox)window.FindName("LogBox")).Text.Length)..]);
                    throw;
                }
                Check(calls == 1
                    && ((ProxyNode)combo.SelectedItem).Name.EndsWith(" · Preferred B"),
                    "Only one request; previously selected server restored");
                Check(((TextBlock)window.FindName("StatusLabel")).Text == "Не подключено",
                    "Subscription loading does not establish a VPN connection");
                var preferences = UserStore.LoadSettings();
                Check(preferences.Mode == "smart" && preferences.Tun
                    && !preferences.AutoLearn && preferences.Theme == "dark"
                    && preferences.SelectedNode.EndsWith(" · Preferred B")
                    && UserStore.DecryptUrl(preferences.ProtectedUrl) == Url,
                    "Subscription refresh preserves settings and encrypted URL");
                window.Close();

                UserStore.SaveSettings(new AppSettings {
                    ProtectedUrl = UserStore.EncryptUrl(Url), SelectedNode = "Preferred B"
                });
                var offline = NewWindow();
                int failureCalls = 0;
                offline.StartupSubscriptionLoader = url => {
                    failureCalls++;
                    return Task.FromException<SubscriptionImporter.Result>(
                        new InvalidOperationException("simulated offline"));
                };
                offline.Show();
                var summary = (TextBlock)offline.FindName("ImportSummary");
                PumpUntil(() => summary.Text.Contains("кэш, обновление недоступно"),
                    "Offline startup keeps cached servers without modal error");
                Check(failureCalls == 1
                    && ((ComboBox)offline.FindName("ServerBox")).Items.Count == 2
                    && ((Button)offline.FindName("RefreshButton")).IsEnabled
                    && ((Button)offline.FindName("ConnectButton")).IsEnabled,
                    "Offline startup retains two DPAPI-cached servers for manual retry");
                offline.Close();

                UserStore.SaveSettings(new AppSettings {
                    ProtectedUrl = UserStore.EncryptUrl(Url), SelectedNode = "Preferred B"
                });
                var stale = NewWindow();
                var pending = new TaskCompletionSource<SubscriptionImporter.Result>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                int staleCalls = 0;
                stale.StartupSubscriptionLoader = input => { staleCalls++; return pending.Task; };
                stale.Show();
                var staleSummary = (TextBlock)stale.FindName("ImportSummary");
                PumpUntil(() => staleCalls == 1, "Slow subscription load starts once");
                ((TextBox)stale.FindName("SubscriptionBox")).Text = "https://edited.invalid/sub/new";
                pending.SetResult(new SubscriptionImporter.Result(Url,
                    new List<ProxyNode>{Node("Stale A")}, "1 сервер"));
                PumpUntil(() => staleSummary.Text.Contains("Ссылка изменена"),
                    "Stale response cannot overwrite changed subscription");
                Check(((ComboBox)stale.FindName("ServerBox")).Items.Count == 2
                    && ((ProxyNode)((ComboBox)stale.FindName("ServerBox")).Items[0]).Name.EndsWith(" · Fallback A"),
                    "Stale network response cannot overwrite cached server list");
                stale.Close();

                SubscriptionProfileStore.Save(new List<SubscriptionProfile>());
                UserStore.SaveSettings(new AppSettings());
                var absent = NewWindow();
                int absentCalls = 0;
                absent.StartupSubscriptionLoader = _ => {
                    absentCalls++;
                    throw new Exception("Should not call loader without saved URL");
                };
                absent.Show();
                PumpUntil(() => ((OnboardingView)absent.FindName("OnboardingPanel")).IsVisible,
                    "First-run onboarding remains available without subscription");
                Check(absentCalls == 0, "No automatic import without saved HTTPS link");
                absent.Close();
                Console.WriteLine("ALL_STARTUP_SUBSCRIPTION_TESTS_PASS");
            }
            catch (Exception e) { failure = e; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(32)))
            throw new TimeoutException("Startup subscription UI test timed out");
        if (failure != null) throw new Exception("Startup subscription tests failed", failure);
    }
}
