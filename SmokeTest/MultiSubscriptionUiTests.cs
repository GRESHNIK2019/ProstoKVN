using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ProstoKVN;

public static class MultiSubscriptionUiTests
{
    private static void Check(bool condition, string title)
    {
        if (!condition) throw new Exception("MULTI_SUB_UI_FAILED: " + title);
        Console.WriteLine("PASS " + title);
    }

    private static void Pump(Func<bool> condition, string title)
    {
        DateTime end = DateTime.UtcNow.AddSeconds(15);
        while (!condition() && DateTime.UtcNow < end)
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,
                new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
            Thread.Sleep(7);
        }
        Check(condition(), title);
    }

    private static void Click(Button button)
        => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    public static void Run()
    {
        Exception? failed = null;
        var thread = new Thread(() =>
        {
            MainWindow? window = null;
            try
            {
                // Do not use the user's real subscriptions or network.
                SubscriptionTransport.TestVpnAvailable = () => Task.FromResult(false);
                string links = "vless://00000000-0000-0000-0000-000000000001@one.example:443?security=tls&sni=one.example#Provider%20One";
                string a = Convert.ToBase64String(Encoding.UTF8.GetBytes(links));
                string b = """
                proxies:
                  - name: EU Two
                    type: vless
                    server: two.example
                    port: 443
                    uuid: 00000000-0000-0000-0000-000000000002
                  - name: HY2 Two
                    type: hysteria2
                    server: three.example
                    port: 443
                    password: test
                """;
                SubscriptionTransport.TestFetch = (_, url) => Task.FromResult(
                    url.EndsWith("/provider-a") ? a : b);
                UserStore.SaveSettings(new AppSettings {
                    Theme = Environment.GetEnvironmentVariable("PROSTOKVN_PREVIEW_THEME") == "dark"
                        ? "dark" : "light"
                });
                window = new MainWindow {
                    Left = -2000, Top = -2000,
                    ShowActivated = false, ShowInTaskbar = false,
                    Width = 920, Height = 710
                };
                window.Show();
                var hide = typeof(MainWindow).GetMethod("HideOnboarding",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                Check(hide != null, "Onboarding can be exited for isolated GUI test");
                hide!.Invoke(window, null);
                var tabs = (TabControl)window.FindName("MainTabs");
                tabs.SelectedIndex = 2;
                var section = (Expander)window.FindName("SubscriptionSection");
                section.IsExpanded = true;
                window.UpdateLayout();

                var list = (ListBox)window.FindName("SubscriptionList")!;
                var name = (TextBox)window.FindName("SubscriptionNameBox")!;
                var url = (TextBox)window.FindName("SubscriptionBox")!;
                var format = (ComboBox)window.FindName("SubscriptionFormatBox")!;
                var import = (Button)window.FindName("ImportButton")!;
                var add = (Button)window.FindName("NewSubscriptionButton")!;
                var remove = (Button)window.FindName("RemoveSubscriptionButton")!;
                var refreshAll = (Button)window.FindName("RefreshAllSubscriptionsButton")!;
                var serverBox = (ComboBox)window.FindName("ServerBox")!;
                Check(window.FindName("SubscriptionList") != null
                    && window.FindName("SubscriptionNameBox") != null
                    && window.FindName("ImportButton") != null
                    && window.FindName("RemoveSubscriptionButton") != null,
                    "Multi-profile WPF controls exist");
                Check(list.BorderThickness == new Thickness(0)
                    && list.Background is SolidColorBrush background
                    && background.Color.A == 0
                    && list.Padding == new Thickness(0),
                    "Subscription list has transparent background with no default WPF white frame");

                Click(add);
                name.Text = "Основная тестовая";
                url.Text = "https://unit.test/feed/provider-a";
                format.SelectedIndex = 0;
                Click(import);
                Pump(() => list.Items.Count == 1 && serverBox.Items.Count == 1 && import.IsEnabled,
                    "First Base64 subscription added from user interface");
                Check(SubscriptionProfileStore.Load(UserStore.LoadSettings()).Count == 1,
                    "First subscription persisted encrypted");

                Click(add);
                name.Text = "Резерв";
                url.Text = "https://unit.test/feed/provider-b";
                format.SelectedIndex = 0;
                Click(import);
                Pump(() => list.Items.Count == 2 && serverBox.Items.Count == 3 && import.IsEnabled,
                    "Second YAML subscription merges nodes without connecting VPN");
                Check(serverBox.Items.Cast<ProxyNode>().Count(x => x.Name.StartsWith("Резерв · ")) == 2,
                    "Merged server names identify source subscriptions");

                list.SelectedIndex = 0;
                window.UpdateLayout();
                Check(name.Text == "Основная тестовая"
                    && url.Text.EndsWith("/provider-a"),
                    "Clicking first subscription restores its editor fields");
                list.SelectedIndex = 1;
                window.UpdateLayout();
                Check(name.Text == "Резерв" && url.Text.EndsWith("/provider-b"),
                    "Switching profiles changes editor without deleting any source");
                Check(remove.IsEnabled && refreshAll.IsEnabled,
                    "Profile management actions remain active");
                string json = File.ReadAllText(Path.Combine(UserStore.Home, "subscriptions.json"));
                Check(!json.Contains("/provider-a") && !json.Contains("/provider-b"),
                    "Profile persistence does not leak raw subscription URLs");
                string? preview = Environment.GetEnvironmentVariable("PROSTOKVN_MULTI_UI_PREVIEW");
                if (!string.IsNullOrWhiteSpace(preview))
                {
                    window.UpdateLayout();
                    int width = Math.Max(750, (int)window.ActualWidth);
                    int height = Math.Max(485, (int)window.ActualHeight);
                    var render = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                    render.Render(window);
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(render));
                    using var stream = File.Create(preview);
                    encoder.Save(stream);
                    Console.WriteLine("MULTI_SUBSCRIPTION_PREVIEW=" + preview);
                }

                var overrideField = typeof(MainWindow).GetField("confirmRemoveSubscriptionOverride",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                Check(overrideField != null, "Delete can be tested without a modal dialog");
                overrideField!.SetValue(window, new Func<SubscriptionProfile, bool>(_ => false));
                Click(remove);
                Check(list.Items.Count == 2, "Cancelling deletion preserves profiles");
                overrideField.SetValue(window, new Func<SubscriptionProfile, bool>(_ => true));
                Click(remove);
                window.UpdateLayout();
                Check(list.Items.Count == 1 && serverBox.Items.Count == 1
                    && SubscriptionProfileStore.Load(UserStore.LoadSettings()).Count == 1,
                    "Confirmed deletion removes only selected profile and its nodes");
                Check(((TextBlock)window.FindName("StatusLabel")).Text == "Не подключено",
                    "Subscription management never connects the VPN automatically");
                Console.WriteLine("ALL_MULTI_SUBSCRIPTION_UI_TESTS_PASS");
            }
            catch (Exception ex)
            {
                if (window != null)
                {
                    try { Console.WriteLine("UI_LOG_TAIL=" +
                        ((TextBox)window.FindName("LogBox")).Text[^Math.Min(1100,
                        ((TextBox)window.FindName("LogBox")).Text.Length)..]); }
                    catch { }
                }
                failed = ex;
            }
            finally
            {
                try { window?.Close(); } catch { }
                SubscriptionTransport.TestFetch = null;
                SubscriptionTransport.TestVpnAvailable = null;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(60))) throw new TimeoutException("Multi subscriptions GUI test timed out");
        if (failed != null) throw new Exception("GUI multi subscriptions regression failed", failed);
    }
}
