using System.Diagnostics;
using System.IO;
using ProstoKVN;
using YamlDotNet.Serialization;

public static class HotRuleCapacityTests
{
    private sealed record Query(string Host, string Ip = "203.0.113.90", string Process = "browser.exe", string? Path = null);
    private static readonly ProxyNode[] Nodes = { new() { Name = "TEST", Values = new()
        { ["name"] = "TEST", ["type"] = "socks5", ["server"] = "127.0.0.1", ["port"] = 19888 } } };
    private static readonly AppSettings Settings = new() { Mode = "smart", SelectedNode = "TEST", Tun = false };

    private static void Check(bool ok, string label)
    {
        if (!ok) throw new Exception("HOT_RULE_CAPACITY_FAILED: " + label);
        Console.WriteLine("PASS " + label);
    }
    private static Dictionary<string,List<string>> Payloads(HotRulePlan plan)
    {
        var reader = new DeserializerBuilder().Build();
        return plan.Files.ToDictionary(x => x.Key,
            x => reader.Deserialize<Dictionary<string,List<string>>>(x.Value)["payload"]);
    }
    private static bool Matches(string type, string value, Query query)
    {
        string input = type switch
        {
            "PROCESS-NAME" => "process-name:" + value,
            "PROCESS-PATH" => "process-path:" + value,
            _ => value
        };
        return type is "DOMAIN-SUFFIX" or "IP-CIDR" or "IP-CIDR6" or "PROCESS-NAME" or "PROCESS-PATH"
            && RouteTarget.TryParse(input, out var target)
            && target.Matches(query.Host, query.Ip, query.Process, query.Path);
    }
    private static string Evaluate(IEnumerable<string> rules, Query query,
        Dictionary<string,List<string>>? providers = null)
    {
        foreach (string rule in rules)
        {
            string[] parts = rule.Split(',');
            if (parts[0] == "MATCH") return parts[1];
            if (parts[0] == "RULE-SET")
            {
                if (providers == null) throw new Exception("Missing rule providers");
                foreach (string pattern in providers[parts[1]])
                {
                    string[] condition = pattern.Split(',');
                    if (Matches(condition[0], condition[1], query)) return parts[2];
                }
            }
            else if (parts.Length >= 3 && Matches(parts[0], parts[1], query)) return parts[2];
        }
        return "DIRECT";
    }
    private static HotRulePlan VerifyEquivalent(List<RouteRule> rules, IEnumerable<Query> queries, string label)
    {
        var plan = HotRuleProviders.Create(Nodes, rules, Settings);
        var payloads = Payloads(plan);
        var inline = (List<string>)VpnCore.BuildConfig(Nodes, rules, Settings)["rules"]!;
        var hot = (List<string>)VpnCore.BuildConfig(Nodes, rules, Settings,
            hotRules: true, preparedHotRules: plan)["rules"]!;
        foreach (var query in queries)
        {
            string expected = Evaluate(inline, query);
            string actual = Evaluate(hot, query, payloads);
            if (expected != actual) throw new Exception(label + ": route changed for " + query + ": " + expected + " -> " + actual);
        }
        Check(true, label + ": hot rules match the inline routing order");
        Check(payloads.Values.Sum(x => x.Count) == rules.Count,
            label + ": every user rule is retained exactly once");
        return plan;
    }
    private static List<RouteRule> Nested(int count, bool manual)
        => Enumerable.Range(0,count).Select(i => new RouteRule {
            Domain = string.Concat(Enumerable.Repeat("a.",i)) + "root.example.test",
            Route = i % 2 == 0 ? "DIRECT" : "VEGA", Manual = manual }).ToList();

    private static async Task ValidateCore(HotRulePlan plan, List<RouteRule> rules, string label)
    {
        string core = Environment.GetEnvironmentVariable("PROSTOKVN_MIHOMO_TEST_CORE")
            ?? throw new Exception("Isolated Mihomo configuration test path required");
        string home = Path.Combine(UserStore.Home,label);
        Directory.CreateDirectory(Path.Combine(home,"hot-rules"));
        foreach(var (name,payload) in plan.Files)
            File.WriteAllText(Path.Combine(home,"hot-rules",name+".yaml"),payload);
        string path = Path.Combine(home,"config.yaml");
        File.WriteAllText(path,YamlWriter.Serialize(VpnCore.BuildConfig(Nodes,rules,Settings,
            hotRules:true,preparedHotRules:plan)));
        using var process = Process.Start(new ProcessStartInfo(core) {
            ArgumentList={"-t","-f",path,"-d",home}, UseShellExecute=false, CreateNoWindow=true,
            RedirectStandardOutput=true,RedirectStandardError=true })!;
        var output=process.StandardOutput.ReadToEndAsync();
        var error=process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { process.Kill(true); throw new TimeoutException("Mihomo configuration test timed out"); }
        await Task.WhenAll(output,error);
        if(process.ExitCode!=0) throw new Exception(label+": "+output.Result+error.Result);
        Check(true,"Real Mihomo accepts " + label);
    }

    public static async Task RunAsync()
    {
        Check(!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PROSTOKVN_TEST_HOME")),"Isolated settings required");
        var nineteen = Enumerable.Range(0,19).Select(i => new RouteRule {
            Domain = new string('b',i+1)+".example.test", Route=i%2==0?"DIRECT":"VEGA" }).ToList();
        var ordered = nineteen.OrderByDescending(r => r.Domain.Length).ToArray();
        int oldGroups = 1 + ordered.Zip(ordered.Skip(1)).Count(pair => pair.First.Route!=pair.Second.Route);
        Check(oldGroups==19,"Reproduces the reported 19 automatic groups");
        var compact = VerifyEquivalent(nineteen,nineteen.SelectMany(r => new[]{new Query(r.Domain),new Query("sub."+r.Domain)}),"19 independent AUTO groups");
        Check(compact.PhaseSlots["auto"]==16 && Payloads(compact).Values.Count(x=>x.Count>0)==2,
            "Independent automatic routes share two groups without growing provider slots");
        await ValidateCore(compact,nineteen,"auto-19");

        var nestedAuto = Nested(35,false);
        var expanded = VerifyEquivalent(nestedAuto,nestedAuto.Select(r=>new Query(r.Domain)),"35 nested AUTO groups");
        Check(expanded.PhaseSlots["auto"]>=35,"Overlapping automatic routes expand beyond 16 slots");
        await ValidateCore(expanded,nestedAuto,"auto-35-overlap");
        var nestedManual = Nested(67,true);
        var manualPlan = VerifyEquivalent(nestedManual,nestedManual.Select(r=>new Query(r.Domain)),"67 nested manual groups");
        Check(manualPlan.PhaseSlots["manual"]>=67,"Manual groups also expand without an artificial limit");
        await ValidateCore(manualPlan,nestedManual,"manual-67-overlap");

        var reducedRules = nestedAuto.Take(3).ToList();
        var reduced = HotRuleProviders.Create(Nodes,reducedRules,Settings,previousPlan:expanded);
        string initialConfig=YamlWriter.Serialize(VpnCore.BuildConfig(Nodes,nestedAuto,Settings,hotRules:true,preparedHotRules:expanded));
        string reducedConfig=YamlWriter.Serialize(VpnCore.BuildConfig(Nodes,reducedRules,Settings,hotRules:true,preparedHotRules:reduced));
        Check(expanded.PhaseSlots["auto"]==reduced.PhaseSlots["auto"] && initialConfig==reducedConfig,
            "Removing rules reuses reserved slots so routine hot updates keep the same configuration");
        Check(expanded.Files.Any(x=>reduced.Files[x.Key]!=x.Value),"Hot update changes provider payloads rather than the network configuration");

        var mixed = new List<RouteRule> {
            new(){Domain=@"process-path:C:\Apps\Client\client.exe",Route="REJECT",Manual=true},
            new(){Domain="process-name:client.exe",Route="VEGA",Manual=true},
            new(){Domain="process-name:other.exe",Route="DIRECT",Manual=true},
            new(){Domain="198.51.100.7",Route="DIRECT",Manual=true},
            new(){Domain="198.51.100.0/24",Route="REJECT",Manual=true},
            new(){Domain="198.51.0.0/16",Route="VEGA",Manual=true},
            new(){Domain="2001:db8:1::/48",Route="DIRECT",Manual=true},
            new(){Domain="2001:db8::/32",Route="VEGA",Manual=true},
            new(){Domain="exact.sub.example.test",Route="REJECT",Manual=true},
            new(){Domain="sub.example.test",Route="VEGA",Manual=true},
            new(){Domain="example.test",Route="DIRECT",Manual=true},
            new(){Domain="sub.example.test",Route="VEGA"},
            new(){Domain="outside.test",Route="VEGA"}
        };
        var queries = new[] {
            new Query("sub.example.test"),new Query("exact.sub.example.test"),new Query("www.example.test"),
            new Query("outside.test"),new Query("unmatched.test"),
            new Query("sub.example.test","198.51.100.7"),new Query("outside.test","198.51.100.8"),
            new Query("outside.test","198.51.101.8"),new Query("outside.test","2001:db8:1::8"),
            new Query("outside.test","2001:db8:2::8"),
            new Query("exact.sub.example.test",Process:"client.exe",Path:@"C:\Apps\Client\client.exe"),
            new Query("exact.sub.example.test",Process:"client.exe",Path:@"D:\Other\client.exe"),
            new Query("exact.sub.example.test",Process:"other.exe")
        };
        var mixedPlan=VerifyEquivalent(mixed,queries,"Domain, IP, subnet and application priorities");
        await ValidateCore(mixedPlan,mixed,"mixed-targets");
        var random = new Random(42);
        for(int iteration=0;iteration<30;iteration++)
        {
            var sample=Enumerable.Range(0,100).Select(i => new RouteRule {
                Domain=(random.Next(4)==0?"child.":"")+"service-"+random.Next(35)+".example.test",
                Route=new[]{"DIRECT","VEGA","REJECT"}[random.Next(3)],Manual=true }).ToList();
            VerifyEquivalent(sample,Enumerable.Range(0,35).SelectMany(i=>new[]{
                new Query("service-"+i+".example.test"),new Query("child.service-"+i+".example.test")}),
                "Overlapping domain sample " + iteration);
        }
        Console.WriteLine("ALL_HOT_RULE_CAPACITY_TESTS_PASS");
    }
}
