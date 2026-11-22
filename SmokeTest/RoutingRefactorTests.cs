using System.IO;
using System.Net.Http;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ProstoKVN;

public static class RoutingRefactorTests
{
    static void Check(bool ok, string message)
    { if (!ok) throw new Exception("ROUTING_REFACTOR_FAILED: " + message); Console.WriteLine("PASS " + message); }
    static readonly BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    static ProxyNode Node(string name) => new() { Name = name, Values = new() {
        ["name"] = name, ["type"] = "socks5", ["server"] = "127.0.0.1", ["port"] = 19888, ["udp"] = true } };
    static int FreePort() { var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); int port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop(); return port; }
    public static async Task RunAsync()
    {
        Check(!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PROSTOKVN_TEST_HOME")), "All data is isolated");
        var routes = new List<RouteRule> {
            new() { Domain="example.org", Route="VEGA", Manual=true },
            new() { Domain="domain-exact:api.example.org", Route="DIRECT", Manual=true },
            new() { Domain="process-name:Discord.exe", Route="VEGA", Manual=true, MyRuleEnabled=false },
            new() { Domain="expired.test", Route="VEGA", ExpiresUtc=DateTime.UtcNow.AddMinutes(-1) },
            new() { Domain="approved.test", Route="VEGA", MyRuleEnabled=true },
            new() { Domain="cdn.example.ru", Route="VEGA" }
        };
        Check(RoutingPolicy.ExpectedRoute(routes,"mine","api.example.org") == "DIRECT"
            && RoutingPolicy.ExpectedRoute(routes,"mine","cdn.example.org") == "VEGA", "Exact exception precedes suffix in the common policy");
        Check(RoutingPolicy.ExpectedRoute(routes,"mine","voice.test",process:"Discord.exe") == "DIRECT"
            && RoutingPolicy.ExpectedRoute(routes,"mine","149.154.160.1",process:"Telegram.exe") == "DIRECT", "Excluded apps and built-in services stay direct in split mode");
        Check(VpnCore.IsStaleConnection("deleted.test",new[]{"TEST","VEGA"},routes,"mine"), "Deleting a VPN rule reconnects existing VPN flows to DIRECT");
        Check(VpnCore.IsStaleConnection("deleted.test",new[]{"DIRECT"},routes,"vpn"), "Deleting a DIRECT exception reconnects flows to VPN fallback");
        Check(VpnCore.IsStaleConnection("voice.test",new[]{"TEST","VEGA"},routes,"mine",processName:"Discord.exe"), "Disabling an app closes its old VPN flows");
        Check(!VpnCore.IsStaleConnection("cdn.example.ru",new[]{"DIRECT"},routes,"smart")
            && RoutingPolicy.ExpectedRoute(routes,"smart","expired.test") == "DIRECT", "Expired and Russian automatic routes are filtered consistently");
        Check(RoutingPolicy.ExpectedRoute(routes,"mine","approved.test") == "VEGA", "Explicitly approved automatic rule participates in split mode");
        Check(RouteTarget.TryParseUserInput("https://ПРИМЕР.РФ/path?q=1",out var url) && url.IsDomain
            && url.Value == "xn--e1afmkfd.xn--p1ai", "Pasted URLs normalize Unicode hosts");
        Check(!RouteTarget.TryParse("domain-exact:192.0.2.1",out _)
            && !RouteTarget.TryParseUserInput("https://user:secret@example.org",out _), "Exact IP and URLs with credentials are rejected");
        var nodes = new[] { Node("TEST"), Node("Europe, Amsterdam") };
        var pinned = new List<RouteRule> { new() {Domain="pinned.test",Route="VEGA",Manual=true,PreferredNode=nodes[1].Name} };
        var settings = new AppSettings {Mode="mine",SelectedNode="TEST"};
        var config = VpnCore.BuildConfig(nodes,pinned,settings);
        Check(((Dictionary<string,object?>)config["tun"]!)["enable"] is true && !settings.Tun, "Core enforces split TUN without mutating desired settings");
        var target = RoutingPolicy.Target(pinned[0],nodes,settings);
        Check(!target.Contains(',') && ((List<string>)config["rules"]!).Contains("DOMAIN-SUFFIX,pinned.test,"+target), "Preferred servers with commas use a safe rule alias");
        Check(((object[])config["proxy-groups"]!).OfType<Dictionary<string,object?>>().Any(g =>
            g["name"]?.ToString()==target && ((string[])g["proxies"]!)[0]==nodes[1].Name), "Safe alias resolves to the original server");
        foreach(string mode in new[]{"smart","vpn","direct","apps","mine"})
        {
            var options = new AppSettings {Mode=mode,SelectedNode="TEST"};
            var plan = HotRuleProviders.Create(nodes,routes,options);
            string files = string.Join("\n",plan.Files.Values);
            Check(!files.Contains("expired.test") && (mode=="smart" || !files.Contains("cdn.example.ru")), "Hot selection filters invalid automatic rules in "+mode);
            HotRuleProviders.Stage(plan);
            var cfg = VpnCore.BuildConfig(nodes,routes,options,hotRules:true,preparedHotRules:plan);
            await Validate(cfg,"policy-"+mode);
        }
        var pinnedPlan=HotRuleProviders.Create(nodes,pinned,settings);
        var removedPinned=HotRuleProviders.Create(nodes,new List<RouteRule>(),settings,previousPlan:pinnedPlan);
        Check(pinnedPlan.Rules.SequenceEqual(removedPinned.Rules) && removedPinned.Files.Values.All(f=>!f.Contains("pinned.test")),
            "Deleting a preferred-server rule reuses existing provider slots");
        await Validate(config,"comma-pinned");
        await CloseManyFlows();
        await LiveProviders();
        UserStore.SaveRules(routes);
        Ui();
        Console.WriteLine("ALL_ROUTING_REFACTOR_TESTS_PASS");
    }
    static async Task Validate(Dictionary<string,object?> config,string name)
    {
        string path=Path.Combine(UserStore.Home,name+".yaml"); File.WriteAllText(path,YamlWriter.Serialize(config));
        using var process=Process.Start(new ProcessStartInfo(VpnCore.CoreExecutable) {
            ArgumentList={"-t","-f",path,"-d",UserStore.Home},UseShellExecute=false,CreateNoWindow=true,
            RedirectStandardOutput=true,RedirectStandardError=true })!;
        var output=process.StandardOutput.ReadToEndAsync(); var error=process.StandardError.ReadToEndAsync();
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try { await process.WaitForExitAsync(timeout.Token); } catch {process.Kill(true);throw;}
        await Task.WhenAll(output,error);
        Check(process.ExitCode==0,"Actual Mihomo accepts "+name+": "+(process.ExitCode==0?"OK":output.Result+error.Result));
    }
    sealed class ConnectionsApi : HttpMessageHandler
    {
        public int Closed, Concurrent, Maximum;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
        {
            if (request.Method == HttpMethod.Get)
            {
                var connections=Enumerable.Range(0,82).Select(i=>new {
                    id=Guid.NewGuid().ToString(), rule=i==80?"InName":"RuleSet", rulePayload=i==80?"vega-probe-vpn":"",
                    metadata=new {host="removed.example.test",destinationIP="198.51.100.5",process="browser.exe",processPath=""},
                    chains=i==81?new[]{"DIRECT"}:new[]{"TEST","VEGA"}
                });
                return new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(JsonSerializer.Serialize(new{connections}))};
            }
            int current=Interlocked.Increment(ref Concurrent);
            int previous;
            do { previous=Maximum; if(previous>=current)break; } while(Interlocked.CompareExchange(ref Maximum,current,previous)!=previous);
            await Task.Delay(10,cancellation);
            Interlocked.Increment(ref Closed);Interlocked.Decrement(ref Concurrent);
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }
    }
    static async Task CloseManyFlows()
    {
        var handler=new ConnectionsApi();using var core=new VpnCore(apiHandler:handler);
        var task=(Task<int>)typeof(VpnCore).GetMethod("CloseStaleConnectionsAsync",Private)!.Invoke(core,
            new object[]{new[]{Node("TEST")},new List<RouteRule>(),new AppSettings{Mode="mine"}})!;
        Check(await task==80 && handler.Closed==80,"All affected flows are closed beyond the previous 60-connection limit; probes and correct flows stay open");
        Check(handler.Maximum is > 1 and <= 8,"Flow cleanup has bounded concurrency");
    }
    static async Task LiveProviders()
    {
        int apiPort=FreePort();
        using var core=new VpnCore(apiPort);
        var nodes=new[]{Node("TEST")}; var settings=new AppSettings{Mode="smart",SelectedNode="TEST"};
        var empty=new List<RouteRule>(); var plan=HotRuleProviders.Create(nodes,empty,settings); HotRuleProviders.Stage(plan);
        string secret=(string)typeof(VpnCore).GetField("secret",Private)!.GetValue(core)!;
        var config=VpnCore.BuildConfig(nodes,empty,settings,hotRules:true,preparedHotRules:plan);
        config["secret"]=secret; config["external-controller"]="127.0.0.1:"+apiPort;
        typeof(VpnCore).GetField("lastAppliedConfig",Private)!.SetValue(core,YamlWriter.Serialize(config));
        typeof(VpnCore).GetField("lastHotPlan",Private)!.SetValue(core,plan);
        // Use only ephemeral ports; never bind or modify the user's running VPN.
        config["mixed-port"]=0; config["listeners"]=Array.Empty<object>(); config["dns"]=new Dictionary<string,object?>{{"enable",false}};
        string path=Path.Combine(UserStore.Home,"isolated-live.yaml"); File.WriteAllText(path,YamlWriter.Serialize(config));
        var process=Process.Start(new ProcessStartInfo(VpnCore.CoreExecutable){ArgumentList={"-f",path,"-d",UserStore.Home},
            UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true})!;
        typeof(VpnCore).GetField("process",Private)!.SetValue(core,process);
        var stdout=process.StandardOutput.ReadToEndAsync();var stderr=process.StandardError.ReadToEndAsync();
        using var client=new HttpClient{BaseAddress=new Uri("http://127.0.0.1:"+apiPort),Timeout=TimeSpan.FromSeconds(2)};
        client.DefaultRequestHeaders.Authorization=new("Bearer",secret);
        bool ready=false;
        for(int i=0;i<60 && !process.HasExited;i++)
        {
            try{using var response=await client.GetAsync("/version");if(response.IsSuccessStatusCode){ready=true;break;}}catch(HttpRequestException){}
            await Task.Delay(100);
        }
        Check(ready,"Isolated live Mihomo API started");
        Check(new LiveConnectionMonitor().Read(await core.ReadConnectionsAsync(), DateTime.UtcNow).Count == 0,
            "Live monitor reads the authenticated isolated controller without including diagnostic traffic");
        var updated=new List<RouteRule>{new(){Domain="live.example.test",Route="REJECT",Manual=true}};
        Check(await core.ReloadRoutesAsync(nodes,updated,settings)==RouteReloadResult.Applied, "Real live provider update returns Applied");
        Check(await core.ReloadRoutesAsync(nodes,updated,settings)==RouteReloadResult.Unchanged, "Identical update returns Unchanged");
        Check(await core.ReloadRoutesAsync(nodes,empty,settings)==RouteReloadResult.Applied, "Deleting a live rule applies without rebuilding the tunnel");
        var topology=Enumerable.Range(0,40).Select(i=>new RouteRule{Domain=string.Concat(Enumerable.Repeat("a.",i))+"nested.test",Route=i%2==0?"VEGA":"DIRECT",Manual=true}).ToList();
        Check(await core.ReloadRoutesAsync(nodes,topology,settings)==RouteReloadResult.ReconnectRequired, "Capacity growth explicitly reports ReconnectRequired");
        Check(ReferenceEquals(typeof(VpnCore).GetField("process",Private)!.GetValue(core),process) && !process.HasExited, "Routine edits and structural changes preserve the running process");
        core.Stop(); await Task.WhenAll(stdout,stderr);
        Check(await core.ReloadRoutesAsync(nodes,updated,settings)==RouteReloadResult.Stopped, "Stopped client reports Stopped");
    }
    static void Ui()
    {
        Exception? error=null;
        var thread=new Thread(()=>{
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            MainWindow? w=null;
            try
            {
                w=new MainWindow{Left=-2400,Top=-2400,ShowInTaskbar=false,ShowActivated=false,WindowStartupLocation=WindowStartupLocation.Manual};
                T Get<T>(string name) where T:FrameworkElement => (T)w.FindName(name);
                void Call(string method,params object?[] args)=>typeof(MainWindow).GetMethod(method,Private)!.Invoke(w,args);
                void Click(string method)=>Call(method,null,new RoutedEventArgs());
                void Pump(){var f=new DispatcherFrame();Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,new Action(()=>f.Continue=false));Dispatcher.PushFrame(f);w.UpdateLayout();}
                void Save(string name){Pump();var bmp=new RenderTargetBitmap((int)w.ActualWidth,(int)w.ActualHeight,96,96,PixelFormats.Pbgra32);bmp.Render(w);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bmp));using var file=File.Create(Path.Combine(UserStore.Home,name+".png"));encoder.Save(file);}
                w.Show();Pump();Call("HideOnboarding");Pump();
                var sampleNodes = new List<ProxyNode> {Node("Германия · Основной"),Node("Нидерланды · Резерв")};
                typeof(MainWindow).GetField("nodes",Private)!.SetValue(w,sampleNodes);
                Get<ComboBox>("ServerBox").ItemsSource=sampleNodes;Get<ComboBox>("ServerBox").SelectedIndex=0;
                Call("RefreshNodeChoices");Call("UpdateConnectionUi");
                var tabs=Get<TabControl>("MainTabs");tabs.SelectedIndex=2;Pump();
                Get<ComboBox>("ModeBox").SelectedValue="mine";Pump();
                Check(UserStore.LoadSettings().Mode=="mine" && Get<CheckBox>("TunBox").IsChecked==true && !Get<CheckBox>("TunBox").IsEnabled,"Split mode persists and enforces TUN");
                Check(Get<ListBox>("RulesList").Items.Cast<RouteRule>().All(r=>r.Manual || r.MyRuleEnabled==true),"Automatic routes are separate from the user's list");
                Get<ComboBox>("RuleSourceBox").SelectedIndex=1;
                Check(Get<ListBox>("RulesList").Items.Cast<RouteRule>().Any() && Get<ListBox>("RulesList").Items.Cast<RouteRule>().All(r=>!r.Manual),"Automatic filter exposes the seeded service rules");
                Get<ComboBox>("RuleSourceBox").SelectedIndex=0;Click("NewRule_Click");Pump();
                Check(Get<Border>("RuleEditorOverlay").IsVisible && !Get<FrameworkElement>("OnboardingPanel").IsVisible && !tabs.IsEnabled,"Rule editor opens above the current page");
                Get<TextBox>("DomainBox").Text="bad value";Click("AddRule_Click");Pump();
                Check(Get<Expander>("AddRuleSection").IsExpanded && Get<TextBlock>("RuleValidationText").Text.Length>0,"Invalid input produces an inline error and preserves the editor");
                Get<TextBox>("DomainBox").Text="https://API.example.net/page";Get<ComboBox>("DomainScopeBox").SelectedIndex=1;Click("AddRule_Click");Pump();
                Check(UserStore.LoadRules().Any(r=>r.Domain=="domain-exact:api.example.net") && !Get<Border>("RuleEditorOverlay").IsVisible,"Saving a URL persists the exact host and closes the editor");
                var savedRow=Get<ListBox>("RulesList").Items.Cast<RouteRule>().Single(r=>r.Domain=="domain-exact:api.example.net");
                Get<ListBox>("RulesList").SelectedItem=savedRow;Click("EditRule_Click");Get<TextBox>("DomainBox").Text="discarded.example.test";
                Click("CloseRuleEditor_Click");Click("EditRule_Click");
                Check(Get<TextBox>("DomainBox").Text=="api.example.net","Reopening a cancelled edit restores the saved rule");
                Click("CloseRuleEditor_Click");
                foreach(string theme in new[]{"light","dark"}) foreach(var size in new[]{(1040,740),(840,620)})
                {
                    Call("ApplyTheme",theme,false);w.Width=size.Item1;w.Height=size.Item2;
                    foreach(int page in new[]{0,1,2,3,4})
                    {tabs.SelectedIndex=page;if(((TabItem)tabs.SelectedItem).Content is ScrollViewer scroll)scroll.ScrollToTop();Save($"{theme}-{size.Item1}-page-{page}");}
                    tabs.SelectedIndex=2;Click("NewRule_Click");Get<TextBox>("DomainBox").Text="example.com";Pump();
                    Get<Button>("SaveRuleButton").BringIntoView();Pump();
                    var bounds=Get<Button>("SaveRuleButton").TransformToAncestor(w).TransformBounds(new Rect(Get<Button>("SaveRuleButton").RenderSize));
                    Check(bounds.Top>=52 && bounds.Bottom<=w.ActualHeight-20,"Editor save remains reachable at "+theme+" "+size.Item1);
                    Save($"{theme}-{size.Item1}-editor");Click("CloseRuleEditor_Click");
                    Check(!Get<Border>("RuleEditorOverlay").IsVisible,"Cancel restores the page");
                }
                Check(!((VpnCore)typeof(MainWindow).GetField("core",Private)!.GetValue(w)!).IsRunning,"UI verification never connects a VPN");
            }
            catch(Exception ex){error=ex;}finally{w?.Close();}
        });thread.SetApartmentState(ApartmentState.STA);thread.Start();
        if(!thread.Join(TimeSpan.FromSeconds(60)))throw new TimeoutException("Routing UI test");if(error!=null)throw new Exception("Routing UI",error);
    }
}
