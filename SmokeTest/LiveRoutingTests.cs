using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ProstoKVN;

public static class LiveRoutingTests
{
    const BindingFlags Private=BindingFlags.Instance|BindingFlags.NonPublic;
    static void Check(bool ok,string text){if(!ok)throw new Exception("LIVE_ROUTING_FAILED: "+text);Console.WriteLine("PASS "+text);}
    static object Flow(string id,string process,string host,string[] chain,long up=0,long down=0,string rule="Match",string payload="")
        => new {id,upload=up,download=down,rule,rulePayload=payload,chains=chain,metadata=new {process,processPath="",host,destinationIP="198.51.100.8",destinationPort=443,network=process=="game.exe"?"udp":"tcp"}};
    static string Sample(long up=0,long down=0)=>JsonSerializer.Serialize(new {connections=new[]{
        Flow("a","chrome.exe","direct.example.ru",new[]{"DIRECT"},up,down),
        Flow("b","browser.exe","selected.example",new[]{"TEST","VEGA"},up,down,"RuleSet","user"),
        Flow("c","game.exe","",new[]{"DIRECT"},up,down,"RuleSet","server"),
        Flow("d","app.exe","unknown.test",Array.Empty<string>()),
        Flow("e","app.exe","conflict.test",new[]{"DIRECT","VEGA"}),
        Flow("f","app.exe","blocked.test",new[]{"REJECT"}),
        Flow("g","xray.exe","transport.test",new[]{"DIRECT"}),
        Flow("h","app.exe","probe.test",new[]{"VEGA"},rule:"InName",payload:"vega-probe-vpn"),
        Flow("i","app.exe","pinned.test",new[]{"PINNED"})}});
    public static async Task RunAsync()
    {
        Check(!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PROSTOKVN_TEST_HOME")),"Isolated test home required");UserStore.EnsureHome();
        var monitor=new LiveConnectionMonitor();var now=DateTime.UtcNow;
        var providers=new Dictionary<string,string>{{"user",HotRuleProviders.Render(new[]{"DOMAIN-SUFFIX,selected.example"})},{"server",HotRuleProviders.Render(new[]{"IP-CIDR,198.51.100.8/32,no-resolve"})}};
        var rows=monitor.Read(Sample(),now,providers,new[]{"PINNED"});
        Check(rows.Count==7,"Engine transports and diagnostic probes are hidden");
        Check(rows.Single(r=>r.Id=="a").Route=="DIRECT" && rows.Single(r=>r.Id=="b").Route=="VPN" && rows.Single(r=>r.Id=="i").Route=="VPN","Routes come from actual DIRECT, VEGA and preferred-node chains");
        Check(rows.Count(r=>r.Route=="Неизвестно")==2 && rows.Single(r=>r.Id=="f").Route=="Блокировка","Missing and conflicting chains remain unknown; rejects are explicit");
        Check(rows.Single(r=>r.Id=="b").Rule.Contains("selected.example") && rows.Single(r=>r.Id=="c").Rule.Contains("198.51.100.8/32"),"Provider IDs resolve to matching domain and IP conditions");
        Check(rows.All(r=>r.UpRate==-1 && r.DownRate==-1),"The initial sample does not invent a speed");
        rows=monitor.Read(Sample(2048,4096),now.AddSeconds(2),providers);
        Check(rows.Single(r=>r.Id=="a").UpRate==1024 && rows.Single(r=>r.Id=="a").DownRate==2048,"Per-flow rates use actual elapsed time and counter differences");
        Check(monitor.Read(Sample(0,0),now.AddSeconds(4)).All(r=>r.UpRate<=0 && r.DownRate<=0),"Counter reset cannot produce negative speed");
        monitor.Reset();Check(monitor.Read("{\"connections\":[]}",now).Count==0,"Reset drops old sessions");
        Check(monitor.Read("{\"connections\":null}",now).Count==0,"Mihomo's null connection slice is a valid empty sample");
        bool rejected=false;try{monitor.Read("{}",now);}catch(JsonException){rejected=true;}Check(rejected,"Malformed telemetry is rejected instead of displayed as valid empty data");
        await Bypass();Ui();Console.WriteLine("ALL_LIVE_ROUTING_TESTS_PASS");
    }
    static async Task Bypass()
    {
        var nodes=new[]{new ProxyNode{Name="TEST",Values=new(){["name"]="TEST",["type"]="socks5",["server"]="127.0.0.1",["port"]=18888}}};
        var settings=new AppSettings{Mode="mine",SelectedNode="TEST"};
        var rules=new List<RouteRule>{
            new(){Domain="198.51.100.8",Route="DIRECT",Manual=true,BypassTun=true},
            new(){Domain="process-name:game.exe",Route="VEGA",Manual=true},
            new(){Domain="203.0.113.8",Route="DIRECT",Manual=true},
            new(){Domain="192.0.2.8",Route="DIRECT",Manual=true,BypassTun=true,MyRuleEnabled=false},
            new(){Domain="not-an-ip.example",Route="DIRECT",Manual=true,BypassTun=true},
            new(){Domain="192.0.2.9",Route="VEGA",Manual=true,BypassTun=true},
            new(){Domain="192.0.2.10",Route="DIRECT",BypassTun=true,MyRuleEnabled=true}};
        Check(RoutingPolicy.TunExclusions(rules,"mine").SequenceEqual(new[]{"198.51.100.8/32"}),"Only explicitly enabled manual DIRECT IP bypasses reach the OS exclusion list");
        Check(RoutingPolicy.ExpectedRoute(rules,"mine","", "198.51.100.8","game.exe")=="DIRECT", "Explicit all-app IP bypass precedes a VPN application rule");
        Check(RoutingPolicy.ExpectedRoute(rules,"mine","", "203.0.113.8","game.exe")=="VEGA", "An ordinary DIRECT IP retains previous application precedence");
        UserStore.SaveRules(rules);Check(UserStore.LoadRules().Single(r=>r.Domain=="198.51.100.8").BypassTun,"IP bypass is persisted");
        var none=rules.Select(r=>r.Snapshot()).ToList();none[0].BypassTun=false;
        foreach(bool hot in new[]{false,true})
        {
            var plan=HotRuleProviders.Create(nodes,rules,settings);HotRuleProviders.Stage(plan);
            var config=VpnCore.BuildConfig(nodes,rules,settings,hotRules:hot,preparedHotRules:hot?plan:null);
            var tun=(Dictionary<string,object?>)config["tun"]!;
            Check(((string[])tun["route-exclude-address"]!).SequenceEqual(new[]{"198.51.100.8/32"}),"Bypass is compiled into "+(hot?"hot":"inline")+" TUN config");
            var old=VpnCore.BuildConfig(nodes,none,settings,hotRules:hot,preparedHotRules:hot?HotRuleProviders.Create(nodes,none,settings):null);
            Check(YamlWriter.Serialize(config)!=YamlWriter.Serialize(old),"Adding an OS exclusion is a structural change requiring reconnect");
            string path=Path.Combine(UserStore.Home,"bypass-"+hot+".yaml");File.WriteAllText(path,YamlWriter.Serialize(config));
            using var process=Process.Start(new ProcessStartInfo(VpnCore.CoreExecutable){ArgumentList={"-t","-f",path,"-d",UserStore.Home},UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true})!;
            var output=process.StandardOutput.ReadToEndAsync();var error=process.StandardError.ReadToEndAsync();
            using var limit=new CancellationTokenSource(TimeSpan.FromSeconds(20));try{await process.WaitForExitAsync(limit.Token);}catch{process.Kill(true);throw;}
            await Task.WhenAll(output,error);Check(process.ExitCode==0,"Actual Mihomo accepts explicit TUN exclusion: "+(process.ExitCode==0?"OK":output.Result+error.Result));
        }
    }
    static void Ui()
    {
        Exception? failure=null;var t=new Thread(()=>{
            MainWindow? w=null;SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            try
            {
                UserStore.SaveSettings(new AppSettings{Mode="mine"});UserStore.SaveRules(new[]{new RouteRule{Domain="198.51.100.8",Route="DIRECT",Manual=true},new RouteRule{Domain="selected.example",Route="VEGA",Manual=true}});
                w=new MainWindow{Left=-2300,Top=-2300,ShowActivated=false,ShowInTaskbar=false};
                T Get<T>(string name) where T:FrameworkElement=>(T)w.FindName(name);
                object? Call(string name,params object?[] args)=>typeof(MainWindow).GetMethod(name,Private)!.Invoke(w,args);
                void Pump(){var f=new DispatcherFrame();Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,new Action(()=>f.Continue=false));Dispatcher.PushFrame(f);w.UpdateLayout();}
                void Await(Task task){var end=DateTime.UtcNow.AddSeconds(4);while(!task.IsCompleted&&DateTime.UtcNow<end){Pump();Thread.Sleep(5);}task.GetAwaiter().GetResult();}
                w.Show();Pump();Call("HideOnboarding");
                ((DispatcherTimer)typeof(MainWindow).GetField("liveRoutesTimer",Private)!.GetValue(w)!).Stop();
                int calls=0;typeof(MainWindow).GetField("liveRoutesLoader",Private)!.SetValue(w,(Func<CancellationToken,Task<string>>)(_=>{calls++;return Task.FromResult(Sample());}));
                Get<TabControl>("MainTabs").SelectedIndex=2;Pump();Check(calls==0,"Monitoring does not poll while its page is hidden");
                var list=Get<ListBox>("RulesList");list.SelectedItem=list.Items.Cast<RouteRule>().Single(r=>r.Domain=="198.51.100.8");Call("EditRule_Click",null,new RoutedEventArgs());Pump();
                Check(Get<StackPanel>("BypassTunSection").IsVisible,"A DIRECT IP exposes the explicit game-server bypass option");
                Get<CheckBox>("BypassTunBox").IsChecked=true;Call("AddRule_Click",null,new RoutedEventArgs());Pump();
                Check(UserStore.LoadRules().Single(r=>r.Domain=="198.51.100.8").BypassTun,"Editor saves the bypass without inventing a VPN route");
                var target=list.Items.Cast<RouteRule>().Single(r=>r.Domain=="selected.example");
                var box=new CheckBox{DataContext=target,IsChecked=false};Call("RuleEnabled_Click",box,new RoutedEventArgs());Pump();
                Check(!UserStore.LoadRules().Single(r=>r.Domain=="selected.example").IncludedInMine,"Row selection can disable a saved rule without deleting it");
                Check(Get<TextBlock>("RoutingActiveStatus").Text.StartsWith("Сейчас: отключено"),"Saved settings are distinct from the applied connection state");
                Call("LiveRoutesLink_Click",null,new RoutedEventArgs());Pump();
                Check(Get<ListBox>("LiveRoutesList").Items.Count==7 && calls>0,"Opening monitoring displays actual flow snapshots");
                Get<TextBox>("LiveRoutesSearch").Text=".ru";Pump();Check(Get<ListBox>("LiveRoutesList").Items.Count==1,"Search isolates a destination suffix");
                Get<TextBox>("LiveRoutesSearch").Clear();Get<ComboBox>("LiveRoutesFilter").SelectedIndex=2;Pump();Check(Get<ListBox>("LiveRoutesList").Items.Cast<LiveConnection>().All(r=>r.Route=="DIRECT"),"DIRECT filter never mixes in VPN rows");
                Get<ComboBox>("LiveRoutesFilter").SelectedIndex=0;Pump();
                foreach(string theme in new[]{"light","dark"})foreach(double width in new[]{1040.0,840.0})
                {
                    Call("ApplyTheme",theme,false);w.Width=width;w.Height=620;Get<ScrollViewer>("HelpPageScroll").ScrollToTop();Pump();
                    var bmp=new RenderTargetBitmap((int)Math.Ceiling(w.ActualWidth),(int)Math.Ceiling(w.ActualHeight),96,96,PixelFormats.Pbgra32);bmp.Render(w);
                    var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bmp));using var file=File.Create(Path.Combine(UserStore.Home,$"monitor-{theme}-{width}.png"));png.Save(file);
                    Check(Get<ListBox>("LiveRoutesList").ActualWidth>300,"Monitor fits "+theme+" at "+width);
                }
                typeof(MainWindow).GetField("liveRoutesLoader",Private)!.SetValue(w,(Func<CancellationToken,Task<string>>)(_=>Task.FromException<string>(new IOException())));
                Await((Task)Call("RefreshLiveRoutesAsync")!);Pump();
                Check(Get<ListBox>("LiveRoutesList").Items.Count==0 && Get<TextBlock>("LiveRoutesStatus").Text.Contains("Нет свежих данных"),"A telemetry failure clears stale rows and reports the error");
            }
            catch(Exception e){failure=e;}finally{w?.Close();}
        });t.SetApartmentState(ApartmentState.STA);t.Start();if(!t.Join(TimeSpan.FromSeconds(40)))throw new TimeoutException("Live routing UI");if(failure!=null)throw new Exception("Live routing UI",failure);
    }
}
