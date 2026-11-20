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

public static class TunDomainTests
{
    static void Check(bool condition, string description)
    {
        if (!condition) throw new Exception("TUN_DOMAIN_FAILED: " + description);
        Console.WriteLine("PASS " + description);
    }

    public static void Run()
    {
        Check(!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PROSTOKVN_TEST_HOME")),
            "All tests run with isolated user data");
        Check(RouteTarget.TryParse("EXAMPLE.COM", out var suffix)
                && suffix.Kind == RouteTargetKind.Domain && suffix.Value == "example.com"
                && suffix.ToRule("VEGA") == "DOMAIN-SUFFIX,example.com,VEGA"
                && suffix.Matches("example.com", null) && suffix.Matches("api.example.com", null)
                && !suffix.Matches("notexample.com", null), "Legacy domain keeps suffix semantics");
        Check(RouteTarget.TryParse("domain-exact:EXAMPLE.COM", out var exact)
                && exact.Kind == RouteTargetKind.DomainExact
                && exact.Value == "domain-exact:example.com"
                && exact.DisplayValue == "example.com"
                && exact.ToRule("DIRECT") == "DOMAIN,example.com,DIRECT"
                && exact.Matches("example.com", null)
                && !exact.Matches("api.example.com", null), "Exact domain matches only one host");
        Check(!RouteTarget.TryParse("domain-exact:*.example.com", out _)
                && !RouteTarget.TryParse("domain-exact:bad,name.com", out _),
            "Exact domain rejects wildcard and comma injection");
        Check(exact.Priority > suffix.Priority, "Exact domain has priority over suffix rule");
        Check(RouteTarget.TryParse("*.example.com", out var legacyWildcard)
                && legacyWildcard.Kind == RouteTargetKind.Domain
                && legacyWildcard.ToRule("VEGA") == "DOMAIN-SUFFIX,example.com,VEGA",
            "Existing wildcard domain input stays compatible");
        Check(ServiceRuleGrouper.ServiceName("domain-exact:discord.com") == "Discord",
            "Exact host retains correct service group");
        var node = new ProxyNode { Name = "TEST", Values = new() {
            ["name"] = "TEST", ["type"] = "socks5",
            ["server"] = "127.0.0.1", ["port"] = 18888, ["udp"] = true
        }};
        var manual = new List<RouteRule> {
            new() { Domain="example.com",Route="VEGA",Manual=true },
            new() { Domain="domain-exact:api.example.com",Route="DIRECT",Manual=true },
            new() { Domain="process-name:Discord.exe",Route="DIRECT",Manual=true }
        };
        var settings = new AppSettings { Mode="apps",Tun=true,SelectedNode="TEST" };
        var oldConfig = VpnCore.BuildConfig(new[] { node }, manual, settings);
        var rules = (List<string>)oldConfig["rules"]!;
        Check(rules.IndexOf("DOMAIN,api.example.com,DIRECT") >= 0
              && rules.IndexOf("DOMAIN,api.example.com,DIRECT") <
                 rules.IndexOf("DOMAIN-SUFFIX,example.com,VEGA"),
            "Exact DIRECT exception precedes full-domain VPN rule");
        Check(VpnCore.IsStaleConnection("api.example.com",new[]{"VEGA"},manual,"apps")
                && VpnCore.IsStaleConnection("other.example.com",new[]{"DIRECT"},manual,"apps"),
            "Closed connections are refreshed for both exact and suffix routes");
        var plan = HotRuleProviders.Create(new[] {node},manual,settings);
        var patterns = string.Join("\n",plan.Files.Values);
        Check(patterns.Contains("DOMAIN,api.example.com") && patterns.Contains("DOMAIN-SUFFIX,example.com"),
            "Hot provider preserves both domain matching scopes");
        int exactIndex=-1,suffixIndex=-1;
        for(int i=0;i<plan.Rules.Count;i++)
        {
            var parts=plan.Rules[i].Split(',');
            if(parts.Length!=3 || !plan.Files.TryGetValue(parts[1],out var payload))continue;
            if(payload.Contains("'DOMAIN,api.example.com'"))exactIndex=i;
            if(payload.Contains("'DOMAIN-SUFFIX,example.com'"))suffixIndex=i;
        }
        Check(exactIndex>=0 && suffixIndex>=0 && exactIndex<suffixIndex,
            "Hot rule slots preserve exact override order");

        var tunDefault=(Dictionary<string,object?>)oldConfig["tun"]!;
        Check(Convert.ToString(tunDefault["stack"])=="mixed"
            && tunDefault["dns-hijack"] is string[] {Length:1}
            && tunDefault["strict-route"] is false,
            "Existing installation keeps compatible TUN defaults");
        var strict = new AppSettings {Mode="apps",Tun=true,SelectedNode="TEST",
            TunStack="system",TunDnsHijack=false,TunStrictRoute=true};
        var strictCfg = VpnCore.BuildConfig(new[] {node},manual,strict);
        var strictTun=(Dictionary<string,object?>)strictCfg["tun"]!;
        Check(Convert.ToString(strictTun["stack"])=="system"
            && strictTun["dns-hijack"] is string[] {Length:0}
            && strictTun["strict-route"] is true
            && strictTun["auto-route"] is true,
            "Custom TUN stack, DNS toggle and strict route are applied");
        strict.TunStack="gvisor";
        var gvisor=VpnCore.BuildConfig(new[]{node},manual,strict);
        Check(Convert.ToString(((Dictionary<string,object?>)gvisor["tun"]!)["stack"])=="gvisor",
            "gVisor stack is configurable");
        strict.TunStack="invalid";
        Check(Convert.ToString(((Dictionary<string,object?>)VpnCore.BuildConfig(new[]{node},manual,strict)["tun"]!)["stack"])=="mixed",
            "Invalid saved TUN stack falls back to mixed");
        UserStore.EnsureHome();
        HotRuleProviders.Stage(plan);
        var hot = VpnCore.BuildConfig(new[]{node},manual,settings,hotRules:true,preparedHotRules:plan);
        foreach(var (name,cfg) in new[] {("normal",oldConfig),("hot",hot),("strict",strictCfg),("gvisor",gvisor)})
        {
            var file=Path.Combine(UserStore.Home,"tun-domain-"+name+".yaml");
            File.WriteAllText(file,YamlWriter.Serialize(cfg));
            using var check=Process.Start(new ProcessStartInfo(VpnCore.CoreExecutable){
                ArgumentList = {"-t","-f",file,"-d",UserStore.Home},
                UseShellExecute=false,CreateNoWindow=true,
                RedirectStandardOutput=true,RedirectStandardError=true
            }) ?? throw new Exception("Mihomo validation did not start");
            var standard=check.StandardOutput.ReadToEndAsync();
            var errors=check.StandardError.ReadToEndAsync();
            if (!check.WaitForExit(20000)) {check.Kill(true); throw new Exception("Mihomo validation timeout");}
            Task.WaitAll(standard,errors);
            Check(check.ExitCode==0, "Real Mihomo validates "+name+" configuration: "+errors.Result[..Math.Min(200,errors.Result.Length)]);
        }
        UserStore.SaveRules(manual);
        UiCheck();
        Console.WriteLine("ALL_TUN_DOMAIN_TESTS_PASS");
    }

    static void UiCheck()
    {
        Exception? failure=null;
        var thread=new Thread(()=>{
            MainWindow? w=null;
            try{
                w=new MainWindow{Left=-2100,Top=-2100,ShowInTaskbar=false,ShowActivated=false};
                T Find<T>(string name) where T:FrameworkElement => (T)w.FindName(name);
                void Invoke(string name)=>typeof(MainWindow).GetMethod(name,BindingFlags.NonPublic|BindingFlags.Instance)!
                    .Invoke(w,new object?[]{null,new RoutedEventArgs()});
                void Pump(){
                    var frame=new DispatcherFrame();
                    Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,new Action(()=>frame.Continue=false));
                    Dispatcher.PushFrame(frame);
                    w.UpdateLayout();
                }
                w.Show();
                typeof(MainWindow).GetMethod("HideOnboarding",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(w,null);
                Find<TabControl>("MainTabs").SelectedIndex=2;
                Pump();
                Invoke("NewRule_Click");
                Find<TextBox>("DomainBox").Text="example.org";
                Check(Find<StackPanel>("DomainScopeSection").Visibility==Visibility.Visible,
                    "Editor offers matching scope for valid domains");
                Find<ComboBox>("DomainScopeBox").SelectedIndex=1;
                Invoke("AddRule_Click");
                Check(UserStore.LoadRules().Any(x=>x.Domain=="domain-exact:example.org" && x.Route=="VEGA"),
                    "GUI saves exact-domain scope in existing routes.json");
                Find<ListBox>("RulesList").SelectedItem=
                    Find<ListBox>("RulesList").Items.Cast<RouteRule>().Single(x=>x.Domain=="domain-exact:example.org");
                Check(Find<ComboBox>("DomainScopeBox").SelectedIndex==1
                    && Find<TextBox>("DomainBox").Text=="example.org",
                    "GUI loads exact domain for editing");
                Invoke("NewRule_Click");
                Find<TextBox>("DomainBox").Text="192.0.2.7";
                Check(Find<StackPanel>("DomainScopeSection").Visibility==Visibility.Collapsed,
                    "Domain scope selector is hidden for IP address");
                Find<TabControl>("MainTabs").SelectedIndex=3;
                Find<ComboBox>("TunStackBox").SelectedIndex=2;
                Find<CheckBox>("TunDnsHijackBox").IsChecked=false;
                Find<CheckBox>("TunStrictRouteBox").IsChecked=true;
                Invoke("SettingsChanged_Click");
                var saved=UserStore.LoadSettings();
                Check(saved.TunStack=="gvisor" && !saved.TunDnsHijack && saved.TunStrictRoute,
                    "WPF settings persist advanced TUN options");
                w.Close();
            } catch(Exception e){failure=e;try{w?.Close();}catch{}}
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if(!thread.Join(TimeSpan.FromSeconds(40)))throw new Exception("TUN/domain GUI test timed out");
        if(failure!=null)throw new Exception("TUN/domain GUI smoke test failed",failure);
    }
}
