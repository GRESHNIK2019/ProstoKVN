using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ProstoKVN;

public enum ProbeState { Reachable, Restricted, Unknown, Failed }

public sealed record RouteProbe(ProbeState State, int? HttpCode, string Reason)
{
    public bool Usable => State == ProbeState.Reachable;
    public override string ToString() => $"{State} {(HttpCode == null ? "" : "HTTP " + HttpCode)} ({Reason})";
}

public sealed record RouteComparison(RouteProbe Direct, RouteProbe Vpn);

public sealed class SmartRouteLearner : IDisposable
{
    private readonly bool persistRules;
    private readonly HashSet<string> active = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> checkedRecently = new(StringComparer.OrdinalIgnoreCase);
    private readonly HttpClient direct;
    private readonly HttpClient vpn;
    public SmartRouteLearner(bool persistRules = true)
        : this(Create(VpnCore.DirectProbePort), Create(VpnCore.VpnProbePort), persistRules) { }
    public SmartRouteLearner(HttpClient direct, HttpClient vpn, bool persistRules = false)
    {
        this.direct = direct;
        this.vpn = vpn;
        this.persistRules = persistRules;
    }
    public void ResetExitCountries() { lock (active) checkedRecently.Clear(); }
    public bool CanInspect(string domain)
    {
        if (!VpnCore.IsDomainSafe(domain) || RuDomainPolicy.IsRuDomain(domain)
            || AiServiceRouting.IsOpenAiDomain(domain)
            || DiscordRouting.IsDiscordDomain(domain)) return false;
        lock (active) return !active.Contains(domain)
            && (!checkedRecently.TryGetValue(domain, out var next) || DateTime.UtcNow >= next);
    }
    public event Action<string>? Log;
    public event Action<RouteRule>? Learned;

    private static HttpClient Create(int port)
    {
        var handler = new HttpClientHandler
        {
            Proxy = new WebProxy($"http://127.0.0.1:{port}"),
            UseProxy = true,
            AllowAutoRedirect = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        };
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(9) };
    }

    private static bool ContainsAny(string content, params string[] markers) =>
        markers.Any(marker => content.Contains(marker, StringComparison.OrdinalIgnoreCase));

    public static async Task<RouteProbe> CheckAsync(HttpClient client, string domain)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            var url = new Uri($"https://{domain}/");
            for (int hop = 0; hop <= 4; hop++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");
                request.Headers.Accept.ParseAdd("text/html,application/xhtml+xml,*/*;q=0.8");
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
                int code = (int)response.StatusCode;
                if (code is 301 or 302 or 303 or 307 or 308)
                {
                    var location = response.Headers.Location;
                    if (hop == 4 || location == null
                        || !Uri.TryCreate(url, location, out var next)
                        || next.Scheme != Uri.UriSchemeHttps || !VpnCore.IsDomainSafe(next.Host)
                        || next.UserInfo.Length > 0)
                        return new(ProbeState.Unknown, code, "перенаправление не удалось проверить безопасно");
                    url = next;
                    continue;
                }
                if (code is 429 or >= 500)
                    return new(ProbeState.Unknown, code, "ограничение запросов или сбой сервиса");
                string body = "";
                var type = response.Content.Headers.ContentType?.MediaType ?? "";
                if (type.Contains("html", StringComparison.OrdinalIgnoreCase))
                {
                    await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
                    byte[] buffer = new byte[32768];
                    int read = 0;
                    while (read < buffer.Length)
                    {
                        int count = await stream.ReadAsync(buffer.AsMemory(read), deadline.Token);
                        if (count == 0) break;
                        read += count;
                    }
                    body = Encoding.UTF8.GetString(buffer, 0, read);
                }
                // A bot challenge says nothing about browser reachability or censorship.
                if (ContainsAny(body, "cf-chl-", "challenge-platform", "Just a moment...",
                    "Attention Required!", "verify you are human", "captcha"))
                    return new(ProbeState.Unknown, code, "защита от ботов; блокировка не подтверждена");
                if (code == 451 || ContainsAny(body, "not available in your country",
                    "not available in your region", "access from your location is not allowed",
                    "доступ ограничен на основании", "доступ к информационному ресурсу ограничен"))
                    return new(ProbeState.Restricted, code, "признаки ограничения доступа");
                if (code == 403)
                    return new(ProbeState.Restricted, code, "HTTP-отказ; причина определяется сравнением маршрутов");
                if (code is < 200 or >= 300)
                    return new(ProbeState.Unknown, code, "ответ конечной точки не доказывает доступность сайта");
                if (AiServiceRouting.IsOpenAiDomain(domain) && type.Contains("html", StringComparison.OrdinalIgnoreCase))
                    return new(ProbeState.Unknown, code, "HTML получен; работа сервиса в браузере не подтверждена");
                return new(ProbeState.Reachable, code, "конечный HTTPS-ответ получен; вход и потоковое видео не проверены");
            }
            return new(ProbeState.Unknown, null, "слишком много перенаправлений");
        }
        catch (OperationCanceledException) { return new(ProbeState.Failed, null, "тайм-аут"); }
        catch (HttpRequestException) { return new(ProbeState.Failed, null, "ошибка сети или TLS"); }
        catch (Exception) { return new(ProbeState.Unknown, null, "ошибка диагностики"); }
    }

    public async Task<RouteComparison> ProbeDetailsAsync(string domain)
    {
        if (!VpnCore.IsDomainSafe(domain)) throw new ArgumentException("Некорректный домен.");
        var a = CheckAsync(direct, domain);
        var b = CheckAsync(vpn, domain);
        await Task.WhenAll(a, b);
        return new RouteComparison(a.Result, b.Result);
    }

    public async Task<(bool Direct, bool Vpn)> ProbeAsync(string domain)
    {
        var result = await ProbeDetailsAsync(domain);
        return (result.Direct.Usable, result.Vpn.Usable);
    }

    private static bool Matches(string domain, string suffix) =>
        domain.Equals(suffix, StringComparison.OrdinalIgnoreCase)
        || domain.EndsWith("." + suffix, StringComparison.OrdinalIgnoreCase);

    private async Task<(string Name, RouteProbe Result)> FindWorkingNodeAsync(
        string domain, IReadOnlyList<ProxyNode> nodes, string selectedNode)
    {
        int attempts = 0;
        for (int i = 0; i < Math.Min(nodes.Count, 32); i++)
        {
            var candidate = nodes[i];
            if (candidate.Name == selectedNode) continue;
            // The sidecar is bound to the selected Reality profile; don't assume others work.
            if (candidate.Values.ContainsKey("reality-opts")) continue;
            if (++attempts > 3) break; // Avoid blocking the learner for a full minute.
            using var probeClient = Create(VpnCore.NodeProbePortBase + i);
            var first = await CheckAsync(probeClient, domain);
            if (!first.Usable) continue;
            await Task.Delay(450);
            var second = await CheckAsync(probeClient, domain);
            if (second.Usable) return (candidate.Name, second);
        }
        return ("", new RouteProbe(ProbeState.Unknown, null, "альтернативный рабочий сервер не найден"));
    }

    private void NotifyUpdate(List<RouteRule> rules, RouteRule rule, string message)
    {
        rule.UpdatedUtc = DateTime.UtcNow;
        if (persistRules) UserStore.SaveRules(rules);
        Learned?.Invoke(rule);
        Log?.Invoke(message);
    }

    public async Task InspectAsync(
        string domain, List<RouteRule> rules, bool autoLearn,
        IReadOnlyList<ProxyNode>? nodes = null, string selectedNode = "", bool force = false)
    {
        domain = domain.Trim().TrimEnd('.').ToLowerInvariant();
        if (!VpnCore.IsDomainSafe(domain)) return;
        var manual = rules.Where(x => x.Manual && Matches(domain, x.Domain))
            .OrderByDescending(x => x.Domain.Length).FirstOrDefault();
        // All explicit user choices are authoritative. A browser-confirmed VPN
        // route must not be second-guessed by automated HTTP anti-bot probes.
        if (manual != null)
        {
            lock (active) checkedRecently[domain] = DateTime.UtcNow.AddMinutes(5);
            return;
        }
        // These services must route before login/streaming begins. HTTP probes
        // frequently get bot challenges on both exits and cannot improve them.
        if (AiServiceRouting.IsOpenAiDomain(domain))
        {
            if (force) Log?.Invoke($"Для {domain} уже предусмотрен сервисный маршрут через сервер. Это не гарантирует принятие IP сервисом.");
            return;
        }
        // HTTPS probing cannot verify Discord's UDP-only WebRTC media path.
        // A successful web page must not teach the client to bypass the VPN.
        if (DiscordRouting.IsDiscordDomain(domain))
        {
            if (force) Log?.Invoke($"Discord использует отдельный маршрут для звонков и демонстрации экрана. HTTPS-проверка не определяет качество UDP.");
            return;
        }
        // Claude can work directly while a foreign VPN receives HTTP 403.
        // Keep it eligible for real direct/VPN probing instead of forcing VPN.
        // Russian websites default to DIRECT. Automated HTTP 403 checks against
        // VK/CDN hosts often trigger anti-bot challenges on BOTH routes.
        // Never auto-create a VPN exception for .ru; user overrides remain valid.
        if (RuDomainPolicy.IsRuDomain(domain))
        {
            if (force) Log?.Invoke($"Автообучение пропущено для {domain}: .ru использует DIRECT. Для отдельного сайта можно добавить ручное правило.");
            return;
        }
        lock (active)
        {
            if (active.Contains(domain)) return;
            if (!force && checkedRecently.TryGetValue(domain, out var last)
                && DateTime.UtcNow < last) return;
            active.Add(domain);
            checkedRecently[domain] = DateTime.UtcNow.AddSeconds(30);
        }
        try
        {
            var first = await ProbeDetailsAsync(domain);
            Log?.Invoke($"Проверка {domain}: напрямую {first.Direct}; через сервер {first.Vpn}");
            if (!autoLearn) return;

            var learned = rules.FirstOrDefault(x => !x.Manual && x.Domain.Equals(domain, StringComparison.OrdinalIgnoreCase));
            if (first.Direct.Usable)
            {
                checkedRecently[domain] = DateTime.UtcNow.AddMinutes(5);
                var inheritedVpn = rules.FirstOrDefault(r => !r.Manual && r.Route == "VEGA"
                    && !r.Domain.Equals(domain, StringComparison.OrdinalIgnoreCase) && Matches(domain, r.Domain));
                if (learned?.Route == "VEGA" || inheritedVpn != null)
                {
                    await Task.Delay(450);
                    var confirm = await ProbeDetailsAsync(domain);
                    if (confirm.Direct.Usable)
                    {
                        if (inheritedVpn != null)
                        {
                            if (learned == null)
                            {
                                learned = new RouteRule { Domain = domain };
                                rules.Add(learned);
                            }
                            learned.Route = "DIRECT";
                            learned.PreferredNode = "";
                            learned.Provisional = false;
                            learned.ExpiresUtc = null;
                            NotifyUpdate(rules, learned, $"АВТООБУЧЕНИЕ: {domain} → DIRECT (две успешные проверки, исключение из общего VPN-правила).");
                        }
                        else
                        {
                            rules.Remove(learned!);
                            if (persistRules) UserStore.SaveRules(rules);
                            Learned?.Invoke(learned!);
                            Log?.Invoke($"Прямой маршрут восстановился для {domain}: автоматическое правило удалено.");
                        }
                    }
                }
                return;
            }
            if (first.Direct.State == ProbeState.Unknown) return;
            // No automatic trial when BOTH routes are inconclusive or denied.
            // A different country alone is not evidence that the VPN helps.
            string targetNode = "";
            if (!first.Vpn.Usable)
            {
                if (nodes == null || nodes.Count == 0) return;
                var alternative = await FindWorkingNodeAsync(domain, nodes, selectedNode);
                if (string.IsNullOrEmpty(alternative.Name))
                {
                    Log?.Invoke($"У {domain} нет подтверждённого рабочего маршрута. Правило не создаю.");
                    return;
                }
                targetNode = alternative.Name;
            }
            await Task.Delay(450);
            var retry = await ProbeDetailsAsync(domain);
            if (retry.Direct.Usable || retry.Direct.State == ProbeState.Unknown) return;
            if (string.IsNullOrEmpty(targetNode) && !retry.Vpn.Usable) return;
            checkedRecently[domain] = DateTime.UtcNow.AddMinutes(5);
            bool created = learned == null;
            if (created)
            {
                learned = new RouteRule { Domain = domain, Route = "VEGA", Manual = false };
                rules.Add(learned);
            }
            if (created || learned!.Route != "VEGA" || learned.PreferredNode != targetNode || learned.Provisional)
            {
                learned!.Route = "VEGA";
                learned.PreferredNode = targetNode;
                learned.Provisional = false;
                learned.ExpiresUtc = null;
                NotifyUpdate(rules, learned, $"АВТООБУЧЕНИЕ: {domain} → СЕРВЕР{(string.IsNullOrEmpty(targetNode) ? "" : " / " + targetNode)}");
            }
        }
        finally { lock (active) active.Remove(domain); }
    }

    public void Dispose() { direct.Dispose(); vpn.Dispose(); }
}