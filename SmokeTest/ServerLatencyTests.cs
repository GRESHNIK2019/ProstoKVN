using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ProstoKVN;

public static class ServerLatencyTests
{
    static void Check(bool condition,string message)
    {if(!condition)throw new Exception("LATENCY_FAILED: "+message);Console.WriteLine("PASS "+message);}
    static ProxyNode Node(string name="Германия · Основной",string type="vless",string host="fixture.example",int port=443)
        =>new(){Name=name,Values=new(){["type"]=type,["server"]=host,["port"]=port}};
    public static async Task RunAsync()
    {
        Check(!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PROSTOKVN_TEST_HOME")),"Isolated data required");
        var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();
        int port=((IPEndPoint)listener.LocalEndpoint).Port;
        try
        {
            var accept=listener.AcceptTcpClientAsync();
            var result=await new ServerLatencyProbe().MeasureAsync(Node(host:"127.0.0.1",port:port));
            using var connection=await accept.WaitAsync(TimeSpan.FromSeconds(3));
            Check(result.Method==ServerLatencyMethod.Tcp && result.Milliseconds>=1,"Real loopback TCP handshake returns RTT");
        }
        finally{listener.Stop();}
        var refused=await new ServerLatencyProbe().MeasureAsync(Node(host:"127.0.0.1",port:port));
        Check(refused.Milliseconds==null,"Refused TCP port is not reported as a successful ping");
        int calls=0,active=0,maximum=0,dns=0;
        var probe=new ServerLatencyProbe((_,_)=>{Interlocked.Increment(ref dns);return Task.FromResult(new[]{IPAddress.Loopback});},
            async(_,_,ct)=>{int now=Interlocked.Increment(ref active);int observed;do{observed=maximum;}while(now>observed && Interlocked.CompareExchange(ref maximum,now,observed)!=observed);
                try{Interlocked.Increment(ref calls);await Task.Delay(30,ct);return 42;}finally{Interlocked.Decrement(ref active);}},
            (_,_)=>Task.FromResult<int?>(83));
        var results=await Task.WhenAll(Enumerable.Range(0,100).Select(i=>probe.MeasureAsync(Node())));
        Check(results.All(r=>r.Milliseconds==42) && calls==100 && maximum<=6,"100 servers are checked with at most six concurrent probes");
        Check(dns<=6,"Shared host resolution is reused within a batch");
        var udp=await probe.MeasureAsync(Node(type:"hysteria2"));
        var tuic=await probe.MeasureAsync(Node(type:"tuic"));
        Check(udp.Method==ServerLatencyMethod.Icmp && udp.Milliseconds==83 && tuic.Method==ServerLatencyMethod.Icmp && calls==100,
            "UDP servers use ICMP rather than probing a misleading TCP port");
        var fake=new ServerLatencyProbe((_,_)=>Task.FromResult(new[]{IPAddress.Parse("198.18.0.5"),IPAddress.Parse("::ffff:198.19.1.1")}),
            (_,_,_)=>throw new Exception("Fake IP must not be probed"));
        Check((await fake.MeasureAsync(Node())).Milliseconds==null,"TUN fake IP addresses never produce false local RTT");
        using var cancel=new CancellationTokenSource();
        var slow=new ServerLatencyProbe((_,_)=>Task.FromResult(new[]{IPAddress.Loopback}),async(_,_,ct)=>{await Task.Delay(Timeout.Infinite,ct);return 1;});
        var pending=Enumerable.Range(0,20).Select(_=>slow.MeasureAsync(Node(),cancel.Token)).ToArray();cancel.Cancel();
        try{await Task.WhenAll(pending);throw new Exception("Cancellation ignored");}catch(OperationCanceledException){}
        Check(pending.All(t=>t.IsCanceled),"Cancellation stops both running probes and queued servers");
        var model=Node();model.BeginLatencyCheck();model.CompleteLatencyCheck(new(ServerLatencyMethod.Tcp,42,DateTime.UtcNow,"fixture"));
        string serialized=JsonSerializer.Serialize(model);
        Check(!serialized.Contains("Latency") && !serialized.Contains("ProtocolCaption"),"Runtime latency is excluded from subscription caches");
        if(Environment.GetEnvironmentVariable("PROSTOKVN_LATENCY_LIVE_TEST")=="1")
        {
            var live=await new ServerLatencyProbe().MeasureAsync(Node(host:"www.microsoft.com"));
            Check(live.Method==ServerLatencyMethod.Tcp && live.Milliseconds>=1,"Real HTTPS endpoint returns RTT on the current network");
            Console.WriteLine("LIVE_TCP_RTT_MS="+live.Milliseconds);
        }
        TestUi();
        Console.WriteLine("ALL_SERVER_LATENCY_TESTS_PASS");
    }
    static IEnumerable<T> Children<T>(DependencyObject parent) where T:DependencyObject
    {for(int i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++){var child=VisualTreeHelper.GetChild(parent,i);if(child is T t)yield return t;foreach(var nested in Children<T>(child))yield return nested;}}
    static void TestUi()
    {
        Exception? error=null;
        var thread=new Thread(()=>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            MainWindow? w=null;
            try
            {
                w=new MainWindow{WindowStartupLocation=WindowStartupLocation.Manual,Left=-2400,Top=-2400,ShowActivated=false,ShowInTaskbar=false};
                T Get<T>(string name) where T:FrameworkElement=>(T)w.FindName(name);
                void Invoke(string method,params object?[] args)=>typeof(MainWindow).GetMethod(method,BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(w,args);
                void Set(string field,object value)=>typeof(MainWindow).GetField(field,BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(w,value);
                void Pump(){var frame=new DispatcherFrame();Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,new Action(()=>frame.Continue=false));Dispatcher.PushFrame(frame);w.UpdateLayout();}
                void Until(Func<bool> predicate,string label){var end=DateTime.UtcNow.AddSeconds(8);while(!predicate() && DateTime.UtcNow<end){Pump();Thread.Sleep(5);}Check(predicate(),label);}
                void Click(string name)=>Get<Button>(name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                void Save(FrameworkElement surface,string name)
                {
                    Pump();var bitmap=new RenderTargetBitmap((int)Math.Ceiling(surface.ActualWidth),(int)Math.Ceiling(surface.ActualHeight),96,96,PixelFormats.Pbgra32);bitmap.Render(surface);
                    var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using var output=File.Create(Path.Combine(UserStore.Home,name+".png"));encoder.Save(output);
                }
                w.Show();Pump();Invoke("HideOnboarding");Get<TabControl>("MainTabs").SelectedIndex=0;
                var nodes=new List<ProxyNode>{Node(),Node("Нидерланды · Резервный","trojan"),Node("Швеция · Быстрый","hysteria2"),Node("США · Очень длинное название сервера для проверки размещения и обрезки текста","tuic")};
                nodes[3].Name+=string.Concat(Enumerable.Repeat(" · Дополнительное описание",12));
                Set("nodes",nodes);var box=Get<ComboBox>("ServerBox");box.ItemsSource=nodes;box.SelectedIndex=0;Invoke("UpdateConnectionUi");
                var originalSettings=File.ReadAllText(Path.Combine(UserStore.Home,"settings.json"));
                int networkCalls=0;
                w.LatencyProbeOverride=new ServerLatencyProbe((_,_)=>Task.FromResult(new[]{IPAddress.Loopback}),
                    async(_,_,ct)=>{Interlocked.Increment(ref networkCalls);await Task.Delay(75,ct);return 83;},async(_,ct)=>{await Task.Delay(50,ct);return null;});
                Pump();Check(networkCalls==0,"Opening the server list never starts probes automatically");
                Click("PingSelectedButton");Until(()=>nodes[0].Latency!=null,"Selected server gets a measurement");
                Check(Children<TextBlock>(box).Any(t=>t.Text=="83 мс"),"Closed dropdown shows the selected server RTT");
                Save(w,"01-home-latency-light");
                box.IsDropDownOpen=true;Pump();var popup=(Popup)box.Template.FindName("PART_Popup",box);
                var rows=Children<ComboBoxItem>(popup.Child).ToArray();
                Check(rows.Length==4,"Each server has a rendered dropdown row");
                Check(((FrameworkElement)popup.Child).ActualWidth<=box.ActualWidth+1,"Long server names keep the dropdown inside the field width");
                var rowButton=Children<Button>(rows[1]).Single();rowButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Until(()=>nodes[1].Latency!=null,"Row button measures an unselected server");
                Check(box.SelectedIndex==0 && box.IsDropDownOpen,"Row ping preserves selected server and keeps dropdown open");
                Click("PingAllButton");Check(Get<Button>("PingAllButton").Content.ToString()=="Остановить","Batch check exposes cancellation");
                Until(()=>nodes.All(n=>n.Latency!=null) && Get<Button>("PingAllButton").Content.ToString()=="Проверить все","Batch finishes and restores its action");
                Check(nodes[2].LatencyText=="Нет ответа" && nodes[2].LatencyDescription.Contains("ICMP"),"Missing ICMP response is labeled without claiming VPN failure");
                Save((FrameworkElement)popup.Child,"02-dropdown-latency-light");box.IsDropDownOpen=false;
                Save(w,"03-home-all-measured-light");
                Invoke("ApplyTheme",ThemeManager.Dark,false);w.Width=w.MinWidth;w.Height=w.MinHeight;Pump();
                Save(w,"04-home-latency-dark-minimum");box.IsDropDownOpen=true;Pump();Save((FrameworkElement)popup.Child,"05-dropdown-latency-dark");box.IsDropDownOpen=false;
                foreach(string name in new[]{"PingSelectedButton","PingAllButton","ServerLatencyHint","ServerBox","ModeBox"})
                {
                    ((FrameworkElement)w.FindName(name)).BringIntoView();Pump();
                    var element=Get<FrameworkElement>(name);var bounds=element.TransformToAncestor(w).TransformBounds(new Rect(element.RenderSize));
                    Check(bounds.Top>=0 && bounds.Bottom<w.ActualHeight && bounds.Right<w.ActualWidth,name+" fits the minimum window");
                }
                w.LatencyProbeOverride=new ServerLatencyProbe((_,_)=>Task.FromResult(new[]{IPAddress.Loopback}),
                    async(_,_,ct)=>{await Task.Delay(Timeout.Infinite,ct);return 1;},async(_,ct)=>{await Task.Delay(Timeout.Infinite,ct);return 1;});
                Click("PingAllButton");Click("PingAllButton");Until(()=>nodes.All(n=>!n.IsLatencyChecking) && Get<Button>("PingAllButton").Content.ToString()=="Проверить все","Cancel stops all row indicators");
                Check(nodes[0].Latency?.Milliseconds==83,"Cancellation retains previous measurements");
                Check(box.SelectedIndex==0 && File.ReadAllText(Path.Combine(UserStore.Home,"settings.json"))==originalSettings,"Probes preserve server selection and saved settings");
                Check(!((VpnCore)typeof(MainWindow).GetField("core",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(w)!).IsRunning,"Pinging does not start the VPN core");
                var delayed=new TaskCompletionSource<int?>(TaskCreationOptions.RunContinuationsAsynchronously);
                w.LatencyProbeOverride=new ServerLatencyProbe((_,_)=>Task.FromResult(new[]{IPAddress.Loopback}),(_,_,_)=>delayed.Task);
                Click("PingSelectedButton");Invoke("ResetServerLatencySession");delayed.SetResult(7);
                Until(()=>!nodes[0].IsLatencyChecking,"A subscription rebind releases old checking state");
                Check(nodes[0].Latency?.Milliseconds==83,"Stale response after rebind cannot overwrite previous RTT");
                Click("PingAllButton");w.Close();Until(()=>nodes.All(n=>!n.IsLatencyChecking),"Window close cancels pending probes");
                Console.WriteLine("LATENCY_PREVIEWS="+UserStore.Home);
            }
            catch(Exception e){error=e;}
            finally{w?.Close();}
        });thread.SetApartmentState(ApartmentState.STA);thread.Start();
        if(!thread.Join(TimeSpan.FromSeconds(55)))throw new TimeoutException("Latency UI timed out");
        if(error!=null)throw new Exception("Latency UI regression",error);
    }
}
