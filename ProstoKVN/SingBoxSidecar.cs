using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Tasks;

namespace ProstoKVN;

public sealed class SingBoxSidecar : IDisposable
{
    public const int SocksPort = 16891;
    private Process? process;
    private readonly string configPath = Path.Combine(UserStore.Home, "singbox-sidecar.json");
    public event Action<string>? OnLog;
    public bool IsRunning => process is { HasExited: false };

    public static string Executable
    {
        get
        {
            string local = Path.Combine(AppContext.BaseDirectory, "core", "sing-box.exe");
            if (File.Exists(local)) return local;
            return Path.Combine("D:\\VEGACRMP_DEV\\tools\\vega_vpn_client", "core", "sing-box.exe");
        }
    }

    private static string V(ProxyNode node, string key)
        => node.Values.TryGetValue(key, out var value) ? Convert.ToString(value) ?? "" : "";

    public static Dictionary<string, object?> BuildConfig(ProxyNode node, int port = SocksPort)
    {
        string type = V(node, "type").ToLowerInvariant();
        string host = V(node, "server");
        if (!int.TryParse(V(node, "port"), out int serverPort) ||
            serverPort <= 0 || serverPort > 65535 || string.IsNullOrWhiteSpace(host))
            throw new InvalidOperationException("В узле отсутствует корректный адрес и порт.");

        var outbound = new Dictionary<string, object?>
        {
            ["type"] = type == "ss" ? "shadowsocks" : type,
            ["tag"] = "VEGA-EGRESS",
            ["server"] = host,
            ["server_port"] = serverPort
        };

        if (type == "hysteria2")
        {
            outbound["password"] = V(node, "password");
            var tls = new Dictionary<string, object?> { ["enabled"] = true };
            string sni = V(node, "sni");
            if (!string.IsNullOrEmpty(sni)) tls["server_name"] = sni;
            if (node.Values.TryGetValue("skip-cert-verify", out var insecure) && insecure is bool b)
                tls["insecure"] = b;
            if (node.Values.TryGetValue("alpn", out var alpn) && alpn is IEnumerable seq && alpn is not string)
                tls["alpn"] = seq.Cast<object>().Select(x => Convert.ToString(x) ?? "").Where(x => x.Length > 0).ToArray();
            outbound["tls"] = tls;
            if (node.Values.TryGetValue("obfs", out var obfs) && Convert.ToString(obfs) is string obf && !string.IsNullOrWhiteSpace(obf))
                outbound["obfs"] = new Dictionary<string, object?> { ["type"] = obf, ["password"] = V(node, "obfs-password") };
        }
        else if (type == "vless")
        {
            if (V(node, "network") != "ws" || !node.Values.ContainsKey("tls"))
                throw new NotSupportedException("sing-box sidecar: для VLESS сейчас поддерживается WebSocket + TLS.");
            outbound["uuid"] = V(node, "uuid");
            string flow = V(node, "flow");
            if (!string.IsNullOrWhiteSpace(flow)) outbound["flow"] = flow;
            string sni = V(node, "servername");
            var tls = new Dictionary<string, object?> { ["enabled"] = true };
            if (!string.IsNullOrEmpty(sni)) tls["server_name"] = sni;
            outbound["tls"] = tls;
            var transport = new Dictionary<string, object?> { ["type"] = "ws" };
            if (node.Values.TryGetValue("ws-opts", out var raw) && raw is Dictionary<string, object?> ws)
            {
                if (ws.TryGetValue("path", out var path) && path is string p)
                    transport["path"] = p;
                if (ws.TryGetValue("headers", out var headers))
                    transport["headers"] = headers;
            }
            outbound["transport"] = transport;
        }
        else if (type == "ss")
        {
            outbound["method"] = V(node, "cipher");
            outbound["password"] = V(node, "password");
        }
        else if (type == "trojan")
        {
            outbound["password"] = V(node, "password");
            var tls = new Dictionary<string, object?> { ["enabled"] = true };
            string sni = V(node, "sni");
            if (!string.IsNullOrWhiteSpace(sni)) tls["server_name"] = sni;
            outbound["tls"] = tls;
            if (V(node, "network") == "ws" && node.Values.TryGetValue("ws-opts", out var raw) &&
                raw is Dictionary<string, object?> ws)
            {
                var transport = new Dictionary<string, object?> { ["type"] = "ws" };
                if (ws.TryGetValue("path", out var path)) transport["path"] = path;
                if (ws.TryGetValue("headers", out var headers)) transport["headers"] = headers;
                outbound["transport"] = transport;
            }
        }
        else throw new NotSupportedException("sing-box не выбран для этого протокола.");

        return new Dictionary<string, object?>
        {
            ["log"] = new Dictionary<string, object?> { ["level"] = "warn" },
            ["inbounds"] = new object[] {
                new Dictionary<string, object?> {
                    ["type"] = "socks", ["tag"] = "VEGA-LOCAL",
                    ["listen"] = "127.0.0.1", ["listen_port"] = port
                }
            },
            ["outbounds"] = new object[] { outbound },
            ["route"] = new Dictionary<string, object?> {
                ["final"] = "VEGA-EGRESS",
                ["auto_detect_interface"] = true
            }
        };
    }

    public async Task StartAsync(ProxyNode node)
    {
        Stop();
        if (!File.Exists(Executable))
            throw new FileNotFoundException("Не найден sing-box.exe.", Executable);
        UserStore.EnsureHome();
        File.WriteAllText(configPath, JsonSerializer.Serialize(BuildConfig(node),
            new JsonSerializerOptions { WriteIndented = true }));
        using (var check = Process.Start(new ProcessStartInfo(Executable)
        {
            Arguments = $"check -c \"{configPath}\"",
            CreateNoWindow = true, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true
        }) ?? throw new InvalidOperationException("Не удалось проверить sing-box."))
        {
            string stderr = await check.StandardError.ReadToEndAsync();
            string stdout = await check.StandardOutput.ReadToEndAsync();
            await check.WaitForExitAsync();
            if (check.ExitCode != 0)
            {
                string error = (stderr + stdout);
                throw new InvalidOperationException("sing-box отклонил конфигурацию: "
                    + error[..Math.Min(500, error.Length)]);
            }
        }
        process = new Process { StartInfo = new ProcessStartInfo(Executable)
        {
            Arguments = $"run -c \"{configPath}\"",
            CreateNoWindow = true, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true
        }, EnableRaisingEvents = true };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) OnLog?.Invoke("sing-box: " + e.Data); };
        process.OutputDataReceived += (_, e) => { if (e.Data != null) OnLog?.Invoke("sing-box: " + e.Data); };
        process.Start();
        process.BeginErrorReadLine();
        process.BeginOutputReadLine();
        await Task.Delay(750);
        if (process.HasExited) throw new InvalidOperationException("sing-box завершился после запуска.");
        using var socket = new TcpClient();
        using var timeout = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(3));
        await socket.ConnectAsync(IPAddress.Loopback, SocksPort, timeout.Token);
        OnLog?.Invoke("sing-box запущен для " + V(node, "type") + ". SOCKS слушает только 127.0.0.1.");
    }

    public void Stop()
    {
        try { if (process is { HasExited: false }) { process.Kill(entireProcessTree: true); process.WaitForExit(2000); } }
        catch { }
        finally
        {
            process?.Dispose();
            process = null;
            try { if (File.Exists(configPath)) File.Delete(configPath); } catch { }
        }
    }
    public void Dispose() => Stop();
}
