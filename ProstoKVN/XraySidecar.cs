using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace ProstoKVN;

public sealed class XraySidecar : IDisposable
{
    public const int SocksPort = 16890;
    private Process? process;
    private readonly string tempConfig = Path.Combine(UserStore.Home, "xray-sidecar.json");
    public event Action<string>? OnLog;
    public bool IsRunning => process is { HasExited: false };
    public static string Executable
    {
        get
        {
            var local = Path.Combine(AppContext.BaseDirectory, "core", "xray.exe");
            if (File.Exists(local)) return local;
            return Path.Combine("D:\\VEGACRMP_DEV\\tools\\vega_vpn_client", "core", "xray.exe");
        }
    }
    private static bool IsProxyOutbound(JsonObject outbound) =>
        outbound["protocol"]?.ToString() is string protocol
        && protocol.Length > 0 && protocol is not ("freedom" or "blackhole" or "dns");

    public static JsonObject BuildConfig(string jsonConfig)
    {
        var config = JsonNode.Parse(jsonConfig)?.AsObject() ?? throw new FormatException("Неверная Xray-конфигурация.");
        config["log"] = new JsonObject { ["loglevel"] = "warning" };
        config["inbounds"] = new JsonArray(new JsonObject
        {
            ["tag"] = "vega-socks-in", ["listen"] = "127.0.0.1", ["port"] = SocksPort, ["protocol"] = "socks",
            ["settings"] = new JsonObject { ["auth"] = "noauth", ["udp"] = true }
        });
        var outbounds = config["outbounds"] as JsonArray
            ?? throw new FormatException("В Xray-конфигурации нет выходов.");
        var routing = config["routing"] as JsonObject ?? new JsonObject();
        var route = new JsonObject
        {
            ["type"] = "field",
            ["inboundTag"] = new JsonArray("vega-socks-in")
        };
        var balancers = routing["balancers"] as JsonArray;
        // Honor an original default route, including a subscription balancer.
        // Destination-specific rules are owned by Mihomo in the local client.
        JsonObject? originalDefault = (routing["rules"] as JsonArray)?
            .OfType<JsonObject>().FirstOrDefault(r => r.All(p =>
                p.Key is "type" or "network" or "inboundTag" or "outboundTag" or "balancerTag")
                && (r["network"] == null || r["network"]!.ToString() is "tcp,udp" or "udp,tcp"));
        string outboundTag = originalDefault?["outboundTag"]?.ToString() ?? "";
        string balancerTag = originalDefault?["balancerTag"]?.ToString() ?? "";
        if (balancerTag.Length > 0 && balancers != null
            && balancers.OfType<JsonObject>().Any(b => b["tag"]?.ToString() == balancerTag))
            route["balancerTag"] = balancerTag;
        else
        {
            var outbound = outbounds.OfType<JsonObject>().FirstOrDefault(o =>
                outboundTag.Length > 0 && o["tag"]?.ToString() == outboundTag);
            outbound ??= outbounds.OfType<JsonObject>().FirstOrDefault(o =>
                o["tag"]?.ToString() == "proxy" && IsProxyOutbound(o));
            outbound ??= outbounds.OfType<JsonObject>().FirstOrDefault(IsProxyOutbound);
            if (outbound == null)
                throw new FormatException("В Xray-конфигурации нет VPN-выхода.");
            string tag = outbound["tag"]?.ToString() ?? "";
            if (string.IsNullOrWhiteSpace(tag))
            {
                tag = "vega-egress";
                while (outbounds.OfType<JsonObject>().Any(o => o["tag"]?.ToString() == tag))
                    tag += "-1";
                outbound["tag"] = tag;
            }
            route["outboundTag"] = tag;
        }
        routing["rules"] = new JsonArray(route);
        routing["domainStrategy"] ??= "AsIs";
        if (config["routing"] == null) config["routing"] = routing;
        return config;
    }

    public async Task StartAsync(string jsonConfig)
    {
        Stop();
        if (!File.Exists(Executable)) throw new FileNotFoundException("Не найден Xray-core для VLESS Reality.", Executable);
        UserStore.EnsureHome();
        var config = BuildConfig(jsonConfig);
        File.WriteAllText(tempConfig, config.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        var verify = Process.Start(new ProcessStartInfo(Executable)
        {
            Arguments = $"run -test -config \"{tempConfig}\"",
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true
        }) ?? throw new InvalidOperationException("Не удалось проверить Xray.");
        string err = await verify.StandardError.ReadToEndAsync();
        string stdout = await verify.StandardOutput.ReadToEndAsync();
        await verify.WaitForExitAsync();
        if (verify.ExitCode != 0) throw new InvalidOperationException("Xray не принял конфигурацию. " + (err + stdout)[..Math.Min(450, (err + stdout).Length)]);
        process = new Process { StartInfo = new ProcessStartInfo(Executable)
        {
            Arguments = $"run -config \"{tempConfig}\"",
            CreateNoWindow = true, UseShellExecute = false,
            RedirectStandardError = true, RedirectStandardOutput = true
        }, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => { if (e.Data != null) OnLog?.Invoke("Xray: " + e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) OnLog?.Invoke("Xray: " + e.Data); };
        process.Start();
        process.BeginErrorReadLine();
        process.BeginOutputReadLine();
        await Task.Delay(900);
        if (process.HasExited) throw new InvalidOperationException("Xray завершился сразу после запуска.");
        using var tcp = new TcpClient();
        using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(3));
        await tcp.ConnectAsync(IPAddress.Loopback, SocksPort, cts.Token);
        OnLog?.Invoke("Xray-core активирован для совместимости Reality.");
    }
    public void Stop()
    {
        try { if (process is { HasExited: false }) { process.Kill(entireProcessTree: true); process.WaitForExit(3000); } }
        catch { }
        finally { process?.Dispose(); process = null; try { if (File.Exists(tempConfig)) File.Delete(tempConfig); } catch { } }
    }
    public void Dispose() => Stop();
}