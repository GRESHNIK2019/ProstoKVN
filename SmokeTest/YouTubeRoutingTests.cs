using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ProstoKVN;

public static class YouTubeRoutingTests
{
    const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    static void Check(bool ok, string text)
    { if (!ok) throw new Exception("YOUTUBE_FAILED: " + text); Console.WriteLine("PASS " + text); }
    static ProxyNode Node(int port) => new() { Name="TEST", Values=new() {
        ["name"]="TEST", ["type"]="socks5", ["server"]="127.0.0.1", ["port"]=port } };
    static List<RouteRule> Legacy() => new[] { "youtube.com", "googlevideo.com", "ytimg.com", "ggpht.com", "www.youtube-nocookie.com" }
        .Select(d => new RouteRule { Domain=d, Route="VEGA" }).ToList();

    public static async Task RunAsync()
    {
        Check(!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PROSTOKVN_TEST_HOME")), "Isolated data directory is required");
        UserStore.EnsureHome();
        var legacy = Legacy();
        Check(RoutingPolicy.ExpectedRoute(legacy, "mine", "rr1---sn-test.googlevideo.com") == "DIRECT",
            "Reproduced: learned YouTube media rules are excluded from Only Selected");
        Check(YouTubeRouting.ActiveDomainCount(new[] {new RouteRule {Domain="domain-exact:googlevideo.com",Route="VEGA",Manual=true}},"mine")==0,
            "Exact media root does not falsely report coverage for video CDN subdomains");
        var selected = YouTubeRouting.Enable(legacy);
        Check(legacy.All(r => !r.Manual && r.MyRuleEnabled == null), "Planning does not mutate the current rules before save succeeds");
        Check(YouTubeRouting.ActiveDomainCount(selected, "mine") == YouTubeRouting.Domains.Count,
            "Explicit preset selects all YouTube endpoints");
        foreach (string host in new[] { "www.youtube.com", "rr1---sn-test.googlevideo.com", "i.ytimg.com", "yt3.ggpht.com", "youtubei.googleapis.com", "youtube.googleapis.com", "www.youtube-nocookie.com", "youtu.be" })
            Check(RoutingPolicy.ExpectedRoute(selected, "mine", host) == "VEGA", "Site, media and API use VPN: " + host);
        foreach (string host in new[] { "google.com", "maps.googleapis.com", "lh3.ggpht.com", "example.org", "googlevideo.com.example.org" })
            Check(RoutingPolicy.ExpectedRoute(selected, "mine", host) == "DIRECT", "Unselected hosts stay DIRECT: " + host);
        Check(YouTubeRouting.Domains.All(d => ServiceRuleGrouper.ServiceName(d) == YouTubeRouting.ServiceName), "All preset rules belong to the sorted YouTube service group");
        var repeated = YouTubeRouting.Enable(selected);
        Check(JsonSerializer.Serialize(repeated) == JsonSerializer.Serialize(selected), "Repeated enable creates no duplicates and preserves timestamps");
        selected.Add(new() { Domain="domain-exact:rr1---sn-test.googlevideo.com", Route="DIRECT", Manual=true });
        selected.Add(new() { Domain="youtube.googleapis.com", Route="REJECT", Manual=true });
        var protectedRules = YouTubeRouting.Enable(selected);
        Check(RoutingPolicy.ExpectedRoute(protectedRules,"mine","rr1---sn-test.googlevideo.com") == "DIRECT"
            && RoutingPolicy.ExpectedRoute(protectedRules,"mine","youtube.googleapis.com") == "REJECT", "Manual DIRECT and REJECT exceptions retain priority");
        var pinned = YouTubeRouting.Enable(new[] { new RouteRule { Domain="youtube.com", Route="VEGA", Manual=true, MyRuleEnabled=false, PreferredNode="Pinned" } });
        Check(pinned.All(r => r.PreferredNode == "Pinned" && r.IncludedInMine), "Explicit enable selects a disabled VPN rule and keeps site/media on the pinned server");
        UserStore.SaveRules(repeated);
        var restored=UserStore.LoadRules();
        Check(YouTubeRouting.ActiveDomainCount(restored,"mine") == YouTubeRouting.Domains.Count, "Selected media rules survive a restart");
        foreach (bool hot in new[] { false, true })
        {
            var nodes=new[] { Node(18888) }; var settings=new AppSettings { Mode="mine", SelectedNode="TEST" };
            var plan=HotRuleProviders.Create(nodes,restored,settings); HotRuleProviders.Stage(plan);
            var config=VpnCore.BuildConfig(nodes,restored,settings,hotRules:hot,preparedHotRules:hot ? plan : null);
            Check(((List<string>)config["rules"]!).Last()=="MATCH,DIRECT", "DIRECT fallback remains in " + (hot ? "hot" : "inline") + " config");
            await Validate(config,hot ? "hot" : "inline");
        }
        await LiveMedia();
        Ui();
        Console.WriteLine("ALL_YOUTUBE_ROUTING_TESTS_PASS");
    }

    static async Task Validate(Dictionary<string, object?> config,string name)
    {
        string path=Path.Combine(UserStore.Home,"youtube-"+name+".yaml");File.WriteAllText(path,YamlWriter.Serialize(config));
        using var process=Process.Start(new ProcessStartInfo(VpnCore.CoreExecutable) {
            ArgumentList={"-t","-f",path,"-d",UserStore.Home},UseShellExecute=false,CreateNoWindow=true,
            RedirectStandardOutput=true,RedirectStandardError=true })!;
        var output=process.StandardOutput.ReadToEndAsync();var error=process.StandardError.ReadToEndAsync();
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try { await process.WaitForExitAsync(timeout.Token); } catch { process.Kill(true); throw; }
        await Task.WhenAll(output,error);
        Check(process.ExitCode==0,"Actual Mihomo validates YouTube " + name + ": " + (process.ExitCode==0 ? "OK" : output.Result+error.Result));
    }

    static int FreePort() { var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();int port=((IPEndPoint)listener.LocalEndpoint).Port;listener.Stop();return port; }
    static async Task LiveMedia()
    {
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(60));var ct=timeout.Token;
        using var upstream=new TcpListener(IPAddress.Loopback,0);upstream.Start();
        int proxyPort=FreePort(),apiPort=FreePort();
        var nodes=new[] { Node(((IPEndPoint)upstream.LocalEndpoint).Port) };var settings=new AppSettings {Mode="mine",SelectedNode="TEST"};
        var legacy=Legacy();var initial=HotRuleProviders.Create(nodes,legacy,settings);HotRuleProviders.Stage(initial);
        using var core=new VpnCore(apiPort,new HttpClientHandler { UseProxy=false });
        string secret=(string)typeof(VpnCore).GetField("secret",Private)!.GetValue(core)!;
        var config=VpnCore.BuildConfig(nodes,legacy,settings,hotRules:true,preparedHotRules:initial);
        config["secret"]=secret;config["external-controller"]="127.0.0.1:"+apiPort;
        typeof(VpnCore).GetField("lastAppliedConfig",Private)!.SetValue(core,YamlWriter.Serialize(config));
        typeof(VpnCore).GetField("lastHotPlan",Private)!.SetValue(core,initial);
        // No TUN, system proxy, public endpoint or fixed listener is used.
        config["mixed-port"]=proxyPort;config["listeners"]=Array.Empty<object>();
        config["tun"]=new Dictionary<string,object?>{{"enable",false}};
        config["dns"]=new Dictionary<string,object?>{{"enable",false}};
        var path=Path.Combine(UserStore.Home,"youtube-live.yaml");File.WriteAllText(path,YamlWriter.Serialize(config));
        using var process=Process.Start(new ProcessStartInfo(VpnCore.CoreExecutable) {
            ArgumentList={"-f",path,"-d",UserStore.Home},UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true })!;
        typeof(VpnCore).GetField("process",Private)!.SetValue(core,process);
        var output=process.StandardOutput.ReadToEndAsync();var error=process.StandardError.ReadToEndAsync();
        try
        {
            using var api=new HttpClient(new HttpClientHandler {UseProxy=false}) {BaseAddress=new Uri("http://127.0.0.1:"+apiPort),Timeout=TimeSpan.FromSeconds(2)};
            api.DefaultRequestHeaders.Authorization=new("Bearer",secret);
            bool ready=false;
            for(int i=0;i<80 && !process.HasExited;i++)
            {try{using var response=await api.GetAsync("/version",ct);if(response.IsSuccessStatusCode){ready=true;break;}}catch(HttpRequestException){}catch(TaskCanceledException) when(!ct.IsCancellationRequested){} await Task.Delay(100,ct);}
            Check(ready,"Isolated live Mihomo started");
            var enabled=YouTubeRouting.Enable(legacy);
            Check(await core.ReloadRoutesAsync(nodes,enabled,settings)==RouteReloadResult.Applied,"Selecting YouTube updates actual live providers without reconnecting");
            using var handler=new HttpClientHandler {Proxy=new WebProxy("http://127.0.0.1:"+proxyPort),UseProxy=true};
            using var client=new HttpClient(handler);
            foreach(string host in new[] {"www.youtube.com","rr1---sn-test.googlevideo.com","youtubei.googleapis.com"})
            {
                Task<string> received=ServeSocks(upstream,ct);
                using var request=new HttpRequestMessage(HttpMethod.Get,"http://"+host+"/videoplayback");request.Headers.ConnectionClose=true;
                using var response=await client.SendAsync(request,ct);
                Check(await response.Content.ReadAsStringAsync(ct)=="media-ok" && await received==host,"Actual request traverses selected SOCKS outbound: "+host);
            }
            Check(!process.HasExited && await core.ReloadRoutesAsync(nodes,enabled,settings)==RouteReloadResult.Unchanged,"Repeated enable keeps the same running tunnel and provider topology");
        }
        finally { core.Stop();await Task.WhenAll(output,error);File.WriteAllText(Path.Combine(UserStore.Home,"youtube-live.log"),output.Result+error.Result); }
    }
    static async Task<string> ServeSocks(TcpListener listener,CancellationToken ct)
    {
        using var client=await listener.AcceptTcpClientAsync(ct);using var stream=client.GetStream();
        byte[] header=new byte[2];await stream.ReadExactlyAsync(header,ct);
        byte[] methods=new byte[header[1]];await stream.ReadExactlyAsync(methods,ct);await stream.WriteAsync(new byte[]{5,0},ct);
        byte[] command=new byte[4];await stream.ReadExactlyAsync(command,ct);
        Check(command[0]==5 && command[1]==1 && command[3]==3,"SOCKS outbound preserves the selected domain");
        byte[] length=new byte[1];await stream.ReadExactlyAsync(length,ct);byte[] host=new byte[length[0]];await stream.ReadExactlyAsync(host,ct);
        byte[] port=new byte[2];await stream.ReadExactlyAsync(port,ct);
        await stream.WriteAsync(new byte[]{5,0,0,1,127,0,0,1,0,0},ct);
        var request=new StringBuilder();byte[] one=new byte[1];
        while(request.Length<16384 && !request.ToString().EndsWith("\r\n\r\n",StringComparison.Ordinal))
        {await stream.ReadExactlyAsync(one,ct);request.Append((char)one[0]);}
        await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 8\r\nConnection: close\r\n\r\nmedia-ok"),ct);
        return Encoding.ASCII.GetString(host);
    }

    static void Ui()
    {
        Exception? failure=null;var thread=new Thread(()=>{
            MainWindow? w=null;
            try
            {
                UserStore.SaveSettings(new AppSettings{Mode="mine"});UserStore.SaveRules(Legacy());
                w=new MainWindow{Left=-2500,Top=-2500,ShowInTaskbar=false,ShowActivated=false};
                T Get<T>(string name) where T:FrameworkElement=>(T)w.FindName(name);
                void Call(string name)=>typeof(MainWindow).GetMethod(name,Private)!.Invoke(w,new object?[]{null,new RoutedEventArgs()});
                void Pump(){var frame=new DispatcherFrame();Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,new Action(()=>frame.Continue=false));Dispatcher.PushFrame(frame);w.UpdateLayout();}
                w.Show();Pump();typeof(MainWindow).GetMethod("HideOnboarding",Private)!.Invoke(w,null);
                Get<TabControl>("MainTabs").SelectedIndex=2;Pump();
                Check(Get<Border>("YouTubePresetCard").IsVisible && Get<TextBlock>("YouTubePresetStatus").Text.Contains("0 из 8"),"Only Selected explains the missing service endpoints");
                Get<Button>("EnableYouTubeButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Pump();
                Check(YouTubeRouting.ActiveDomainCount(UserStore.LoadRules(),"mine")==8 && Get<TextBlock>("YouTubePresetStatus").Text.Contains("8 из 8"),"One click persists all selected YouTube rules");
                Check(!Get<Button>("EnableYouTubeButton").IsEnabled && Get<Button>("EnableYouTubeButton").Content.ToString()=="YouTube включён","Completed service setup is clearly marked");
                Check(Get<ListBox>("RulesList").Items.Cast<RouteRule>().Any(r=>r.Domain=="googlevideo.com") && Get<ComboBox>("RuleSourceBox").SelectedIndex==0,"Selected media rules are visible in My Rules");
                Call("ApplicationsRules_Click");Pump();Check(!Get<Border>("YouTubePresetCard").IsVisible,"Service control is confined to sites");
                Call("SitesRules_Click");Pump();
                foreach(string theme in new[]{"light","dark"})
                {
                    typeof(MainWindow).GetMethod("ApplyTheme",Private)!.Invoke(w,new object[]{theme,false});
                    w.Width=w.MinWidth;w.Height=w.MinHeight;Get<Button>("EnableYouTubeButton").BringIntoView();Pump();
                    var button=Get<Button>("EnableYouTubeButton");var scroll=Get<ScrollViewer>("RulesPageScroll");
                    var bounds=button.TransformToAncestor(scroll).TransformBounds(new Rect(button.RenderSize));
                    Check(bounds.Left>=0 && bounds.Right<=scroll.ActualWidth && bounds.Top>=0 && bounds.Bottom<=scroll.ActualHeight,"YouTube action is reachable at minimum size: "+theme);
                    var bmp=new RenderTargetBitmap((int)Math.Ceiling(w.ActualWidth),(int)Math.Ceiling(w.ActualHeight),96,96,PixelFormats.Pbgra32);bmp.Render(w);
                    var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bmp));using var file=File.Create(Path.Combine(UserStore.Home,"youtube-"+theme+".png"));png.Save(file);
                }
                Get<ComboBox>("ModeBox").SelectedValue="smart";Pump();Check(!Get<Border>("YouTubePresetCard").IsVisible,"Service prompt follows the selected mode");
            }
            catch(Exception e){failure=e;}
            finally{w?.Close();}
        });thread.SetApartmentState(ApartmentState.STA);thread.Start();
        if(!thread.Join(TimeSpan.FromSeconds(35)))throw new TimeoutException("YouTube UI tests");if(failure!=null)throw new Exception("YouTube UI",failure);
    }
}
