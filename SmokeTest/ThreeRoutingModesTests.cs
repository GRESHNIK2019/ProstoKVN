using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ProstoKVN;
using YamlDotNet.Serialization;

public static class ThreeRoutingModesTests
{
    static void Check(bool ok, string message)
    { if (!ok) throw new Exception("THREE_MODES_FAILED: " + message); Console.WriteLine("PASS " + message); }
    static readonly ProxyNode[] Nodes = { new() { Name="TEST", Values=new() {
        ["name"]="TEST", ["type"]="socks5", ["server"]="127.0.0.1", ["port"]=19888, ["udp"]=true } } };
    sealed record Flow(string Host, string Process="browser.exe", string? Path=null, string Ip="198.51.100.9");
    static List<RouteRule> OriginalRules() => new() {
        new() {Domain="process-name:DISCORD.exe", Route="DIRECT", Manual=true, MyRuleEnabled=false, Label="My Discord exception"},
        new() {Domain="process-name:chrome.exe", Route="VEGA", Manual=true, MyRuleEnabled=false},
        new() {Domain=@"process-path:C:\Apps\DiscordCanary.exe", Route="DIRECT", Manual=true},
        new() {Domain="work.example", Route="REJECT", Manual=true},
        new() {Domain="domain-exact:api.discord.com", Route="DIRECT", Manual=true},
        new() {Domain="example.org", Route="VEGA", Manual=true},
        new() {Domain="automatic.test", Route="VEGA", MyRuleEnabled=true},
        new() {Domain="process-name:DiscordPTB.exe", Route="DIRECT", MyRuleEnabled=true}
    };
    static string Json(object value) => JsonSerializer.Serialize(value);

    public static async Task RunAsync()
    {
        Check(!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PROSTOKVN_TEST_HOME")), "Isolated test data required");
        Check(RoutingModes.Options.Select(x=>x.Id).SequenceEqual(new[]{"vpn","mine","smart"})
            && RoutingModes.Options.Select(x=>x.Title).SequenceEqual(new[]{"Весь трафик","Только выбранное","Умный"}),
            "Exactly three named primary modes exist");
        var oldSettings=new AppSettings {Mode="apps", SelectedNode="TEST", TunStack="gvisor", TunDnsHijack=false,
            TunStrictRoute=true, AutoLearn=false, ProtectedUrl="protected-fixture", Theme="dark"};
        var original=OriginalRules(); string untouched=Json(original);
        var upgrade=RoutingModeMigration.Plan(oldSettings,original);
        Check(upgrade.Changed && upgrade.Settings.Mode=="mine" && upgrade.Settings.Tun, "Legacy apps mode becomes selected-only with TUN");
        Check(oldSettings.Mode=="apps" && !oldSettings.Tun && Json(original)==untouched, "Planning never mutates original settings or rules");
        Check(upgrade.Settings.SelectedNode==oldSettings.SelectedNode && upgrade.Settings.ProtectedUrl==oldSettings.ProtectedUrl
            && upgrade.Settings.TunStack=="gvisor" && !upgrade.Settings.TunDnsHijack && upgrade.Settings.TunStrictRoute
            && !upgrade.Settings.AutoLearn && upgrade.Settings.Theme=="dark", "Migration retains independent connection settings");
        Check(upgrade.Rules.Where(r=>r.Preset=="Discord").Count()==3
            && upgrade.Rules.Where(r=>r.Preset=="Discord").All(r=>r.Manual && r.IncludedInMine && r.Route=="VEGA"),
            "Discord becomes visible enabled preset rules without duplicating the manual override");
        Check(upgrade.Rules.Single(r=>r.Domain=="process-name:DISCORD.exe").Route=="DIRECT"
            && upgrade.Rules.Single(r=>r.Domain=="process-name:chrome.exe").IncludedInMine
            && upgrade.Rules.Where(r=>!r.Manual).All(r=>!r.IncludedInMine), "Manual and automatic ownership flags preserve the old apps behavior");

        var flows=new[] {
            new Flow("generic.test"), new Flow("generic.test","Discord.exe"), new Flow("generic.test","DiscordPTB.exe"),
            new Flow("generic.test","DiscordCanary.exe"), new Flow("generic.test","DiscordDevelopment.exe"),
            new Flow("work.example","DiscordPTB.exe"), new Flow("work.example","DiscordCanary.exe"),
            new Flow("api.discord.com","DiscordPTB.exe"), new Flow("other.discord.com","browser.exe"),
            new Flow("generic.test","DiscordCanary.exe",@"C:\Apps\DiscordCanary.exe"), new Flow("generic.test","chrome.exe"),
            new Flow("automatic.test"), new Flow("example.org"), new Flow("chatgpt.com"),
            new Flow("149.154.160.1","Telegram.exe",Ip:"149.154.160.1")
        };
        var before=VpnCore.BuildConfig(Nodes,original,oldSettings);
        var after=VpnCore.BuildConfig(Nodes,upgrade.Rules,upgrade.Settings);
        var plan=HotRuleProviders.Create(Nodes,upgrade.Rules,upgrade.Settings);
        var hot=VpnCore.BuildConfig(Nodes,upgrade.Rules,upgrade.Settings,hotRules:true,preparedHotRules:plan);
        foreach(var flow in flows)
        {
            string expected=Evaluate(before,flow);
            Check(expected==Evaluate(after,flow) && expected==Evaluate(hot,flow,plan)
                && expected==RoutingPolicy.ExpectedRoute(upgrade.Rules,"mine",flow.Host,flow.Ip,flow.Process,flow.Path),
                "Same route before/after migration, inline/hot/runtime: "+flow.Host+" / "+flow.Process+" = "+expected);
        }
        var retried=RoutingModeMigration.Plan(oldSettings,upgrade.Rules);
        Check(Json(retried.Rules)==Json(upgrade.Rules), "Interrupted migration can be planned again without duplicates or changes");
        Check(!RoutingModeMigration.Plan(upgrade.Settings,upgrade.Rules).Changed, "Completed migration is a no-op");
        foreach(string mode in new[]{"smart","vpn","mine"})
            Check(!RoutingModeMigration.Plan(new AppSettings{Mode=mode},original).Changed, "Existing primary mode stays intact: "+mode);

        var direct=RoutingModeMigration.Plan(new AppSettings{Mode="direct"},original);
        Check(direct.Settings.Mode=="mine" && direct.Rules.All(r=>!r.IncludedInMine), "Legacy DIRECT disables all selected rules");
        foreach(var flow in flows) Check(RoutingPolicy.ExpectedRoute(direct.Rules,"mine",flow.Host,flow.Ip,flow.Process,flow.Path)=="DIRECT",
            "Legacy DIRECT still routes directly: "+flow.Process);
        HotRuleProviders.Stage(plan);
        await Validate(hot,"migrated-apps");
        await Validate(VpnCore.BuildConfig(Nodes,direct.Rules,direct.Settings),"migrated-direct");
        Persistence(oldSettings,original);
        Ui();
        Console.WriteLine("ALL_THREE_ROUTING_MODES_TESTS_PASS");
    }

    static string Evaluate(Dictionary<string,object?> config, Flow flow, HotRulePlan? plan=null)
    {
        Dictionary<string,List<string>> payloads=plan==null?new():plan.Files.ToDictionary(x=>x.Key,
            x=>new DeserializerBuilder().Build().Deserialize<Dictionary<string,List<string>>>(x.Value)["payload"]);
        bool Match(string condition)
        {
            var p=condition.Split(','); if(p.Length<2)return false;
            string input=p[0] switch {"DOMAIN"=>"domain-exact:"+p[1],"PROCESS-NAME"=>"process-name:"+p[1],
                "PROCESS-PATH"=>"process-path:"+p[1],_=>p[1]};
            return p[0] is "DOMAIN" or "DOMAIN-SUFFIX" or "PROCESS-NAME" or "PROCESS-PATH" or "IP-CIDR" or "IP-CIDR6"
                && RouteTarget.TryParse(input,out var target) && target.Matches(flow.Host,flow.Ip,flow.Process,flow.Path);
        }
        foreach(string rule in (List<string>)config["rules"]!)
        {
            var p=rule.Split(',');if(p[0]=="MATCH")return p[1];
            if(p[0]=="RULE-SET") {if(payloads[p[1]].Any(Match))return p[2];}
            else if(p.Length>=3 && Match(rule))return p[2];
        }
        throw new Exception("No MATCH fallback");
    }

    static async Task Validate(Dictionary<string,object?> config,string name)
    {
        string path=Path.Combine(UserStore.Home,name+".yaml"); File.WriteAllText(path,YamlWriter.Serialize(config));
        using var process=Process.Start(new ProcessStartInfo(VpnCore.CoreExecutable) {
            ArgumentList={"-t","-f",path,"-d",UserStore.Home}, UseShellExecute=false,CreateNoWindow=true,
            RedirectStandardError=true,RedirectStandardOutput=true})!;
        var stdout=process.StandardOutput.ReadToEndAsync();var stderr=process.StandardError.ReadToEndAsync();
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try {await process.WaitForExitAsync(timeout.Token);}catch{process.Kill(true);throw;}
        await Task.WhenAll(stdout,stderr);
        Check(process.ExitCode==0,"Actual Mihomo validates "+name+(process.ExitCode==0?"":": "+stdout.Result+stderr.Result));
    }

    static void Persistence(AppSettings settings,List<RouteRule> original)
    {
        UserStore.SaveSettings(settings);UserStore.SaveRules(original);
        string settingsPath=Path.Combine(UserStore.Home,"settings.json"), rulesPath=Path.Combine(UserStore.Home,"routes.json");
        string oldSettings=File.ReadAllText(settingsPath),oldRules=File.ReadAllText(rulesPath);
        var stored=UserStore.UpgradeRoutingModes(UserStore.LoadSettings(),UserStore.LoadRules());
        Check(File.ReadAllText(Path.Combine(stored.BackupDirectory,"settings.json"))==oldSettings
            && File.ReadAllText(Path.Combine(stored.BackupDirectory,"routes.json"))==oldRules, "Both original files are backed up exactly before committing");
        Check(UserStore.LoadSettings().Mode=="mine" && Json(UserStore.LoadRules())==Json(stored.Rules), "Both migrated files are persisted");
        int backups=Directory.GetDirectories(UserStore.Home,"routing-modes-backup-*").Length;
        Check(!UserStore.UpgradeRoutingModes(UserStore.LoadSettings(),UserStore.LoadRules()).Changed
            && Directory.GetDirectories(UserStore.Home,"routing-modes-backup-*").Length==backups, "Second load neither rewrites nor creates another backup");
        UserStore.SaveSettings(settings);UserStore.SaveRules(original);
        string saved=settingsPath+".fixture";File.Move(settingsPath,saved);
        try
        {
            Directory.CreateDirectory(settingsPath);
            bool failed=false;
            try{UserStore.UpgradeRoutingModes(settings,original);}catch(Exception e) when(e is IOException or UnauthorizedAccessException){failed=true;}
            Check(failed && Json(UserStore.LoadRules())==Json(original), "Settings write failure restores the original rules");
        }
        finally {Directory.Delete(settingsPath);File.Move(saved,settingsPath);}
    }

    static void Ui()
    {
        UserStore.SaveSettings(new AppSettings{Mode="apps",SelectedNode="TEST"});UserStore.SaveRules(OriginalRules());
        Exception? failure=null;var thread=new Thread(()=>{
            MainWindow? w=null;
            try
            {
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
                w=new MainWindow{Left=-2400,Top=-2400,ShowInTaskbar=false,ShowActivated=false,WindowStartupLocation=WindowStartupLocation.Manual};
                T Get<T>(string n) where T:FrameworkElement => (T)w.FindName(n);
                void Call(string n,params object?[] a)=>typeof(MainWindow).GetMethod(n,BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(w,a);
                void Pump(){var f=new DispatcherFrame();Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,new Action(()=>f.Continue=false));Dispatcher.PushFrame(f);w.UpdateLayout();}
                void Save(FrameworkElement surface,string name)
                {
                    Pump();var bmp=new RenderTargetBitmap((int)Math.Ceiling(surface.ActualWidth),(int)Math.Ceiling(surface.ActualHeight),96,96,PixelFormats.Pbgra32);bmp.Render(surface);
                    var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bmp));using var file=File.Create(Path.Combine(UserStore.Home,name+".png"));png.Save(file);
                }
                w.Show();Pump();Call("HideOnboarding");Get<TabControl>("MainTabs").SelectedIndex=2;Pump();
                var mode=Get<ComboBox>("ModeBox");
                Check(mode.Items.Count==3 && (string?)mode.SelectedValue=="mine" && UserStore.LoadSettings().Mode=="mine", "GUI loads and persists the migrated primary mode");
                Check(Get<TextBlock>("ModeMigrationNotice").Text.Contains("Discord"), "GUI explains the legacy migration");
                foreach(var option in RoutingModes.Options)
                {
                    mode.SelectedValue=option.Id;Pump();
                    Check(UserStore.LoadSettings().Mode==option.Id && mode.SelectedItem is RoutingModeOption selected && selected.Title==option.Title,
                        "Selection persists by stable ID: "+option.Title);
                    Check(mode.SelectionBoxItem?.ToString()==option.Title, "Collapsed selector displays the readable title: "+option.Title);
                    Check(Get<CheckBox>("TunBox").IsEnabled==(option.Id!="mine") && Get<CheckBox>("AutoLearnBox").IsEnabled==(option.Id=="smart"),
                        "TUN and learning controls match "+option.Id);
                }
                mode.SelectedValue="mine";Call("ApplicationsRules_Click",null,new RoutedEventArgs());Pump();
                Check(Get<ListBox>("RulesList").Items.Cast<RouteRule>().Count(r=>r.Preset=="Discord")==3, "Migrated Discord presets are visible and editable in My applications");
                foreach(string theme in new[]{"light","dark"})
                {
                    Call("ApplyTheme",theme,false);w.Width=theme=="light"?1040:840;w.Height=theme=="light"?740:620;
                    Get<ScrollViewer>("RulesPageScroll").ScrollToTop();Save(w,theme+"-three-modes");
                    mode.IsDropDownOpen=true;Pump();var popup=(Popup)mode.Template.FindName("PART_Popup",mode);
                    Save((FrameworkElement)popup.Child,theme+"-mode-menu");mode.IsDropDownOpen=false;
                }
                Check(!((VpnCore)typeof(MainWindow).GetField("core",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(w)!).IsRunning,
                    "Migration and mode selection never connect a VPN");
            }
            catch(Exception e){failure=e;}finally{w?.Close();}
        });thread.SetApartmentState(ApartmentState.STA);thread.Start();
        if(!thread.Join(TimeSpan.FromSeconds(45)))throw new TimeoutException("Three-mode UI test");if(failure!=null)throw new Exception("Three-mode GUI",failure);
    }
}
