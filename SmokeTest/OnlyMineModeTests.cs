using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ProstoKVN;

public static class OnlyMineModeTests
{
    static void Check(bool success, string label)
    {
        if (!success) throw new Exception("ONLY_MINE_FAILED: "+label);
        Console.WriteLine("PASS "+label);
    }

    public static void Run()
    {
        Check(!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PROSTOKVN_TEST_HOME")),
            "Isolated test directory required");
        var node=new ProxyNode{ Name="TEST", Values=new(){
            ["name"]="TEST",["type"]="socks5",["server"]="127.0.0.1",["port"]=18888
        }};
        var settings=new AppSettings { Mode="mine", Tun=true, AutoLearn=true, SelectedNode="TEST" };
        var routes=new List<RouteRule>{
            new() {Domain="example.org",Route="VEGA",Manual=true}, // Legacy rule: preserved
            new() {Domain="domain-exact:api.example.org",Route="DIRECT",Manual=true},
            new() {Domain="82.47.130.46",Route="DIRECT",Manual=true}, // Legacy user's IP exception
            new() {Domain="process-name:chrome.exe",Route="VEGA",Manual=true,MyRuleEnabled=true},
            new() {Domain="process-name:Discord.exe",Route="VEGA",Manual=true,MyRuleEnabled=false},
            new() {Domain="example.net",Route="VEGA",Manual=false},
            new() {Domain="openai.com",Route="VEGA",Manual=false,Label="ChatGPT / OpenAI"},
            new() {Domain="claude.com",Route="DIRECT",Manual=false,Label="Claude / Anthropic"},
            new() {Domain="process-name:Telegram.exe",Route="VEGA",Manual=false}
        };
        Check(routes[0].IncludedInMine && routes[2].IncludedInMine,
            "Old manually added sites and IPs stay selected");
        Check(!routes[4].IncludedInMine && !routes[5].IncludedInMine,
            "Explicitly disabled and auto-created rules stay excluded");
        UserStore.SaveRules(routes);
        var restored=UserStore.LoadRules();
        Check(restored.Count==routes.Count
            && restored.Single(r=>r.Domain=="example.org").IncludedInMine
            && restored.Single(r=>r.Domain=="process-name:Discord.exe").MyRuleEnabled==false,
            "Old and new ownership flags survive JSON roundtrip");
        var config=VpnCore.BuildConfig(new[]{node},restored,settings);
        var rules=(List<string>)config["rules"]!;
        Check(rules.Last()=="MATCH,DIRECT",
            "Any site or application not selected by the user routes DIRECT");
        Check(rules.Contains("DOMAIN-SUFFIX,example.org,VEGA")
            && rules.Contains("DOMAIN,api.example.org,DIRECT")
            && rules.Contains("IP-CIDR,82.47.130.46/32,DIRECT,no-resolve")
            && rules.Contains("PROCESS-NAME,chrome.exe,VEGA"),
            "Legacy user sites, exact rules, IP exceptions and selected apps remain active");
        Check(rules.IndexOf("DOMAIN,api.example.org,DIRECT") <
              rules.IndexOf("DOMAIN-SUFFIX,example.org,VEGA"),
            "A specific DIRECT exception outranks a full-domain VPN rule");
        Check(!rules.Any(r=>r.Contains("DOMAIN-SUFFIX,openai.com")
            || r.Contains("DOMAIN-SUFFIX,claude.com")
            || r.Contains("DOMAIN-SUFFIX,example.net")
            || r.Contains("PROCESS-NAME,Telegram.exe")
            || r.Contains("PROCESS-NAME,Discord.exe")
            || r.Contains("DOMAIN-SUFFIX,discord.com")
            || r=="MATCH,VEGA"),
            "No implicit OpenAI, Claude, Telegram, Discord, auto-learned or blanket VPN routes");
        var plan=HotRuleProviders.Create(new[]{node},restored,settings);
        string joined=string.Join("\n",plan.Files.Values);
        Check(joined.Contains("example.org") && joined.Contains("chrome.exe")
            && joined.Contains("82.47.130.46")
            && !joined.Contains("openai.com") && !joined.Contains("example.net")
            && !joined.Contains("Discord.exe") && !joined.Contains("Telegram.exe"),
            "Hot providers contain only user-approved rules");
        var hot=VpnCore.BuildConfig(new[]{node},restored,settings,hotRules:true,preparedHotRules:plan);
        var hotRules=(List<string>)hot["rules"]!;
        Check(hotRules.Last()=="MATCH,DIRECT"
            && !hotRules.Any(r=>r.StartsWith("DOMAIN-SUFFIX,discord.")),
            "Hot provider route list preserves DIRECT fallback with no built-in Discord policy");
        UserStore.EnsureHome();
        HotRuleProviders.Stage(plan);
        foreach(var (name,cfg) in new[]{("plain",config),("hot",hot)})
        {
            var path=Path.Combine(UserStore.Home,"mine-"+name+".yaml");
            File.WriteAllText(path,YamlWriter.Serialize(cfg));
            using var check=Process.Start(new ProcessStartInfo(VpnCore.CoreExecutable) {
                ArgumentList={"-t","-f",path,"-d",UserStore.Home},
                UseShellExecute=false,CreateNoWindow=true,
                RedirectStandardOutput=true,RedirectStandardError=true
            }) ?? throw new Exception("Cannot start config validation");
            var stdout=check.StandardOutput.ReadToEndAsync();
            var stderr=check.StandardError.ReadToEndAsync();
            if(!check.WaitForExit(20000)) {check.Kill(true);throw new TimeoutException("Mihomo validation");}
            System.Threading.Tasks.Task.WaitAll(stdout,stderr);
            Check(check.ExitCode==0, "Actual Mihomo validates "+name+" only-mine rules");
        }
        VerifyUi();
        Console.WriteLine("ALL_ONLY_MINE_MODE_TESTS_PASS");
    }

    static void VerifyUi()
    {
        Exception? failure=null;
        var t=new Thread(()=>{
            MainWindow? w=null;
            try
            {
                w=new MainWindow{Left=-2100,Top=-2100,ShowInTaskbar=false,ShowActivated=false};
                T Get<T>(string key) where T:FrameworkElement => (T)w.FindName(key);
                void Call(string name)=>typeof(MainWindow).GetMethod(name,
                    BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(w,new object?[]{null,new RoutedEventArgs()});
                void Pump() {
                    var f=new DispatcherFrame();
                    Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,
                        new Action(()=>f.Continue=false));
                    Dispatcher.PushFrame(f);
                    w.UpdateLayout();
                }
                w.Show();
                typeof(MainWindow).GetMethod("HideOnboarding",BindingFlags.Instance|BindingFlags.NonPublic)!
                    .Invoke(w,null);
                var mode=Get<ComboBox>("ModeBox");
                Check(mode.Items.Count==3
                    && mode.Items.Cast<RoutingModeOption>().Any(x=>x.Id=="mine"),
                    "Only my rules is a real selectable mode");
                mode.SelectedValue="mine";
                Pump();
                var saved=UserStore.LoadSettings();
                Check(saved.Mode=="mine" && saved.Tun
                    && Get<CheckBox>("TunBox").IsChecked==true
                    && !Get<CheckBox>("TunBox").IsEnabled,
                    "New mode persists and forces TUN for full application routing");
                Check(!Get<CheckBox>("AutoLearnBox").IsEnabled,
                    "Auto-learning is not enabled in only-my-rules mode");
                Get<TabControl>("MainTabs").SelectedIndex=2;
                Pump();
                var list=Get<ListBox>("RulesList");
                var old=list.Items.Cast<RouteRule>().Single(x=>x.Domain=="example.org");
                list.SelectedItem=old;
                Check(Get<CheckBox>("MyRuleEnabledBox").IsChecked==true,
                    "Existing legacy manual rule is checked in the editor");
                Call("NewRule_Click");
                Check(Get<CheckBox>("MyRuleEnabledBox").IsChecked==true,
                    "New rules default to user's selected list");
                Get<TextBox>("DomainBox").Text="onlymine.example";
                Get<ComboBox>("RuleRouteBox").SelectedIndex=1;
                Get<CheckBox>("MyRuleEnabledBox").IsChecked=false;
                Call("AddRule_Click");
                var after=UserStore.LoadRules();
                Check(after.Any(r=>r.Domain=="onlymine.example"
                    && r.Manual && r.MyRuleEnabled==false),
                    "Unchecked user rule is saved but not included in strict mode");
                var updated=VpnCore.BuildConfig(new[]{new ProxyNode{
                    Name="TEST",Values=new(){["name"]="TEST",["type"]="socks5",
                        ["server"]="127.0.0.1",["port"]=18888}
                }},after,new AppSettings{Mode="mine",Tun=true,SelectedNode="TEST"});
                Check(!((List<string>)updated["rules"]!).Any(x=>x.Contains("onlymine.example")),
                    "Explicitly excluded rule does not create VPN connection");
                w.Close();
            }
            catch(Exception e){failure=e;try{w?.Close();}catch{}}
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        if(!t.Join(TimeSpan.FromSeconds(35)))throw new TimeoutException("Only-mine UI test timeout");
        if(failure!=null)throw new Exception("Only-mine UI test",failure);
    }
}
