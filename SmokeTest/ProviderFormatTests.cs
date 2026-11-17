using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ProstoKVN;

public static class ProviderFormatTests
{
    static void Check(bool value,string label)
    {
        if(!value) throw new Exception("PROVIDER_FORMAT_FAILED: "+label);
        Console.WriteLine("PASS "+label);
    }
    static string Url(string name) => "https://"+name+"-"+Guid.NewGuid().ToString("N")+".test.invalid/token";
    static HttpResponseMessage Response(string body,HttpStatusCode status=HttpStatusCode.OK,params string[] headers)
    {
        var response=new HttpResponseMessage(status){Content=new StringContent(body)};
        foreach(string name in headers) response.Headers.TryAddWithoutValidation(name,"true");
        return response;
    }
    static readonly string Native = """
    [
      {"remarks":"VLESS XHTTP","routing":{"rules":[{"outboundTag":"block"}]},"inbounds":[{"port":1234}],"outbounds":[
        {"tag":"direct","protocol":"freedom"},
        {"tag":"proxy","protocol":"vless","settings":{"vnext":[{"address":"edge.example","port":443,"users":[{"id":"00000000-0000-0000-0000-000000000001","encryption":"none"}]}]},"streamSettings":{"network":"xhttp","security":"reality","realitySettings":{"serverName":"front.example","publicKey":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA","shortId":"deadbeef","fingerprint":"chrome"},"xhttpSettings":{"path":"/connect","mode":"auto","extra":{"noGRPCHeader":true}}}}
      ]},
      {"remarks":"Trojan gRPC","outbounds":[
        {"tag":"proxy","protocol":"trojan","settings":{"servers":[{"address":"grpc.example","port":443,"password":"trojan-fixture"}]},"streamSettings":{"network":"grpc","security":"tls","tlsSettings":{"serverName":"grpc-front.example","alpn":["h2"],"fingerprint":"chrome"},"grpcSettings":{"serviceName":"tunnel-service","multiMode":true},"sockopt":{"tcpFastOpen":true}}}
      ]},
      {"remarks":"Hysteria2","outbounds":[
        {"tag":"proxy","protocol":"hysteria","settings":{"address":"hy.example","port":443,"version":2},"streamSettings":{"network":"hysteria","security":"tls","hysteriaSettings":{"version":2,"auth":"hysteria-fixture"},"tlsSettings":{"serverName":"hy-front.example","alpn":["h3"]},"finalmask":{"quicParams":{"congestion":"bbr"}}}}
      ]}
    ]
    """;
    static async Task CheckCore(ProxyNode node,bool sensitive)
    {
        string file=Path.Combine(UserStore.Home,"native-"+Guid.NewGuid().ToString("N")+".json");
        try
        {
            File.WriteAllText(file,XraySidecar.BuildConfig(node.NativeXrayConfig!).ToJsonString());
            using var process=Process.Start(new ProcessStartInfo(XraySidecar.Executable){
                ArgumentList={"run","-test","-config",file},UseShellExecute=false,CreateNoWindow=true,
                RedirectStandardOutput=true,RedirectStandardError=true})!;
            var output=process.StandardOutput.ReadToEndAsync();var error=process.StandardError.ReadToEndAsync();
            using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(20));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch(OperationCanceledException){process.Kill(true);throw new Exception("Native configuration validation timed out");}
            await Task.WhenAll(output,error);
            if(process.ExitCode!=0) throw new Exception("Xray rejected a "+node.Values["type"]+" configuration; exit="+process.ExitCode
                +(sensitive?"":": "+output.Result+error.Result));
        }
        finally { if(File.Exists(file)) File.Delete(file); }
    }
    static async Task CheckRouter(IReadOnlyList<ProxyNode> nodes,ProxyNode selected)
    {
        string file=Path.Combine(UserStore.Home,"mihomo-"+Guid.NewGuid().ToString("N")+".yaml");
        try
        {
            var settings=new AppSettings{Mode="smart",SelectedNode=selected.Name,Tun=false};
            File.WriteAllText(file,YamlWriter.Serialize(VpnCore.BuildConfig(nodes,new List<RouteRule>(),settings,useXraySidecar:true)));
            string core=Environment.GetEnvironmentVariable("PROSTOKVN_MIHOMO_TEST_CORE")??VpnCore.CoreExecutable;
            using var process=Process.Start(new ProcessStartInfo(core){
                ArgumentList={"-t","-f",file,"-d",UserStore.Home},UseShellExecute=false,CreateNoWindow=true,
                RedirectStandardError=true,RedirectStandardOutput=true})!;
            var output=process.StandardOutput.ReadToEndAsync();var error=process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();await Task.WhenAll(output,error);
            Check(process.ExitCode==0,"Mihomo accepts all provider nodes with a selected "+selected.Values["type"]+" Xray adapter");
        }
        finally{if(File.Exists(file))File.Delete(file);}
    }
    static async Task CheckEgress(ProxyNode node)
    {
        var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();
        int port=((IPEndPoint)listener.LocalEndpoint).Port;listener.Stop();
        string file=Path.Combine(UserStore.Home,"egress-"+Guid.NewGuid().ToString("N")+".json");
        Process? process=null;
        Task<string>? output=null,error=null;
        bool failed=true;
        try
        {
            var config=XraySidecar.BuildConfig(node.NativeXrayConfig!);
            if(Environment.GetEnvironmentVariable("PROSTOKVN_PROVIDER_DEBUG")=="1")
                config["log"]!["loglevel"]="debug";
            config["inbounds"]![0]!["port"]=port;
            File.WriteAllText(file,config.ToJsonString());
            string executable=Environment.GetEnvironmentVariable("PROSTOKVN_PROVIDER_XRAY_CORE")??XraySidecar.Executable;
            string configArgument="-config";
            if(Environment.GetEnvironmentVariable("PROSTOKVN_PROVIDER_EGRESS_ENGINE")=="singbox")
            {
                var copy=new ProxyNode{Name=node.Name,Values=new(node.Values)};
                var original=JsonNode.Parse(node.NativeXrayConfig!)!["outbounds"]![0]!;
                var mask=original["streamSettings"]?["finalmask"]?["udp"]?.AsArray()
                    .FirstOrDefault(m=>m?["type"]?.ToString()=="salamander");
                if(mask!=null){copy.Values["obfs"]="salamander";copy.Values["obfs-password"]=mask["settings"]!["password"]!.ToString();}
                File.WriteAllText(file,JsonSerializer.Serialize(SingBoxSidecar.BuildConfig(copy,port)));
                executable=SingBoxSidecar.Executable;configArgument="-c";
            }
            process=Process.Start(new ProcessStartInfo(executable){
                ArgumentList={"run",configArgument,file},UseShellExecute=false,CreateNoWindow=true,
                RedirectStandardOutput=true,RedirectStandardError=true})!;
            output=process.StandardOutput.ReadToEndAsync();error=process.StandardError.ReadToEndAsync();
            bool ready=false;
            for(int i=0;i<30 && !process.HasExited;i++)
            {
                try{using var socket=new TcpClient();await socket.ConnectAsync(IPAddress.Loopback,port);ready=true;break;}
                catch(SocketException){await Task.Delay(100);}
            }
            if(!ready)throw new Exception("Isolated Xray listener did not start for "+node.Values["type"]);
            using var handler=new HttpClientHandler{UseProxy=true,Proxy=new WebProxy("socks5://127.0.0.1:"+port),UseCookies=false};
            using var client=new HttpClient(handler){Timeout=TimeSpan.FromSeconds(25)};
            foreach(string target in new[]{"https://www.cloudflare.com/cdn-cgi/trace","https://example.com/"})
            {
                try
                {
                    using var response=await client.GetAsync(target);
                    if(response.IsSuccessStatusCode){failed=false;break;}
                }
                catch(Exception e) when(e is HttpRequestException or TaskCanceledException){}
            }
            Check(!failed,"Real HTTPS traffic succeeds through an isolated "+node.Values["type"]+" SOCKS connection");
        }
        finally
        {
            if(process!=null){if(!process.HasExited){process.Kill(true);await process.WaitForExitAsync();}process.Dispose();}
            if(output!=null && error!=null)
            {
                await Task.WhenAll(output,error);
                if(failed)
                {
                    string log=output.Result+error.Result;
                    using var json=JsonDocument.Parse(node.NativeXrayConfig!);
                    void Redact(JsonElement element)
                    {
                        if(element.ValueKind==JsonValueKind.Object)foreach(var p in element.EnumerateObject())Redact(p.Value);
                        else if(element.ValueKind==JsonValueKind.Array)foreach(var item in element.EnumerateArray())Redact(item);
                        else if(element.ValueKind==JsonValueKind.String && element.GetString() is {Length:>3} text)
                            log=log.Replace(text,"[value]",StringComparison.OrdinalIgnoreCase);
                    }
                    Redact(json.RootElement);
                    log=System.Text.RegularExpressions.Regex.Replace(log,@"\b(?:\d{1,3}\.){3}\d{1,3}\b","[IP]");
                    Console.WriteLine("REDACTED_EGRESS_DIAGNOSTIC="+log[^Math.Min(1800,log.Length)..]);
                }
            }
            if(File.Exists(file))File.Delete(file);
        }
    }
    public static async Task RunAsync()
    {
        Check(!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PROSTOKVN_TEST_HOME")),"Isolated settings required");
        UserStore.EnsureHome();
        string? egressFamily=Environment.GetEnvironmentVariable("PROSTOKVN_PROVIDER_EGRESS_ONLY");
        if(!string.IsNullOrWhiteSpace(egressFamily))
        {
            string snapshot=Environment.GetEnvironmentVariable("PROSTOKVN_PROVIDER_FIXTURE")!;
            byte[] data=ProtectedData.Unprotect(File.ReadAllBytes(snapshot),null,DataProtectionScope.CurrentUser);
            try
            {
                var family=JsonSubscription.ParseContent(Encoding.UTF8.GetString(data))
                    .Where(n=>n.Values["type"]?.ToString()==egressFamily).ToList();
                string region=Environment.GetEnvironmentVariable("PROSTOKVN_PROVIDER_EGRESS_REGION")??"Германия";
                await CheckEgress(family.FirstOrDefault(n=>n.Name.Contains(region,StringComparison.OrdinalIgnoreCase))??family.First());
            }
            finally{CryptographicOperations.ZeroMemory(data);}
            return;
        }
        Check(SubscriptionDeviceIdentity.Id.Length==64 && SubscriptionDeviceIdentity.Id.All(Uri.IsHexDigit)
            && SubscriptionDeviceIdentity.Id==SubscriptionDeviceIdentity.Id,"Device identifier is stable and matches Remnawave's format");
        var nodes=JsonSubscription.ParseContent(Native);
        Check(nodes.Count==3 && nodes.All(n=>EngineSelector.Choose(n)==TunnelEngine.Xray),
            "Native VLESS XHTTP, Trojan gRPC and Hysteria2 select Xray");
        var source=JsonNode.Parse(Native)!.AsArray();
        for(int i=0;i<nodes.Count;i++)
        {
            var expected=source[i]!["outbounds"]!.AsArray().Single(o=>o!["tag"]!.ToString()=="proxy");
            var actual=JsonNode.Parse(nodes[i].NativeXrayConfig!)!["outbounds"]![0];
            Check(JsonNode.DeepEquals(expected,actual),"Native outbound "+i+" retains TLS and transport options exactly");
            var safe=JsonNode.Parse(nodes[i].NativeXrayConfig!)!.AsObject();
            Check(safe.Count==1 && safe.ContainsKey("outbounds"),"Provider inbounds and routing are excluded from imported node "+i);
            var runtime=XraySidecar.BuildConfig(nodes[i].NativeXrayConfig!);
            Check(runtime["dns"]?["servers"]?[0]?.ToString().StartsWith("https+local://")==true
                && runtime["outbounds"]![0]!["streamSettings"]!["sockopt"]!["domainStrategy"]!.ToString()=="UseIPv4",
                "Native VPN endpoint "+i+" resolves independently of TUN Fake-IP DNS");
            await CheckCore(nodes[i],false);
        }
        Check(nodes[1].Values["tls"] is true && nodes[1].Values["servername"]?.ToString()=="grpc-front.example",
            "Trojan TLS metadata is retained for fallback configuration checks");
        Check(EngineSelector.Choose(new ProxyNode{Values=new(){["type"]="hysteria2"}})==TunnelEngine.SingBox,
            "Existing share-link Hysteria2 still selects sing-box");
        Console.WriteLine("PASS Real Xray validates all three synthetic native transports");
        SubscriptionTransport.TestVpnAvailable=()=>Task.FromResult(false);
        try
        {
            string challenge=Url("challenge");var ids=new List<string>();int calls=0;
            SubscriptionTransport.TestSend=(_,request)=>{
                calls++;
                Check(request.Headers.UserAgent.ToString().StartsWith("ProstoKVN/"),"Requests identify ProstoKVN honestly");
                if(!request.Headers.TryGetValues("x-hwid",out var values))
                    return Task.FromResult(Response("",HttpStatusCode.NotFound,"x-hwid-not-supported"));
                ids.Add(values.Single());return Task.FromResult(Response(Native,HttpStatusCode.OK,"x-hwid-active"));
            };
            Check(await SubscriptionTransport.GetStringAsync(challenge)==Native,"HWID challenge retries the same subscription with device metadata");
            await SubscriptionTransport.GetStringAsync(challenge);
            Check(calls==3 && ids.Distinct().Count()==1,"Later updates use the same device identifier without registering extra devices");

            int denied=0;SubscriptionTransport.TestVpnAvailable=()=>Task.FromResult(true);
            SubscriptionTransport.TestSend=(vpn,_)=>{
                Check(vpn,"Provider denials retain the original download route");denied++;
                return Task.FromResult(Response("",HttpStatusCode.OK,"x-hwid-max-devices-reached"));
            };
            bool rejected=false;
            try {await SubscriptionTransport.GetStringAsync(Url("full"));}
            catch(SubscriptionAccessException e){rejected=e.Failure==SubscriptionAccessFailure.DeviceLimit;}
            Check(rejected && denied==1,"A full device limit is reported and never bypassed or retried");
            denied=0;SubscriptionTransport.TestSend=(_,_)=>{denied++;return Task.FromResult(Response("",HttpStatusCode.Forbidden));};
            try{await SubscriptionTransport.GetStringAsync(Url("forbidden"));}catch(HttpRequestException){}
            Check(denied==1,"HTTP 403 without an HWID challenge is not retried");

            SubscriptionTransport.TestVpnAvailable=()=>Task.FromResult(false);
            string baseLink=Convert.ToBase64String(Encoding.UTF8.GetBytes("vless://00000000-0000-0000-0000-000000000001@base.example:443?security=tls#Base"));
            var jsonIds=new List<string>();
            SubscriptionTransport.TestSend=(_,request)=>{
                if(!request.Headers.Contains("x-hwid")) return Task.FromResult(Response("",HttpStatusCode.NotFound,"x-hwid-not-supported"));
                jsonIds.Add(request.Headers.GetValues("x-hwid").Single());
                return Task.FromResult(Response(request.RequestUri!.AbsolutePath.EndsWith("/json")?Native:baseLink,HttpStatusCode.OK,"x-hwid-active"));
            };
            var imported=await SubscriptionImporter.LoadAsync(Url("native"));
            Check(imported.Nodes.Count==3 && imported.DetectedFormat=="Xray JSON" && jsonIds.Distinct().Count()==1,
                "Importer negotiates /json with one stable device and retains all transport families");
            var variants=new List<ProxyNode>{nodes[1],new(){Name="Trojan alternate",Values=new(nodes[1].Values),
                NativeXrayConfig=nodes[1].NativeXrayConfig}};
            Check(SubscriptionProfileStore.Merge(new[]{(new SubscriptionProfile{Name="Native profiles"},(IReadOnlyList<ProxyNode>)variants)}).Count==2,
                "Native subscription profile aliases remain selectable instead of disappearing during merge");
            SubscriptionTransport.TestSend=(_,request)=>{
                if(!request.Headers.Contains("x-hwid")) return Task.FromResult(Response("",HttpStatusCode.NotFound,"x-hwid-not-supported"));
                return Task.FromResult(request.RequestUri!.AbsolutePath.EndsWith("/json")?Response("",HttpStatusCode.NotFound):Response(baseLink,HttpStatusCode.OK,"x-hwid-active"));
            };
            Check((await SubscriptionImporter.LoadAsync(Url("fallback"))).Nodes.Count==1,
                "A provider without /json retains its usable base subscription");
            SubscriptionTransport.TestSend=null;
            string placeholder="vless://00000000-0000-0000-0000-000000000000@0.0.0.0:1?security=none#App%20not%20supported";
            SubscriptionTransport.TestFetch=(_,_)=>Task.FromResult(Convert.ToBase64String(Encoding.UTF8.GetBytes(placeholder)));
            rejected=false;
            try{await SubscriptionImporter.LoadAsync(Url("placeholder"));}
            catch(SubscriptionAccessException e){rejected=e.Failure==SubscriptionAccessFailure.UnsupportedClient;}
            Check(rejected,"App not supported placeholders are reported instead of imported as VPN servers");
            var fake=SubscriptionReader.ParseContent(placeholder).Nodes;
            Check(!SubscriptionCache.Save(Url("fake-cache"),fake),"Provider placeholders are never written to the working server cache");
        }
        finally {SubscriptionTransport.TestFetch=null;SubscriptionTransport.TestSend=null;SubscriptionTransport.TestVpnAvailable=null;}

        string? fixture=Environment.GetEnvironmentVariable("PROSTOKVN_PROVIDER_FIXTURE");
        if(!string.IsNullOrWhiteSpace(fixture))
        {
            byte[] plain=ProtectedData.Unprotect(File.ReadAllBytes(fixture),null,DataProtectionScope.CurrentUser);
            try
            {
                string body=Encoding.UTF8.GetString(plain);using var json=JsonDocument.Parse(body);
                var actual=JsonSubscription.ParseContent(body);
                Check(actual.Count==json.RootElement.GetArrayLength(),"Real provider JSON imports one server for every supplied profile");
                Check(actual.All(n=>EngineSelector.Choose(n)==TunnelEngine.Xray),"Every real provider profile selects native Xray");
                foreach(var node in actual) await CheckCore(node,true);
                foreach(var representative in actual.GroupBy(n=>n.Values["type"]?.ToString()).Select(g=>g.First()))
                    await CheckRouter(actual,representative);
                if(Environment.GetEnvironmentVariable("PROSTOKVN_PROVIDER_EGRESS_TEST")=="1")
                    foreach(var family in actual.GroupBy(n=>n.Values["type"]?.ToString()))
                        await CheckEgress(family.FirstOrDefault(n=>n.Name.Contains("Германия",StringComparison.OrdinalIgnoreCase))??family.First());
                Console.WriteLine("REAL_PROVIDER_CORE_VALIDATED="+actual.Count);
            }
            finally{CryptographicOperations.ZeroMemory(plain);}
        }
        string? liveUrl=Environment.GetEnvironmentVariable("PROSTOKVN_PROVIDER_LIVE_URL");
        if(!string.IsNullOrWhiteSpace(liveUrl))
        {
            SubscriptionTransport.TestVpnAvailable=()=>Task.FromResult(false);
            try
            {
                var live=await SubscriptionImporter.LoadAsync(liveUrl);
                Check(live.Nodes.Count==41 && live.Nodes.All(n=>EngineSelector.Choose(n)==TunnelEngine.Xray),"Actual subscription URL negotiates and imports all 41 native profiles");
                Check(SubscriptionProfileStore.Merge(new[]{(new SubscriptionProfile{Name="Provider"},(IReadOnlyList<ProxyNode>)live.Nodes)}).Count==41,
                    "All 41 provider profiles remain selectable after merging into the server list");
                Check(SubscriptionCache.Save(liveUrl,live.Nodes) && SubscriptionCache.TryLoad(liveUrl,out var cached,out _)
                    && cached.Count==live.Nodes.Count && cached.All(n=>EngineSelector.Choose(n)==TunnelEngine.Xray),
                    "Actual native profiles survive encrypted cache reload");
                Console.WriteLine("LIVE_PROVIDER_IMPORTED="+live.Nodes.Count);
            }
            finally{SubscriptionTransport.TestVpnAvailable=null;}
        }
        Console.WriteLine("ALL_PROVIDER_FORMAT_TESTS_PASS");
    }
}
