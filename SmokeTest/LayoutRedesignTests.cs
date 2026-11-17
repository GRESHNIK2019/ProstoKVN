using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ProstoKVN;

public static class LayoutRedesignTests
{
    static void Check(bool value,string message){if(!value)throw new Exception("LAYOUT_FAILED: "+message);Console.WriteLine("PASS "+message);}
    public static void Run()
    {
        Check(!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PROSTOKVN_TEST_HOME")),"Isolated settings required");
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
                void Save(FrameworkElement surface,string name,double scale=1)
                {
                    Pump();var bitmap=new RenderTargetBitmap((int)Math.Ceiling(surface.ActualWidth*scale),(int)Math.Ceiling(surface.ActualHeight*scale),96*scale,96*scale,PixelFormats.Pbgra32);bitmap.Render(surface);
                    var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using var file=File.Create(Path.Combine(UserStore.Home,name+".png"));encoder.Save(file);
                }
                void NoOverlap(params string[] names)
                {
                    Pump();var boxes=names.Select(n=>(Name:n,Element:Get<FrameworkElement>(n))).Where(v=>v.Element.IsVisible)
                        .Select(v=>(v.Name,Bounds:v.Element.TransformToAncestor(w).TransformBounds(new Rect(v.Element.RenderSize)))).ToArray();
                    foreach(var box in boxes){Check(box.Bounds.Width>40 && box.Bounds.Left>=184 && box.Bounds.Right<=w.ActualWidth-10,box.Name+" has usable width and stays inside the content area");}
                    for(int i=0;i<boxes.Length;i++)for(int j=i+1;j<boxes.Length;j++)
                    {var intersect=Rect.Intersect(boxes[i].Bounds,boxes[j].Bounds);Check(intersect.IsEmpty||intersect.Width<1||intersect.Height<1,boxes[i].Name+" does not overlap "+boxes[j].Name);}
                }
                w.Show();Pump();Save(w,"00-welcome");Invoke("HideOnboarding");
                var nodes=new List<ProxyNode>{new(){Name="Германия · Основной",Values=new(){["type"]="vless",["server"]="one.example",["port"]=443}},new(){Name="Нидерланды · Резервный",Values=new(){["type"]="trojan",["server"]="two.example",["port"]=443}}};
                nodes[0].CompleteLatencyCheck(new(ServerLatencyMethod.Tcp,83,DateTime.UtcNow,"Тестовое измерение"));
                Set("nodes",nodes);Get<ComboBox>("ServerBox").ItemsSource=nodes;Get<ComboBox>("ServerBox").SelectedIndex=0;Invoke("RefreshNodeChoices");Invoke("UpdateConnectionUi");
                Set("rules",new List<RouteRule>{new(){Domain="example.com",Label="Рабочий сервис",Route="VEGA",Manual=true},new(){Domain="process-name:chrome.exe",Label="Браузер",Route="DIRECT",Manual=true}});Invoke("RefreshRules");
                var profiles=new List<SubscriptionProfile>{new(){Name="Основная подписка",NodeCount=41,DetectedFormat="Xray JSON"},new(){Name="Резервная подписка",NodeCount=12,DetectedFormat="Mihomo"}};
                Set("profiles",profiles);Invoke("RefreshSubscriptionList",null,false);
                var tabs=Get<TabControl>("MainTabs");
                foreach(string theme in new[]{ThemeManager.Light,ThemeManager.Dark})
                foreach(bool minimum in new[]{false,true})
                {
                    Invoke("ApplyTheme",theme,false);w.Width=minimum?w.MinWidth:1040;w.Height=minimum?w.MinHeight:740;
                    string prefix=theme+(minimum?"-minimum":"-default");
                    tabs.SelectedIndex=0;Get<ScrollViewer>("HomePageScroll").ScrollToTop();Save(w,prefix+"-home");
                    NoOverlap("ConnectButton","ConnectionTitle","ServerBox","ModeBox","CoverageHint","CoverageSettingsButton","PingSelectedButton","PingAllButton");
                    tabs.SelectedIndex=1;Invoke("NewSubscription_Click",null,new RoutedEventArgs());Get<TextBox>("SubscriptionNameBox").Text="Моя подписка";Get<TextBox>("SubscriptionBox").Text="https://provider.example/feed/demo";Get<ScrollViewer>("SubscriptionsPageScroll").ScrollToTop();Pump();Save(w,prefix+"-subscriptions");
                    NoOverlap("SubscriptionNameBox","SubscriptionBox","SubscriptionFormatBox","ImportButton","ProfileStatusText");
                    Get<Button>("ImportButton").BringIntoView();Pump();var import=Get<Button>("ImportButton").TransformToAncestor(w).TransformBounds(new Rect(Get<Button>("ImportButton").RenderSize));
                    Check(import.Top>=52 && import.Bottom<w.ActualHeight,"Subscription save is reachable by scrolling");
                    Invoke("CloseSubscriptionEditor_Click",null,new RoutedEventArgs());Pump();Check(!Get<Expander>("SubscriptionEditor").IsVisible,"Cancel hides subscription editor");
                    tabs.SelectedIndex=2;Invoke("NewRule_Click",null,new RoutedEventArgs());Get<ComboBox>("RuleTargetKindBox").SelectedIndex=1;Get<TextBox>("DomainBox").Text=@"C:\Program Files\Example App\application.exe";Get<TextBox>("RuleLabelBox").Text="Рабочее приложение";Get<ScrollViewer>("RulesPageScroll").ScrollToTop();Pump();Save(w,prefix+"-rules");
                    NoOverlap("RuleTargetKindBox","DomainBox","ChooseApplicationButton","RuleRouteBox","RuleNodeBox","RuleLabelBox","SaveRuleButton");
                    Get<Button>("SaveRuleButton").BringIntoView();Save(w,prefix+"-rule-save");var save=Get<Button>("SaveRuleButton").TransformToAncestor(w).TransformBounds(new Rect(Get<Button>("SaveRuleButton").RenderSize));Check(save.Top>=52 && save.Bottom<w.ActualHeight,"Rule save is reachable by scrolling");
                    Invoke("CloseRuleEditor_Click",null,new RoutedEventArgs());Pump();Check(!Get<Expander>("AddRuleSection").IsVisible,"Cancel hides rule editor");
                    tabs.SelectedIndex=3;Get<Expander>("RoutingSection").IsExpanded=true;Get<Expander>("UpdateSection").IsExpanded=true;Get<ScrollViewer>("SettingsPageScroll").ScrollToTop();Save(w,prefix+"-settings");NoOverlap("TunBox","AutoLearnBox","SettingsApplyHint","CheckUpdateButton");
                    tabs.SelectedIndex=4;Get<Expander>("ConnectionDetailsSection").IsExpanded=true;Get<Expander>("ProbeSection").IsExpanded=true;Get<Expander>("LogSection").IsExpanded=true;Get<TextBox>("ProbeBox").Text="example.com";Get<ScrollViewer>("HelpPageScroll").ScrollToTop();Save(w,prefix+"-help");NoOverlap("ProbeBox","ProbeResult","LogBox");
                }
                tabs.SelectedIndex=0;w.Width=1040;w.Height=740;Save(w,"dark-high-dpi-home",1.5);
                var server=Get<ComboBox>("ServerBox");server.IsDropDownOpen=true;Pump();var popup=(Popup)server.Template.FindName("PART_Popup",server);Save((FrameworkElement)popup.Child,"dark-server-menu");Check(((FrameworkElement)popup.Child).ActualWidth<=server.ActualWidth+1,"Server menu remains aligned with its field");server.IsDropDownOpen=false;
                foreach(string theme in new[]{ThemeManager.Light,ThemeManager.Dark})
                {
                    Invoke("ApplyTheme",theme,false);
                    var dialog=new ClientDialog(w,string.Join("\n",Enumerable.Repeat("Очень длинное сообщение об ошибке. "+new string('А',120),30)),"Ошибка подключения",MessageBoxButton.YesNo,MessageBoxImage.Error,MessageBoxResult.No)
                    {WindowStartupLocation=WindowStartupLocation.Manual,Left=-2400,Top=-2400,ShowActivated=false};
                    dialog.Show();Pump();Save(dialog,theme+"-long-error-dialog");
                    var actions=((Grid)dialog.Content).Children.OfType<WrapPanel>().Single();
                    var bounds=actions.TransformToAncestor(dialog).TransformBounds(new Rect(actions.RenderSize));
                    Check(bounds.Bottom<=dialog.ActualHeight && bounds.Top>0,"Long dialog keeps confirmation buttons visible");
                    Check(dialog.Result==MessageBoxResult.No && actions.Children.OfType<Button>().Single(b=>b.IsDefault).Content.ToString()=="Нет","Delete-style dialog defaults to No");
                    actions.Children.OfType<Button>().Single(b=>b.Content.ToString()=="Да").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Check(dialog.Result==MessageBoxResult.Yes && !dialog.IsVisible,"Confirmation button returns the selected result");
                }
                Invoke("ShowOnboarding");w.Width=w.MinWidth;w.Height=w.MinHeight;Save(w,"dark-minimum-welcome");
                Check(!((VpnCore)typeof(MainWindow).GetField("core",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(w)!).IsRunning,"Layout verification never starts VPN");
                Console.WriteLine("LAYOUT_PREVIEWS="+UserStore.Home);Console.WriteLine("ALL_LAYOUT_REDESIGN_TESTS_PASS");
            }
            catch(Exception e){error=e;}
            finally{w?.Close();}
        });thread.SetApartmentState(ApartmentState.STA);thread.Start();if(!thread.Join(TimeSpan.FromSeconds(90)))throw new TimeoutException("Layout test timed out");if(error!=null)throw new Exception("Layout regression",error);
    }
    public static void RunBaseline()
    {
        Check(!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PROSTOKVN_TEST_HOME")),"Baseline capture requires isolated settings");
        var thread=new Thread(()=>
        {
            var w=new MainWindow{WindowStartupLocation=WindowStartupLocation.Manual,Left=-2400,Top=-2400,Width=1040,Height=740,ShowActivated=false,ShowInTaskbar=false};
            void Invoke(string method,params object?[] args)=>typeof(MainWindow).GetMethod(method,BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(w,args);
            void Pump(){var frame=new DispatcherFrame();Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,new Action(()=>frame.Continue=false));Dispatcher.PushFrame(frame);w.UpdateLayout();}
            w.Show();Pump();Invoke("HideOnboarding");
            ((ComboBox)w.FindName("ServerBox")).ItemsSource=new[]{new ProxyNode{Name="Германия · Основной",Values=new(){["type"]="vless"}}};((ComboBox)w.FindName("ServerBox")).SelectedIndex=0;
            var tabs=(TabControl)w.FindName("MainTabs");
            foreach(var theme in new[]{ThemeManager.Light,ThemeManager.Dark})
            foreach(var size in new[]{(1040,740),(840,600)})
            foreach(var page in new[]{0,1,2,3,4})
            {
                Invoke("ApplyTheme",theme,false);w.Width=size.Item1;w.Height=size.Item2;tabs.SelectedIndex=page;
                if(page==1){Invoke("NewSubscription_Click",null,new RoutedEventArgs());((TextBox)w.FindName("SubscriptionNameBox")).Text="Моя подписка";((TextBox)w.FindName("SubscriptionBox")).Text="https://provider.example/feed/demo";}
                if(page==2){Invoke("NewRule_Click",null,new RoutedEventArgs());((ComboBox)w.FindName("RuleTargetKindBox")).SelectedIndex=1;((TextBox)w.FindName("DomainBox")).Text=@"C:\Program Files\Example App\application.exe";}
                Pump();if(((TabItem)tabs.SelectedItem).Content is ScrollViewer scroll)scroll.ScrollToTop();Pump();
                var bitmap=new RenderTargetBitmap((int)w.ActualWidth,(int)w.ActualHeight,96,96,PixelFormats.Pbgra32);bitmap.Render(w);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using var file=File.Create(Path.Combine(UserStore.Home,$"{theme}-{size.Item1}x{size.Item2}-page-{page}.png"));encoder.Save(file);
            }
            w.Close();Console.WriteLine("BASELINE_PREVIEWS="+UserStore.Home);
        });thread.SetApartmentState(ApartmentState.STA);thread.Start();if(!thread.Join(TimeSpan.FromSeconds(60)))throw new TimeoutException("Baseline capture timed out");
    }
}
