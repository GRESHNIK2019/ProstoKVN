using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;

namespace ProstoKVN;

public enum ServerLatencyMethod { Tcp, Icmp }
public sealed record ServerLatencyResult(ServerLatencyMethod Method, int? Milliseconds,
    DateTime MeasuredUtc, string Detail);

/// <summary>Endpoint RTT only. Never starts a tunnel or changes proxy/route settings.</summary>
public sealed class ServerLatencyProbe
{
    private static readonly HttpClient DnsClient = CreateDnsClient();
    private readonly SemaphoreSlim concurrency = new(6,6);
    private readonly ConcurrentDictionary<string,(DateTime Expires,IPAddress[] Values)> addresses = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<string,CancellationToken,Task<IPAddress[]>> resolver;
    private readonly Func<IPAddress,int,CancellationToken,Task<int?>> tcp;
    private readonly Func<IPAddress,CancellationToken,Task<int?>> icmp;

    public ServerLatencyProbe(Func<string,CancellationToken,Task<IPAddress[]>>? resolve = null,
        Func<IPAddress,int,CancellationToken,Task<int?>>? connect = null,
        Func<IPAddress,CancellationToken,Task<int?>>? echo = null)
    {
        resolver=resolve??ResolveAsync;tcp=connect??TcpAsync;icmp=echo??IcmpAsync;
    }
    public static ServerLatencyMethod MethodFor(ProxyNode node)
        => node.Values.TryGetValue("type",out var type) && Convert.ToString(type)?.ToLowerInvariant() is "hysteria2" or "hy2" or "tuic"
            ? ServerLatencyMethod.Icmp : ServerLatencyMethod.Tcp;
    public async Task<ServerLatencyResult> MeasureAsync(ProxyNode node,CancellationToken cancellation=default)
    {
        var method=MethodFor(node);
        await concurrency.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            string host=node.Values.TryGetValue("server",out var server)?Convert.ToString(server)??"":"";
            if(string.IsNullOrWhiteSpace(host))return Result(method,null,"Нет адреса сервера.");
            if(!node.Values.TryGetValue("port",out var rawPort) || !int.TryParse(Convert.ToString(rawPort),out int port) || port is <1 or >65535)
                return Result(method,null,"Нет корректного порта сервера.");
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeout.CancelAfter(TimeSpan.FromSeconds(7));
            IPAddress[] resolved;
            if(addresses.TryGetValue(host,out var cached) && cached.Expires>DateTime.UtcNow)resolved=cached.Values;
            else
            {
                resolved=await resolver(host,timeout.Token).WaitAsync(timeout.Token).ConfigureAwait(false);
                if(resolved.Length>0 && resolved.All(a=>!IsFakeIp(a)))
                    addresses[host]=(DateTime.UtcNow.AddSeconds(30),resolved);
            }
            var candidates=resolved.Where(a=>!IsFakeIp(a)).OrderBy(a=>a.AddressFamily==AddressFamily.InterNetwork?0:1).Take(2).ToArray();
            if(candidates.Length==0)return Result(method,null,"Не удалось получить настоящий IP сервера; адрес TUN не измеряется.");
            foreach(var address in candidates)
            {
                timeout.Token.ThrowIfCancellationRequested();
                int? milliseconds=method==ServerLatencyMethod.Tcp
                    ? await tcp(address,port,timeout.Token).ConfigureAwait(false)
                    : await icmp(address,timeout.Token).ConfigureAwait(false);
                if(milliseconds.HasValue)return Result(method,milliseconds,
                    method==ServerLatencyMethod.Tcp?"Время TCP-подключения к порту сервера по текущему сетевому маршруту."
                        :"ICMP-отклик адреса сервера. Доступность UDP и работа VPN проверяются отдельно.");
            }
            return Result(method,null,method==ServerLatencyMethod.Icmp
                ?"Нет ICMP-ответа. Сервер может запрещать пинг; это не означает, что VPN недоступен."
                :"Порт сервера не ответил за время проверки. Это проверка отклика, а не авторизации VPN.");
        }
        catch(OperationCanceledException) when(cancellation.IsCancellationRequested){throw;}
        catch(Exception e) when(e is SocketException or HttpRequestException or PingException or OperationCanceledException or JsonException)
        {return Result(method,null,"Не удалось измерить отклик в текущей сети. Проверьте позже.");}
        finally{concurrency.Release();}
    }
    private static ServerLatencyResult Result(ServerLatencyMethod method,int? milliseconds,string detail)
        =>new(method,milliseconds,DateTime.UtcNow,detail);
    public static bool IsFakeIp(IPAddress address)
    {
        if(address.IsIPv4MappedToIPv6)address=address.MapToIPv4();
        var bytes=address.GetAddressBytes();
        return bytes.Length==4 && bytes[0]==198 && bytes[1] is 18 or 19;
    }
    private static async Task<int?> TcpAsync(IPAddress address,int port,CancellationToken cancellation)
    {
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        using var socket=new TcpClient(address.AddressFamily);
        var watch=Stopwatch.StartNew();
        try{await socket.ConnectAsync(address,port,timeout.Token).ConfigureAwait(false);return Math.Max(1,(int)watch.ElapsedMilliseconds);}
        catch(SocketException){return null;}
        catch(OperationCanceledException) when(!cancellation.IsCancellationRequested){return null;}
    }
    private static async Task<int?> IcmpAsync(IPAddress address,CancellationToken cancellation)
    {
        using var ping=new Ping();
        var reply=await ping.SendPingAsync(address,TimeSpan.FromSeconds(2),cancellationToken:cancellation).ConfigureAwait(false);
        return reply.Status==IPStatus.Success?Math.Max(1,(int)reply.RoundtripTime):null;
    }
    private static HttpClient CreateDnsClient()
    {
        var handler=new SocketsHttpHandler{UseProxy=false,AllowAutoRedirect=false,UseCookies=false,
            ConnectCallback=async(_,cancellation)=>
            {
                var socket=new Socket(AddressFamily.InterNetwork,SocketType.Stream,ProtocolType.Tcp);
                try{await socket.ConnectAsync(IPAddress.Parse("8.8.8.8"),443,cancellation).ConfigureAwait(false);return new NetworkStream(socket,true);}
                catch{socket.Dispose();throw;}
            }};
        return new HttpClient(handler){Timeout=TimeSpan.FromSeconds(4)};
    }
    private static async Task<IPAddress[]> ResolveAsync(string host,CancellationToken cancellation)
    {
        if(IPAddress.TryParse(host,out var literal))return new[]{literal};
        IPAddress[] local=Array.Empty<IPAddress>();
        try{local=await Dns.GetHostAddressesAsync(host,cancellation).WaitAsync(TimeSpan.FromSeconds(2),cancellation).ConfigureAwait(false);}
        catch(Exception e) when(e is SocketException or TimeoutException){}
        if(local.Length>0 && local.All(a=>!IsFakeIp(a)))return local;
        using var request=new HttpRequestMessage(HttpMethod.Get,"https://dns.google/resolve?name="+Uri.EscapeDataString(host)+"&type=A&edns_client_subnet=0.0.0.0/0");
        request.Headers.TryAddWithoutValidation("Accept","application/dns-json");
        using var response=await DnsClient.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,cancellation).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if(response.Content.Headers.ContentLength>65536)throw new JsonException("DNS response too large");
        using var stream=await response.Content.ReadAsStreamAsync(cancellation).ConfigureAwait(false);
        using var limited=new MemoryStream();var buffer=new byte[4096];int count;
        while((count=await stream.ReadAsync(buffer,cancellation).ConfigureAwait(false))>0)
        {if(limited.Length+count>65536)throw new JsonException("DNS response too large");limited.Write(buffer,0,count);}
        using var json=JsonDocument.Parse(limited.ToArray());
        if(!json.RootElement.TryGetProperty("Answer",out var answers))return Array.Empty<IPAddress>();
        return answers.EnumerateArray().Where(a=>a.TryGetProperty("type",out var type)&&type.GetInt32()==1)
            .Select(a=>a.TryGetProperty("data",out var data)&&IPAddress.TryParse(data.GetString(),out var ip)?ip:null)
            .OfType<IPAddress>().Where(a=>!IsFakeIp(a)).Distinct().ToArray();
    }
}
