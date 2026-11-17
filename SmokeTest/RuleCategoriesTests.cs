using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ProstoKVN;

public static class RuleCategoriesTests
{
    static void Assert(bool condition, string name)
    {
        if (!condition) throw new Exception("RULE_CATEGORIES_FAILED: " + name);
        Console.WriteLine("PASS " + name);
    }
    public static void Run()
    {
        Assert(!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PROSTOKVN_TEST_HOME")),
            "Isolated test settings required");
        var original = new List<RouteRule> {
            new() { Domain="example.com", Route="VEGA", Manual=true, Label="Example site" },
            new() { Domain="203.0.113.0/24", Route="DIRECT", Manual=true },
            new() { Domain="process-name:Discord.exe", Route="VEGA", Manual=true, Label="Discord desktop" },
            new() { Domain=@"process-path:C:\Apps\editor.exe", Route="DIRECT", Manual=true }
        };
        UserStore.SaveRules(original);
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            MainWindow? window = null;
            try
            {
                window = new MainWindow { Left=-2200, Top=-2200, ShowInTaskbar=false, ShowActivated=false };
                T Get<T>(string key) where T: FrameworkElement => (T)window.FindName(key);
                void Invoke(string name) => typeof(MainWindow).GetMethod(name,BindingFlags.Instance|BindingFlags.NonPublic)!
                    .Invoke(window,new object?[]{null,new RoutedEventArgs()});
                void Pump()
                {
                    var frame=new DispatcherFrame();
                    Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,new Action(()=>frame.Continue=false));
                    Dispatcher.PushFrame(frame);
                    window.UpdateLayout();
                }
                window.Show();
                typeof(MainWindow).GetMethod("HideOnboarding",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(window,null);
                Get<TabControl>("MainTabs").SelectedIndex=2;
                Pump();
                var list=Get<ListBox>("RulesList");
                Assert(list.Items.Cast<RouteRule>().Any(r=>r.Domain=="example.com") &&
                    list.Items.Cast<RouteRule>().Any(r=>r.Domain=="203.0.113.0/24") &&
                    list.Items.Cast<RouteRule>().All(r=>!r.Domain.StartsWith("process-")),
                    "Sites page displays domains and IP/subnets, not programs");
                Assert(Get<Button>("NewRuleButton").Content?.ToString()=="Добавить сайт или IP",
                    "Site editor has its own add action");
                Get<TextBox>("RuleSearchBox").Text="example.com";
                Assert(list.Items.Count==1,"Site search applies only to sites");
                Invoke("ApplicationsRules_Click");
                Pump();
                Assert(list.Items.Cast<RouteRule>().Count()==2 &&
                    list.Items.Cast<RouteRule>().All(r=>r.Domain.StartsWith("process-")),
                    "Applications page displays both process name and executable path rules");
                Assert(Get<TextBox>("RuleSearchBox").Text=="", "App search starts with its own query");
                Assert(Get<Button>("NewRuleButton").Content?.ToString()=="Добавить приложение" &&
                    Get<ComboBox>("RuleTargetKindBox").SelectedIndex==1,
                    "Application editor automatically selects executable target");
                var existing=list.Items.Cast<RouteRule>().Single(r=>r.Domain=="process-name:Discord.exe");
                list.SelectedItem=existing;
                Assert(Get<TextBox>("DomainBox").Text=="Discord.exe" &&
                    Get<ComboBox>("RuleTargetKindBox").SelectedIndex==1,
                    "Selecting existing app loads correct editor");
                Get<TextBox>("RuleSearchBox").Text="editor.exe";
                Assert(list.Items.Count==1,"Application search filters by executable name");
                Invoke("SitesRules_Click");
                Pump();
                Assert(Get<TextBox>("RuleSearchBox").Text=="example.com" && list.Items.Count==1,
                    "Site query is restored independently");
                Invoke("ApplicationsRules_Click");
                Pump();
                Assert(Get<TextBox>("RuleSearchBox").Text=="editor.exe" && list.Items.Count==1,
                    "Application query is restored independently");
                Get<TextBox>("RuleSearchBox").Clear();
                Invoke("NewRule_Click");
                Assert(Get<ComboBox>("RuleTargetKindBox").SelectedIndex==1 &&
                    Get<Button>("ChooseApplicationButton").Visibility==Visibility.Visible,
                    "New app rule keeps application target and .exe picker");
                var after=UserStore.LoadRules();
                Assert(original.All(x=>after.Any(y=>x.Domain==y.Domain && x.Route==y.Route)),
                    "Switching pages does not change saved routing rules");
                window.Close();
                Console.WriteLine("ALL_RULE_CATEGORIES_TESTS_PASS");
            }
            catch(Exception e) { failure=e; try{window?.Close();}catch{} }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if(!thread.Join(TimeSpan.FromSeconds(35)))throw new TimeoutException("Rule category UI test timeout");
        if(failure!=null)throw new Exception("Rule categories GUI regression",failure);
    }
}
