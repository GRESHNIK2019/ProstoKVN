using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ProstoKVN;

public static class SubscriptionLimitTests
{
    static void Check(bool value, string label)
    {
        if (!value) throw new Exception("SUBSCRIPTION_LIMIT_FAILED: " + label);
        Console.WriteLine("PASS " + label);
    }
    static List<ProxyNode> Nodes(string prefix, int count) => Enumerable.Range(0, count).Select(i => new ProxyNode
    {
        Name = "Сервер " + i,
        Values = new() { ["name"] = "Сервер " + i, ["type"] = "hysteria2",
            ["server"] = prefix + "-" + i + ".example", ["port"] = 443, ["password"] = "fixture" }
    }).ToList();
    static string Yaml(string prefix, int count) => "proxies:\n" + string.Concat(Enumerable.Range(0, count).Select(i =>
        $"  - name: Server {i}\n    type: hysteria2\n    server: {prefix}-{i}.example\n    port: 443\n    password: fixture\n"));

    public static async Task RunAsync()
    {
        Check(!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PROSTOKVN_TEST_HOME")), "Isolated data required");
        SubscriptionTransport.TestVpnAvailable = () => Task.FromResult(false);
        var bodies = new Dictionary<string, string>
        {
            ["mihomo"] = Yaml("yaml", 125),
            ["base64"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(string.Join("\n", Enumerable.Range(0,125)
                .Select(i => $"hysteria2://fixture@links-{i}.example:443#Server{i}")))),
            ["singbox"] = JsonSerializer.Serialize(new { outbounds = Enumerable.Range(0,125).Select(i => new {
                type = "hysteria2", tag = "Server " + i, server = "sing-" + i + ".example", server_port = 443, password = "fixture" }) }),
            ["xray"] = JsonSerializer.Serialize(Enumerable.Range(0,125).Select(i => new { remarks = "Server " + i,
                outbounds = new[] { new { protocol = "shadowsocks", settings = new {
                    servers = new[] { new { address = "xray-" + i + ".example", port = 443, method = "aes-128-gcm", password = "fixture" } } } } } }))
        };
        try
        {
            foreach (var (format, body) in bodies)
            {
                SubscriptionTransport.TestFetch = (_, _) => Task.FromResult(body);
                var result = await SubscriptionImporter.LoadAsync("https://unit.test/feed/" + format, format);
                Check(result.Nodes.Count == 100 && result.ServerLimitReached, format + " retains 100 servers and reports truncation");
            }
            SubscriptionTransport.TestFetch = (_, url) => Task.FromResult(url.Contains("/sub/") ? Yaml("first",60) : Yaml("second",60));
            var combined = await SubscriptionImporter.LoadAsync("https://unit.test/sub/combined");
            Check(combined.Nodes.Count == 100 && combined.ServerLimitReached, "3x-ui alternate endpoints share one 100-server limit");
            SubscriptionTransport.TestFetch = (_, _) => Task.FromResult(Yaml("exact",100));
            var exact = await SubscriptionImporter.LoadAsync("https://unit.test/feed/exact");
            Check(exact.Nodes.Count == 100 && !exact.ServerLimitReached, "Exactly 100 servers do not report discarded entries");
            var duplicateBody = "proxies:\n" + string.Concat(Enumerable.Range(0,130).Select(i =>
                $"  - name: Duplicate {i}\n    type: hysteria2\n    server: duplicate-{i % 80}.example\n    port: 443\n    password: fixture\n"));
            SubscriptionTransport.TestFetch = (_, _) => Task.FromResult(duplicateBody);
            var duplicates = await SubscriptionImporter.LoadAsync("https://unit.test/feed/duplicates");
            Check(duplicates.Nodes.Count == 80 && !duplicates.ServerLimitReached, "Duplicate servers do not consume separate limit entries");
            const string cacheUrl = "https://unit.test/feed/cache";
            Check(SubscriptionCache.Save(cacheUrl, Nodes("cache",125))
                && SubscriptionCache.TryLoad(cacheUrl,out var cached,out _) && cached.Count == 100,
                "Cache writes obey the per-subscription limit");
            const string legacyUrl = "https://unit.test/feed/legacy-cache";
            byte[] plain = JsonSerializer.SerializeToUtf8Bytes(Nodes("legacy",150));
            var envelope = new { UrlHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(legacyUrl))),
                SavedUtc = DateTime.UtcNow, ProtectedNodes = Convert.ToBase64String(ProtectedData.Protect(plain,null,DataProtectionScope.CurrentUser)) };
            File.WriteAllText(Path.Combine(UserStore.Home,"subscription-cache.json"),JsonSerializer.Serialize(envelope));
            Check(SubscriptionCache.TryLoad(legacyUrl,out var legacy,out _) && legacy.Count == 100,
                "Oversized caches from earlier versions load only 100 servers");
            var merged = SubscriptionProfileStore.Merge(new[] {
                (new SubscriptionProfile { Name = "First" }, (IReadOnlyList<ProxyNode>)Nodes("one",125)),
                (new SubscriptionProfile { Name = "Second" }, (IReadOnlyList<ProxyNode>)Nodes("two",125)) });
            Check(merged.Count == 200, "Limit is per link: two subscriptions retain 200 servers");
            TestManyServersUi();
            Console.WriteLine("ALL_SUBSCRIPTION_LIMIT_TESTS_PASS");
        }
        finally { SubscriptionTransport.TestFetch = null; SubscriptionTransport.TestVpnAvailable = null; }
    }

    static IEnumerable<T> Children<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++)
        {
            var child=VisualTreeHelper.GetChild(parent,i);
            if (child is T typed) yield return typed;
            foreach(var nested in Children<T>(child)) yield return nested;
        }
    }
    static void TestManyServersUi()
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(
                new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            MainWindow? w = null;
            try
            {
                var profiles = Enumerable.Range(0,12).Select(i => new SubscriptionProfile {
                    Name="Источник " + i, ProtectedUrl=UserStore.EncryptUrl("https://unit.test/feed/profile-"+i) }).ToList();
                UserStore.SaveSettings(new AppSettings { AutoLearn=false });
                SubscriptionProfileStore.Save(profiles);
                var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                w = new MainWindow { WindowStartupLocation=WindowStartupLocation.Manual,
                    Left=-2400,Top=-2400,ShowInTaskbar=false,ShowActivated=false };
                w.StartupSubscriptionLoader = async url => { await ready.Task;
                    return new(url,Nodes(url.Split('/')[^1],125),"125 servers"); };
                T Get<T>(string name) where T : FrameworkElement => (T)w.FindName(name);
                void Pump()
                {
                    var frame=new DispatcherFrame();
                    Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,new Action(()=>frame.Continue=false));
                    Dispatcher.PushFrame(frame); w.UpdateLayout();
                }
                void Until(Func<bool> predicate,string label)
                {
                    var end=DateTime.UtcNow.AddSeconds(15);
                    while(!predicate() && DateTime.UtcNow<end) { Pump(); Thread.Sleep(5); }
                    Check(predicate(),label);
                }
                void Click(string name) => Get<Button>(name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                void Save(string file)
                {
                    Pump(); var bitmap=new RenderTargetBitmap((int)w.ActualWidth,(int)w.ActualHeight,96,96,PixelFormats.Pbgra32);
                    bitmap.Render(w); var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var stream=File.Create(Path.Combine(UserStore.Home,file));encoder.Save(stream);
                }
                w.Show(); Pump();
                var tabs=Get<TabControl>("MainTabs"); tabs.SelectedIndex=1;
                Until(()=>!Get<Button>("ImportButton").IsEnabled,"Background refresh is in flight");
                Click("NewSubscriptionButton"); Pump();
                Get<TextBox>("SubscriptionNameBox").Text="Новая подписка";
                Get<TextBox>("SubscriptionBox").Text="https://unit.test/feed/new";
                ready.SetResult(true);
                Until(()=>Get<Button>("ImportButton").IsEnabled,"Large background subscription refresh completes");
                Check(Get<TextBox>("SubscriptionBox").Text=="https://unit.test/feed/new"
                    && Get<TextBox>("SubscriptionNameBox").Text=="Новая подписка"
                    && Get<ListBox>("SubscriptionList").SelectedItem==null,
                    "Background refresh preserves a new-link draft instead of replacing it");
                var servers=Get<ComboBox>("ServerBox");
                Check(servers.Items.Count==1200,"Startup caps each oversized result to 100 servers");
                var scroll=Get<ScrollViewer>("SubscriptionsPageScroll");
                foreach(string control in new[]{"NewSubscriptionButton","SubscriptionBox","ImportButton"})
                {
                    var element=Get<FrameworkElement>(control);
                    var bounds=element.TransformToAncestor(scroll).TransformBounds(new Rect(element.RenderSize));
                    Check(bounds.Top>=-1 && bounds.Bottom<=scroll.ViewportHeight+1,control+" is reachable above the long subscription list");
                }
                Save("01-add-link-large-list.png");
                SubscriptionTransport.TestFetch=(_,_)=>Task.FromResult(Yaml("new",125));
                Click("ImportButton");
                Until(()=>Get<ListBox>("SubscriptionList").Items.Count==13 && Get<Button>("ImportButton").IsEnabled,
                    "Another link can be added with 1200 existing servers");
                Check(servers.Items.Count==1300 && SubscriptionProfileStore.Load(UserStore.LoadSettings()).All(p=>p.NodeCount==100),
                    "New subscription contributes 100 servers without replacing other links");
                tabs.SelectedIndex=0;Pump();Save("02-home-spacious.png");
                servers.IsDropDownOpen=true;Pump();
                var popup=(Popup)servers.Template.FindName("PART_Popup",servers);
                Check(popup.IsOpen && ((FrameworkElement)popup.Child).ActualHeight<=servers.MaxDropDownHeight+1,
                    "1300-server menu is height-limited and scrolls");
                Check(Children<ComboBoxItem>(popup.Child).Count()<40,"Server menu virtualizes entries instead of creating 1300 rows");
                servers.IsDropDownOpen=false;
                w.Width=w.MinWidth;w.Height=w.MinHeight; tabs.SelectedIndex=1;
                Click("NewSubscriptionButton"); Pump();
                Get<TextBox>("SubscriptionBox").Text="https://unit.test/feed/next";
                ThemeManager.Apply(w.Resources,ThemeManager.Dark);Save("03-minimum-dark-add-link.png");
                Get<Button>("ImportButton").BringIntoView();Pump();
                var importBounds=Get<Button>("ImportButton").TransformToAncestor(w)
                    .TransformBounds(new Rect(Get<Button>("ImportButton").RenderSize));
                Check(importBounds.Top>=52 && importBounds.Bottom<w.ActualHeight,"Add action is reachable by scrolling in the minimum window");
                var pending=new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                SubscriptionTransport.TestFetch=(_,_)=>pending.Task;
                Click("ImportButton");
                Until(()=>!Get<Button>("ImportButton").IsEnabled,"Single import is pending");
                Click("NewSubscriptionButton");Pump();
                Get<TextBox>("SubscriptionNameBox").Text="Следующая ссылка";
                Get<TextBox>("SubscriptionBox").Text="https://unit.test/feed/another";
                pending.SetResult(Yaml("pending",125));
                Until(()=>Get<Button>("ImportButton").IsEnabled && Get<ListBox>("SubscriptionList").Items.Count==14,
                    "Completed import refreshes the list while another draft is open");
                Check(Get<TextBox>("SubscriptionBox").Text=="https://unit.test/feed/another"
                    && Get<TextBox>("SubscriptionNameBox").Text=="Следующая ссылка",
                    "An earlier import cannot overwrite the next link being entered");
                Console.WriteLine("SUBSCRIPTION_LIMIT_PREVIEWS="+UserStore.Home);
            }
            catch(Exception e) { error=e; }
            finally { w?.Close(); }
        });
        thread.SetApartmentState(ApartmentState.STA);thread.Start();
        if(!thread.Join(TimeSpan.FromSeconds(60))) throw new TimeoutException("Large subscription UI test timed out");
        if(error!=null) throw new Exception("Large subscription UI regression",error);
    }
}
