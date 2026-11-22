using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ProstoKVN;

public static class SimpleUiTests
{
    static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception("SIMPLE_UI_FAILED: " + message);
        Console.WriteLine("PASS " + message);
    }
    static void Invoke(MainWindow w, string method, params object?[] args)
        => typeof(MainWindow).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(w, args);
    static void Set(MainWindow w, string field, object value)
        => typeof(MainWindow).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(w, value);
    static T Get<T>(MainWindow w, string name) where T : FrameworkElement => (T)w.FindName(name);
    static void Pump(MainWindow w)
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
        w.UpdateLayout();
    }
    static void Save(MainWindow w, string name)
    {
        Pump(w);
        string directory = Environment.GetEnvironmentVariable("PROSTOKVN_SIMPLE_PREVIEW") ?? UserStore.Home;
        Directory.CreateDirectory(directory);
        var bitmap = new RenderTargetBitmap((int)w.ActualWidth, (int)w.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(w);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(directory, name + ".png"));
        encoder.Save(file);
    }
    static IEnumerable<FrameworkElement> Children(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is FrameworkElement element) yield return element;
            foreach (var nested in Children(child)) yield return nested;
        }
    }
    static void HomeFits(MainWindow w, string state)
    {
        Pump(w);
        var home = Get<Grid>(w, "HomePanel");
        Check(!Children(home).OfType<ScrollViewer>().Any(v => v.IsVisible),
            state + ": home contains no scrolling surface");
        foreach (string name in new[] { "ConnectionTitle", "ConnectionHint", "ServerBox", "ModeBox", "CoverageHint", "CoverageSettingsButton", "ConnectButton", "DisconnectButton", "PendingSettingsBanner" })
        {
            var element = Get<FrameworkElement>(w, name);
            if (!element.IsVisible) continue;
            var bounds = element.TransformToAncestor(home).TransformBounds(new Rect(element.RenderSize));
            Check(bounds.Left >= -1 && bounds.Top >= -1 && bounds.Right <= home.ActualWidth + 1
                && bounds.Bottom <= home.ActualHeight + 1,
                state + ": " + name + " fits inside the visible panel");
        }
    }
    static double Luminance(Color c)
    {
        static double Channel(byte v) => v / 255.0 <= 0.04045
            ? v / 3294.6 : Math.Pow((v / 255.0 + 0.055) / 1.055, 2.4);
        return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
    }
    static void CheckContrast(MainWindow w)
    {
        foreach (var pair in new[] { ("TextPrimary", "Canvas"), ("TextMuted", "Canvas"), ("TextMuted", "SurfaceRaised"), ("OnAccent", "AccentSolid") })
        {
            double a = Luminance(((SolidColorBrush)w.FindResource(pair.Item1)).Color);
            double b = Luminance(((SolidColorBrush)w.FindResource(pair.Item2)).Color);
            Check((Math.Max(a,b) + 0.05) / (Math.Min(a,b) + 0.05) >= 4.5,
                pair.Item1 + " on " + pair.Item2 + " passes normal-text contrast");
        }
    }

    public static void Run()
    {
        Check(!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PROSTOKVN_TEST_HOME")), "Isolated settings required");
        Exception? error = null;
        var thread = new Thread(() =>
        {
            MainWindow? w = null;
            Process? helper = null;
            VpnCore? core = null;
            try
            {
                UserStore.SaveSettings(new AppSettings());
                w = new MainWindow { Left = -2200, Top = -2200, ShowInTaskbar = false, ShowActivated = false };
                w.StartupSubscriptionLoader = _ => Task.FromException<SubscriptionImporter.Result>(new InvalidOperationException("Offline fixture"));
                w.Show();
                Pump(w);
                Check(Get<OnboardingView>(w, "OnboardingPanel").IsVisible, "Fresh install opens subscription setup");
                Check(!Children(Get<OnboardingView>(w, "OnboardingPanel")).OfType<System.Windows.Controls.Primitives.ScrollBar>()
                    .Any(v => v.IsVisible), "First-run setup fits without a scrollbar at the default size");
                Save(w, "01-welcome");
                Invoke(w, "HideOnboarding");
                var tabs = Get<TabControl>(w, "MainTabs");
                Check(tabs.Items.Cast<TabItem>().Select(t => (string)t.Header).SequenceEqual(new[] {
                    "Подключение", "Подписки", "Маршрутизация", "Настройки", "Мониторинг" }), "Task-based navigation is in expected order");
                Check(w.Width >= w.MinWidth && w.Width <= 1040 && w.Height >= w.MinHeight && w.Height <= 740,
                    "Window starts within the desktop with a spacious default size");
                Check(System.Windows.Shell.WindowChrome.GetWindowChrome(w)?.CaptionHeight == 52,
                    "Single custom caption supports native drag and resize");
                foreach (string name in new[] { "MinimizeWindowButton", "MaximizeWindowButton", "CloseWindowButton" })
                    Check(Get<Button>(w, name).IsEnabled, "Window action is available: " + name);
                CheckContrast(w);
                HomeFits(w, "Empty home");
                Check(Get<Button>(w, "ConnectButton").Content.ToString() == "Добавить подписку", "Empty home directs to adding a subscription");
                Check(Get<Button>(w, "DisconnectButton").Visibility == Visibility.Collapsed, "Only one primary connection action is shown at rest");
                Invoke(w, "SubscriptionsLink_Click", null, new RoutedEventArgs());
                Pump(w);
                Check(tabs.SelectedIndex == 1 && Get<Expander>(w, "SubscriptionEditor").IsExpanded,
                    "Subscription shortcut opens the editor when no servers exist");
                Save(w, "02-subscriptions-empty");
                tabs.SelectedIndex = 0;
                var nodes = new List<ProxyNode> { new() { Name = "Германия · Основной", Values = new() { ["type"] = "hysteria2" } }, new() { Name = "Нидерланды · Резервный" } };
                Set(w, "nodes", nodes);
                Get<ComboBox>(w, "ServerBox").ItemsSource = nodes;
                Get<ComboBox>(w, "ServerBox").SelectedIndex = 0;
                Invoke(w, "UpdateConnectionUi");
                Check(Get<Button>(w, "ConnectButton").Content.ToString() == "Подключиться", "Available servers enable the connection action");
                Save(w, "03-home-light");
                // A sleeping helper represents only the process-alive flag. No
                // network core, proxy, subscription or system setting is touched.
                helper = Process.Start(new ProcessStartInfo("powershell.exe", "-NoProfile -Command Start-Sleep -Seconds 90") {
                    UseShellExecute = false, CreateNoWindow = true });
                core = (VpnCore)typeof(MainWindow).GetField("core", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(w)!;
                typeof(VpnCore).GetField("process", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(core, helper);
                Set(w, "appliedMode", "smart");
                Set(w, "appliedTun", false);
                Invoke(w, "UpdateConnectionUi");
                Check(Get<TextBlock>(w, "ConnectionTitle").Text == "Проверяем соединение", "A running process never claims a verified VPN exit");
                Set(w, "vpnExitVerified", true);
                Invoke(w, "UpdateConnectionUi");
                Check(Get<TextBlock>(w, "ConnectionTitle").Text == "VPN подключён"
                    && Get<Button>(w, "ConnectButton").Content.ToString() == "Отключиться", "Verified connection has one disconnect action");
                Save(w, "04-connected");
                HomeFits(w, "Connected home");
                Get<ComboBox>(w, "ModeBox").SelectedValue = "vpn";
                Pump(w);
                Check(Get<Border>(w, "PendingSettingsBanner").Visibility == Visibility.Visible,
                    "Mode changes require visible explicit application");
                HomeFits(w, "Pending settings");
                w.Width = w.MinWidth;
                w.Height = w.MinHeight;
                HomeFits(w, "Minimum window with pending settings");
                Set(w, "connectionBusy", true);
                Invoke(w, "UpdateConnectionUi");
                HomeFits(w, "Minimum window connecting with pending settings");
                Check(!Get<Button>(w, "ConnectButton").IsVisible && Get<Button>(w, "DisconnectButton").IsVisible,
                    "Cancellation replaces the primary action without adding a row");
                Save(w, "04a-minimum-busy-pending");
                Set(w, "connectionBusy", false);
                Invoke(w, "UpdateConnectionUi");
                tabs.SelectedIndex = 2;
                Pump(w);
                Check(Get<Button>(w, "ApplyRulesButton").IsVisible, "Routing provides the apply action beside the changed settings");
                Get<Button>(w, "ApplyRulesButton").BringIntoView(); Pump(w);
                var applyBounds = Get<Button>(w,"ApplyRulesButton").TransformToAncestor(w).TransformBounds(new Rect(Get<Button>(w,"ApplyRulesButton").RenderSize));
                Check(applyBounds.Bottom<w.ActualHeight && applyBounds.Top>=52, "Pending changes remain reachable in a short window");
                Get<ComboBox>(w, "ModeBox").SelectedValue = "smart";
                Check(Get<Border>(w, "PendingSettingsBanner").Visibility == Visibility.Collapsed, "Reverting pending settings removes the apply banner");
                typeof(VpnCore).GetField("process", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(core, null);
                Set(w, "vpnExitVerified", false);
                Invoke(w, "UpdateConnectionUi");
                Save(w, "05-routing");
                tabs.SelectedIndex = 2;
                Set(w, "rules", new List<RouteRule> {
                    new() { Domain = "example.com", Label = "Работа", Route = "VEGA", Manual = true },
                    new() { Domain = "local.example", Label = "Домашняя сеть", Route = "DIRECT", Manual = true }
                });
                Invoke(w, "RefreshRules");
                Get<TextBox>(w, "RuleSearchBox").Text = "Работа";
                Check(Get<ListBox>(w, "RulesList").Items.Count == 1, "Search matches a user-defined label");
                Get<TextBox>(w, "RuleSearchBox").Text = "missing";
                Check(Get<TextBlock>(w, "RulesEmptyText").Visibility == Visibility.Visible, "No matching rules offers a recovery path");
                Get<TextBox>(w, "RuleSearchBox").Clear();
                Get<ListBox>(w, "RulesList").SelectedIndex = 0;
                Check(Get<Button>(w, "EditRuleButton").IsEnabled, "Selecting a rule enables a visible edit action");
                Invoke(w, "EditRule_Click", null, new RoutedEventArgs());
                Check(Get<Expander>(w, "AddRuleSection").IsExpanded && Get<TextBox>(w, "DomainBox").Text.Length > 0,
                    "Edit action opens the selected rule");
                Invoke(w, "NewRule_Click", null, new RoutedEventArgs());
                Check(Get<TextBox>(w, "DomainBox").Text == "" && Get<ComboBox>(w, "RuleRouteBox").SelectedIndex == 1,
                    "New rule clears the selection and defaults to VPN");
                Save(w, "06-rules");
                Invoke(w, "CloseRuleEditor_Click", null, new RoutedEventArgs());
                tabs.SelectedIndex = 4;
                Pump(w);
                Check(Get<TextBlock>(w, "StatusLabel").IsVisible && !Get<Expander>(w, "LogSection").IsExpanded,
                    "Diagnostics are available in Help with technical logs collapsed");
                Save(w, "07-help");
                Invoke(w, "ThemeButton_Click", null, new RoutedEventArgs());
                tabs.SelectedIndex = 0;
                w.Width = w.MinWidth;
                w.Height = w.MinHeight;
                Save(w, "08-home-dark-minimum");
                CheckContrast(w);
                HomeFits(w, "Dark minimum window");
                var server = Get<ComboBox>(w, "ServerBox");
                Check(server.ActualWidth > 300 && server.IsVisible, "Minimum window keeps server selection usable");
                typeof(VpnCore).GetField("process", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(core, helper);
                Set(w, "vpnExitVerified", true);
                Set(w, "appliedTun", true);
                Get<CheckBox>(w, "TunBox").IsChecked = true;
                Invoke(w, "SettingsChanged_Click", null, new RoutedEventArgs());
                Save(w, "08a-connected-dark-minimum");
                HomeFits(w, "Connected dark minimum with TUN");
                typeof(VpnCore).GetField("process", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(core, null);
                Set(w, "vpnExitVerified", false);
                Get<CheckBox>(w, "TunBox").IsChecked = false;
                Invoke(w, "SettingsChanged_Click", null, new RoutedEventArgs());
                tabs.SelectedIndex = 2;
                Save(w, "09-rules-dark-minimum");
                var ruleScroll = Get<ScrollViewer>(w, "RulesPageScroll");
                ruleScroll.ScrollToEnd();
                Pump(w);
                Check(ruleScroll.VerticalOffset > 0, "Long rule lists still scroll to the editor and saved rules");
                var bar = Children(ruleScroll).OfType<System.Windows.Controls.Primitives.ScrollBar>()
                    .First(b => b.IsVisible && b.Orientation == Orientation.Vertical);
                var lane = (Grid)bar.Template.FindName("ThinTrackLane", bar);
                Check(lane.ActualWidth == 8, "Long lists use the thin scrollbar instead of the system track");
                ruleScroll.ScrollToTop();
                var title = ((ScrollViewer)((TabItem)tabs.SelectedItem).Content).Content as StackPanel;
                var heading = (TextBlock)title!.Children[0];
                Check(heading.Foreground is SolidColorBrush brush && brush.Color.R > 150,
                    "Dark theme page heading keeps a readable foreground");
                Check(Get<TextBox>(w, "DomainBox").ActualWidth >= 105, "Rule editor remains usable at minimum width");
                var delayed = new TaskCompletionSource<string>();
                SubscriptionTransport.TestVpnAvailable = () => Task.FromResult(false);
                SubscriptionTransport.TestFetch = (_, _) => delayed.Task;
                Set(w, "nodes", new List<ProxyNode>());
                Set(w, "profiles", new List<SubscriptionProfile> { new() {
                    Name = "Delayed fixture", ProtectedUrl = UserStore.EncryptUrl("https://unit.test/cancel"), Format = "mihomo"
                } });
                tabs.SelectedIndex = 0;
                Invoke(w, "Connect_Click", null, new RoutedEventArgs());
                Check(Get<Button>(w, "DisconnectButton").Visibility == Visibility.Visible,
                    "Subscription loading offers an explicit cancellation action");
                Invoke(w, "Disconnect_Click", null, new RoutedEventArgs());
                delayed.SetResult("proxies:\n  - name: Cancelled node\n    type: hysteria2\n    server: example.com\n    port: 443\n    password: fixture\n");
                DateTime end = DateTime.UtcNow.AddSeconds(10);
                while (!Get<Button>(w, "ConnectButton").IsEnabled && DateTime.UtcNow < end) {
                    Pump(w); Thread.Sleep(5);
                }
                Check(Get<Button>(w, "ConnectButton").IsEnabled && !core.IsRunning
                    && !Get<TextBox>(w, "LogBox").Text.Contains("Ошибка запуска"),
                    "Cancelling a slow subscription load prevents a later core start");
                Console.WriteLine("ALL_SIMPLE_UI_TESTS_PASS");
            }
            catch (Exception ex) { error = ex; }
            finally
            {
                if (core != null) typeof(VpnCore).GetField("process", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(core, null);
                if (helper is { HasExited: false }) helper.Kill();
                SubscriptionTransport.TestFetch = null;
                SubscriptionTransport.TestVpnAvailable = null;
                w?.Close();
                if (w != null) Pump(w);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(55))) throw new TimeoutException("Simple UI test timed out");
        if (error != null) throw new Exception("Simple UI regression", error);
    }
}
