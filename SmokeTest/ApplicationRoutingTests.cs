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

public static class ApplicationRoutingTests
{
    private static void Check(bool ok, string label)
    {
        if (!ok) throw new Exception("APPLICATION_ROUTING_FAILED: " + label);
        Console.WriteLine("PASS " + label);
    }

    public static async Task RunAsync()
    {
        Check(!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PROSTOKVN_TEST_HOME")), "Isolated settings required");
        const string exe = @"C:\Program Files\Тест O'Brien\client.exe";
        Check(RouteTarget.TryParseApplication('"' + exe + '"', out var path)
            && path.Kind == RouteTargetKind.ProcessPath && path.DisplayValue == exe,
            "Executable picker paths preserve spaces, Unicode and apostrophes");
        Check(RouteTarget.TryParse(path.Value, out var restored) && restored == path,
            "Persisted application target round trips");
        Check(RouteTarget.TryParseApplication("CLIENT.exe", out var name)
            && name.Kind == RouteTargetKind.ProcessName
            && name.Matches(null, null, exe) && !name.Matches("client.exe", null, "other.exe"),
            "Process names match only the owner, ignoring Windows case");
        Check(path.Matches(null, null, "client.exe", exe.ToUpperInvariant())
            && !path.Matches(null, null, "client.exe", @"D:\Other\client.exe")
            && !path.Matches(null, null, "client.exe"), "Full paths do not affect a different copy of the same executable");
        foreach (string invalid in new[] { "", "app", "app.lnk", @"relative\app.exe", @"C:app.exe",
            @"C:\Apps,Other\app.exe", "bad\napp.exe", "*.exe", "process-name:app.exe",
            @"C:\App\app.exe:other.exe", @"C:\Bad|Path\app.exe" })
            Check(!RouteTarget.TryParseApplication(invalid, out _), "Invalid application target rejected: " + invalid.Replace('\n', ' '));
        Check(RouteTarget.TryParse("client.exe", out var domain) && domain.IsDomain
            && RouteTarget.TryParse("8.8.8.8", out var ip) && ip.IsAddress
            && RouteTarget.TryParse("2001:db8::/32", out var subnet) && subnet.IsSubnet,
            "Existing domain and IP targets retain their meaning");

        var node = new ProxyNode { Name = "TEST", Values = new()
        {
            ["name"] = "TEST", ["type"] = "socks5", ["server"] = "127.0.0.1", ["port"] = 19888
        }};
        var rules = new List<RouteRule>
        {
            new() { Domain = path.Value, Route = "REJECT", Manual = true, Label = "Тест" },
            new() { Domain = name.Value, Route = "VEGA", Manual = true },
            new() { Domain = "example.com", Route = "DIRECT", Manual = true },
            new() { Domain = "203.0.113.8", Route = "DIRECT", Manual = true }
        };
        UserStore.EnsureHome();
        UserStore.SaveRules(rules);
        Check(UserStore.LoadRules().Select(r => r.Domain).SequenceEqual(rules.Select(r => r.Domain))
            && rules[0].ServiceName == "Приложения" && rules[0].DisplayTarget == exe,
            "Application and legacy rules save together and display in their own group");

        foreach (string mode in new[] { "smart", "vpn", "direct" })
        foreach (bool hot in new[] { false, true })
        {
            var settings = new AppSettings { Mode = mode, Tun = true, SelectedNode = node.Name };
            var config = VpnCore.BuildConfig(new[] { node }, rules, settings, hotRules: hot);
            var generated = (List<string>)config["rules"]!;
            if (mode == "direct")
                Check(!generated.Any(r => r.StartsWith("PROCESS-PATH,")) && generated[^1] == "MATCH,DIRECT", "DIRECT ignores application exceptions");
            else if (!hot)
                Check(generated.IndexOf(path.ToRule("REJECT")) < generated.IndexOf(name.ToRule("VEGA"))
                    && generated.IndexOf(name.ToRule("VEGA")) < generated.IndexOf("DOMAIN-SUFFIX,example.com,DIRECT")
                    && generated.Contains(path.ToRule("REJECT")), "Path then process name precede site exceptions in " + mode);
            else
            {
                var plan = HotRuleProviders.Create(new[] { node }, rules, settings);
                HotRuleProviders.Stage(plan);
                Check(plan.Files.Values.Any(v => v.Contains("PROCESS-PATH," + exe.Replace("'", "''")))
                    && plan.Files.Values.Any(v => v.Contains("PROCESS-NAME,CLIENT.exe")), "Hot rule sets retain both application match types in " + mode);
            }
            string configPath = Path.Combine(UserStore.Home, "application-test.yaml");
            File.WriteAllText(configPath, YamlWriter.Serialize(config));
            string corePath = Environment.GetEnvironmentVariable("PROSTOKVN_MIHOMO_TEST_CORE")
                ?? throw new Exception("Real Mihomo path required");
            using var process = Process.Start(new ProcessStartInfo(corePath)
            {
                ArgumentList = { "-t", "-f", configPath, "-d", UserStore.Home },
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            })!;
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            await Task.WhenAll(output, error);
            Check(process.ExitCode == 0, "Real Mihomo accepts " + mode + (hot ? " hot" : " inline") + " application configuration: "
                + (process.ExitCode == 0 ? "valid" : output.Result + error.Result));
        }
        Check(VpnCore.IsStaleConnection("example.com", new[] { "DIRECT" }, rules, "smart", null, "client.exe")
            && !VpnCore.IsStaleConnection("example.com", new[] { "TEST", "VEGA" }, rules, "smart", null, "client.exe")
            && VpnCore.IsStaleConnection("example.com", new[] { "VEGA" }, rules, "smart", null, "client.exe", exe)
            && !VpnCore.IsStaleConnection("example.com", new[] { "DIRECT" }, rules, "smart", null, "other.exe"),
            "Only sessions affected by application routing are reconnected");
        UserStore.SaveRules(new List<RouteRule>());
        TestEditor();
        Console.WriteLine("ALL_APPLICATION_ROUTING_TESTS_PASS");
    }

    private static void TestEditor()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            MainWindow? window = null;
            try
            {
                window = new MainWindow { Left = -2200, Top = -2200, ShowInTaskbar = false, ShowActivated = false };
                T Get<T>(string name) where T : FrameworkElement => (T)window.FindName(name);
                void Invoke(string name) => typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(window, new object?[] { null, new RoutedEventArgs() });
                void Pump()
                {
                    var frame = new DispatcherFrame();
                    Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
                    Dispatcher.PushFrame(frame);
                    window.UpdateLayout();
                }
                window.Show();
                Pump();
                typeof(MainWindow).GetMethod("HideOnboarding", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
                Get<TabControl>("MainTabs").SelectedIndex = 2;
                Invoke("NewRule_Click");
                Check(Get<Button>("ChooseApplicationButton").Visibility == Visibility.Collapsed, "Site editor remains the default");
                Get<ComboBox>("RuleTargetKindBox").SelectedIndex = 1;
                Pump();
                Check(Get<Button>("ChooseApplicationButton").IsVisible && Get<TextBlock>("ApplicationRuleHint").IsVisible,
                    "Application editor exposes executable picker and TUN instructions");
                Get<TextBox>("DomainBox").Text = @"C:\Program Files\Test\client.exe";
                Get<TextBox>("RuleLabelBox").Text = "Тестовое приложение";
                Invoke("AddRule_Click");
                Pump();
                var saved = UserStore.LoadRules().Single(r => r.Manual);
                Check(saved.Domain == @"process-path:C:\Program Files\Test\client.exe" && saved.Route == "VEGA", "Editor saves application path and selected route");
                var list = Get<ListBox>("RulesList");
                var original = list.Items.Cast<RouteRule>().Single(r => r.Manual);
                list.SelectedItem = original;
                Check(Get<ComboBox>("RuleTargetKindBox").SelectedIndex == 1
                    && Get<TextBox>("DomainBox").Text == original.DisplayTarget, "Selecting a saved application restores the editor");
                Get<TextBox>("DomainBox").Text = "client.exe";
                Get<ComboBox>("RuleRouteBox").SelectedIndex = 2;
                Invoke("AddRule_Click");
                Pump();
                saved = UserStore.LoadRules().Single(r => r.Manual);
                Check(saved.Domain == "process-name:client.exe" && saved.Route == "REJECT", "Editing target replaces original without leaving a duplicate");
                Get<TextBox>("RuleSearchBox").Text = "client.exe";
                Check(list.Items.Count == 1, "Application search finds saved executables");
                list.SelectedItem = list.Items[0];
                Get<Expander>("AddRuleSection").IsExpanded = true;
                Get<TextBox>("RuleLabelBox").BringIntoView();
                Pump();
                var scroll = Get<ScrollViewer>("RulesPageScroll");
                scroll.ScrollToVerticalOffset(scroll.VerticalOffset
                    + Get<Expander>("AddRuleSection").TransformToAncestor(scroll).Transform(new Point()).Y - 4);
                Pump();
                string preview = Path.Combine(UserStore.Home, "application-editor.png");
                var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(window);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using (var file = File.Create(preview)) encoder.Save(file);
                Console.WriteLine("APPLICATION_EDITOR_PREVIEW=" + preview);
                Invoke("NewRule_Click");
                Check(Get<ComboBox>("RuleTargetKindBox").SelectedIndex == 0 && Get<TextBox>("DomainBox").Text == "", "New rule resets application editor");
            }
            catch (Exception e) { failure = e; }
            finally { window?.Close(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(40))) throw new TimeoutException("Application editor test timed out");
        if (failure != null) throw new Exception("Application editor regression", failure);
    }
}
