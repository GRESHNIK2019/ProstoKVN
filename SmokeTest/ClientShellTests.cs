using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ProstoKVN;
using Forms = System.Windows.Forms;

public static class ClientShellTests
{
    static readonly BindingFlags Private=BindingFlags.Instance|BindingFlags.NonPublic;
    static void Check(bool ok,string message){if(!ok)throw new Exception("CLIENT_SHELL_FAILED: "+message);Console.WriteLine("PASS "+message);}
    static IEnumerable<T> Children<T>(DependencyObject parent) where T:DependencyObject
    {
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++)
        {var child=VisualTreeHelper.GetChild(parent,i);if(child is T match)yield return match;foreach(var nested in Children<T>(child))yield return nested;}
    }
    public static void Run()
    {
        Check(!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PROSTOKVN_TEST_HOME")),"Isolated settings required");
        Exception? failure=null;var thread=new Thread(()=>{
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            try{Grouping();Tray();}catch(Exception e){failure=e;}
        });thread.SetApartmentState(ApartmentState.STA);thread.Start();
        if(!thread.Join(TimeSpan.FromSeconds(45)))throw new TimeoutException("Client shell tests");if(failure!=null)throw new Exception("Client shell",failure);
        Console.WriteLine("ALL_CLIENT_SHELL_TESTS_PASS");
    }
    static void Pump(MainWindow w)
    {var f=new DispatcherFrame();Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,new Action(()=>f.Continue=false));Dispatcher.PushFrame(f);w.UpdateLayout();}
    static void Invoke(MainWindow w,string method,params object?[] args)=>typeof(MainWindow).GetMethod(method,Private)!.Invoke(w,args);
    static T Get<T>(MainWindow w,string name) where T:FrameworkElement=>(T)w.FindName(name);
    static MainWindow Window()
    {
        var w=new MainWindow{WindowStartupLocation=WindowStartupLocation.Manual,Left=-2400,Top=-2400,ShowInTaskbar=false,ShowActivated=false};
        w.Show();Pump(w);Invoke(w,"HideOnboarding");return w;
    }
    static void Grouping()
    {
        UserStore.SaveSettings(new AppSettings{Mode="mine"});
        UserStore.SaveRules(new[]{"z.google.com","a.google.com","domain-exact:api.openai.com","chatgpt.com","discord.com","example.org","198.51.100.7","198.51.100.0/24","process-name:chrome.exe"}
            .Select(d=>new RouteRule{Domain=d,Route="VEGA",Manual=true}));
        var w=Window();
        try
        {
            Get<TabControl>(w,"MainTabs").SelectedIndex=2;Pump(w);var list=Get<ListBox>(w,"RulesList");
            ListCollectionView View()=>(ListCollectionView)list.ItemsSource;
            Expander Group(string name)=>Children<Expander>(list).Single(g=>g.DataContext is CollectionViewGroup data && data.Name?.ToString()==name);
            Check(View().Groups?.Count==6,"Sites are separated into service, IP, subnet and other-site groups");
            var groups=View().Groups!.Cast<CollectionViewGroup>().ToArray();
            Check(groups.Select(g=>g.Name.ToString()).SequenceEqual(groups.Select(g=>g.Name.ToString()).OrderBy(s=>s,StringComparer.CurrentCultureIgnoreCase)),"Group headers are sorted alphabetically");
            Check(groups.Single(g=>g.Name.ToString()=="Google").Items.Cast<RouteRule>().Select(r=>r.DisplayTarget).SequenceEqual(new[]{"a.google.com","z.google.com"}),"Sites inside a service are sorted by displayed host");
            Check(groups.Single(g=>g.Name.ToString()=="ChatGPT / OpenAI").ItemCount==2,"Exact domains keep the correct service and count");
            Check(Children<Expander>(list).Where(g=>g.DataContext is CollectionViewGroup).All(g=>g.IsExpanded),"Service groups initially show their rules");
            Group("Google").IsExpanded=false;Invoke(w,"RefreshRules");Pump(w);
            Check(!Group("Google").IsExpanded,"Refreshing the list remembers a collapsed group");
            Get<TextBox>(w,"RuleSearchBox").Text="google";Pump(w);
            Check(View().Groups!.Count==1 && Group("Google").IsExpanded && list.Items.Count==2,"Search shows and opens the matching group");
            Get<TextBox>(w,"RuleSearchBox").Clear();Pump(w);
            Check(!Group("Google").IsExpanded,"Clearing search restores the user's collapsed state");
            Get<TextBox>(w,"RuleSearchBox").Text="api.openai";Pump(w);
            Check(View().Groups!.Count==1 && list.Items.Count==1,"Searching an exact host retains its service header");
            Get<TextBox>(w,"RuleSearchBox").Clear();Invoke(w,"ApplicationsRules_Click",null,new RoutedEventArgs());Pump(w);
            Check(View().Groups==null && list.Items.Cast<RouteRule>().All(r=>r.Domain.StartsWith("process-")),"Application list remains separate from site groups");
            Invoke(w,"SitesRules_Click",null,new RoutedEventArgs());Pump(w);Group("Google").IsExpanded=true;
            foreach(string theme in new[]{"light","dark"})
            {
                Invoke(w,"ApplyTheme",theme,false);Get<ScrollViewer>(w,"RulesPageScroll").ScrollToTop();Get<Expander>(w,"RuleGroupsSection").BringIntoView();Pump(w);
                var surface=Get<Expander>(w,"RuleGroupsSection");var scroll=Get<ScrollViewer>(w,"RulesPageScroll");
                scroll.ScrollToVerticalOffset(scroll.VerticalOffset+surface.TransformToAncestor(scroll).Transform(new Point()).Y);Pump(w);
                var bmp=new RenderTargetBitmap((int)Math.Ceiling(w.ActualWidth),(int)Math.Ceiling(w.ActualHeight),96,96,PixelFormats.Pbgra32);bmp.Render(w);
                var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bmp));using var file=File.Create(Path.Combine(UserStore.Home,theme+"-grouped-sites.png"));png.Save(file);
            }
        }
        finally{w.Close();Pump(w);}
    }
    static void Tray()
    {
        var w=Window();var core=(VpnCore)typeof(MainWindow).GetField("core",Private)!.GetValue(w)!;
        var gate=(SemaphoreSlim)typeof(MainWindow).GetField("connectionGate",Private)!.GetValue(w)!;
        bool gateHeld=false,closed=false;Process? helper=null;
        w.Closed+=(_,_)=>closed=true;
        try
        {
            helper=Process.Start(new ProcessStartInfo("powershell.exe","-NoProfile -Command Start-Sleep -Seconds 60") {UseShellExecute=false,CreateNoWindow=true})!;
            typeof(VpnCore).GetField("process",Private)!.SetValue(core,helper);
            typeof(MainWindow).GetField("closeToTrayEnabled",Private)!.SetValue(w,true);
            Get<Button>(w,"CloseWindowButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Pump(w);
            var tray=(Forms.NotifyIcon)typeof(MainWindow).GetField("trayIcon",Private)!.GetValue(w)!;
            Check(!w.IsVisible && !w.ShowInTaskbar && tray.Visible,"X hides the window and keeps its notification icon");
            Check(core.IsRunning && !helper.HasExited && !(bool)typeof(MainWindow).GetField("isWindowClosing",Private)!.GetValue(w)!,"Hiding does not stop the running process or start shutdown");
            var menu=tray.ContextMenuStrip!;
            Check(menu.Items[0].Text=="Открыть ProstoKVN" && menu.Items[2].Text=="Выход","Tray has the right-click open and exit commands");
            ((Forms.ToolStripMenuItem)menu.Items[0]).PerformClick();Pump(w);
            Check(w.IsVisible && w.ShowInTaskbar,"Tray Open restores the same window");
            w.Close();Pump(w);
            Check(!w.IsVisible && core.IsRunning,"Alt+F4-style closing also hides without disconnecting");
            gate.Wait();gateHeld=true;((Forms.ToolStripMenuItem)menu.Items[2]).PerformClick();Pump(w);
            Check(!closed && core.IsRunning,"Explicit Exit waits for an in-flight connection transition");
            gate.Release();gateHeld=false;
            var end=DateTime.UtcNow.AddSeconds(8);while(!closed && DateTime.UtcNow<end){Pump(w);Thread.Sleep(5);}
            Check(closed && !core.IsRunning,"Tray Exit completes normal connection cleanup and closes the window");
            Check(typeof(MainWindow).GetField("trayIcon",Private)!.GetValue(w)==null && menu.IsDisposed,"Exiting disposes the tray icon and menu");
        }
        finally
        {
            if(gateHeld)gate.Release();if(!closed){Invoke(w,"RequestExit");Pump(w);}core.Dispose();helper?.Dispose();
        }
    }
}
