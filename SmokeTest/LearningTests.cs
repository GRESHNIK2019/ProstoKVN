using System.Net;
using System.Net.Http;
using System.Text;
using ProstoKVN;

public static class LearningTests
{
    sealed class Stub(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            => Task.FromResult(reply(request));
    }
    static HttpClient Client(Func<HttpRequestMessage, HttpResponseMessage> reply) => new(new Stub(reply));
    static HttpResponseMessage Reply(int status, string body = "<html>Welcome</html>") =>
        new((HttpStatusCode)status) { Content = new StringContent(body, Encoding.UTF8, "text/html") };
    static void Check(bool ok, string label)
    {
        if (!ok) throw new Exception("LEARNING_FAILED: " + label);
        Console.WriteLine("PASS " + label);
    }
    public static async Task RunAsync()
    {
        int hops = 0;
        using (var client = Client(req =>
        {
            hops++;
            if (req.RequestUri!.AbsolutePath == "/")
            {
                var redirect = Reply(302);
                redirect.Headers.Location = new Uri("/home", UriKind.Relative);
                return redirect;
            }
            return Reply(200);
        }))
            Check((await SmartRouteLearner.CheckAsync(client, "example.com")).Usable && hops == 2,
                "Redirect followed through the same route to final response");
        using (var client = Client(_ => { var r = Reply(302); r.Headers.Location = new Uri("/"); return r; }))
            Check((await SmartRouteLearner.CheckAsync(client, "example.com")).State == ProbeState.Unknown,
                "Redirect loop stays inconclusive");
        foreach (int code in new[] { 401, 404, 429, 503 })
        {
            using var client = Client(_ => Reply(code));
            Check((await SmartRouteLearner.CheckAsync(client, "example.com")).State == ProbeState.Unknown,
                "HTTP " + code + " is not censorship evidence");
        }
        using (var client = Client(_ => Reply(403, "<html>Just a moment... cf-chl-test</html>")))
            Check((await SmartRouteLearner.CheckAsync(client, "example.com")).State == ProbeState.Unknown,
                "Bot challenge is not classified as blocked");
        using (var client = Client(_ => Reply(200, "<html>Service not available in your country</html>")))
            Check((await SmartRouteLearner.CheckAsync(client, "example.com")).State == ProbeState.Restricted,
                "Geographic block page with HTTP 200 detected");
        using (var client = Client(_ => Reply(200, "<html>Documentation about Access denied errors</html>")))
            Check((await SmartRouteLearner.CheckAsync(client, "example.com")).Usable,
                "Generic words in ordinary content do not imply blocking");
        var rules = new List<RouteRule>();
        using (var learner = new SmartRouteLearner(Client(_ => Reply(403)), Client(_ => Reply(403))))
        {
            await learner.InspectAsync("example.com", rules, true, force: true);
            Check(rules.Count == 0, "Both routes denied: no speculative VPN route");
            Check(!learner.CanInspect("example.com"), "Recent inconclusive host does not monopolize queue");
            learner.ResetExitCountries();
            Check(learner.CanInspect("example.com"), "Reconnect clears observation cooldown");
        }
        int directRequests = 0, vpnRequests = 0;
        using (var learner = new SmartRouteLearner(
            Client(_ => { directRequests++; throw new HttpRequestException("reset"); }),
            Client(_ => { vpnRequests++; return Reply(200); })))
        {
            await learner.InspectAsync("example.com", rules, true, force: true);
            Check(directRequests == 2 && vpnRequests == 2 && rules.Single().Route == "VEGA",
                "Two independent comparisons required before VPN learning");
        }
        using (var learner = new SmartRouteLearner(Client(_ => Reply(200)), Client(_ => Reply(200))))
        {
            await learner.InspectAsync("example.com", rules, true, force: true);
            Check(rules.Count == 0, "Recovered DIRECT removes obsolete automatic VPN route");
        }
        rules.Add(new() { Domain = "example.com", Route = "VEGA" });
        using (var learner = new SmartRouteLearner(Client(_ => Reply(200)), Client(_ => Reply(200))))
        {
            await learner.InspectAsync("cdn.example.com", rules, true, force: true);
            Check(rules.Any(r => r.Domain == "cdn.example.com" && r.Route == "DIRECT"),
                "Working subdomain can escape inherited VPN route");
        }
        var node = new ProxyNode { Name = "TEST", Values = new() {
            ["name"] = "TEST", ["type"] = "socks5", ["server"] = "127.0.0.1", ["port"] = 18888 } };
        var config = VpnCore.BuildConfig(new[] { node }, rules, new AppSettings { Mode = "smart", SelectedNode = "TEST" });
        var lines = (List<string>)config["rules"]!;
        Check(lines.IndexOf("DOMAIN-SUFFIX,cdn.example.com,DIRECT")
            < lines.IndexOf("DOMAIN-SUFFIX,example.com,VEGA"), "DIRECT exception compiled ahead of broad VPN route");
        rules = new() { new() { Domain = "claude.ai", Route = "DIRECT" } };
        using (var learner = new SmartRouteLearner(Client(_ => Reply(403)), Client(_ => Reply(200))))
        {
            await learner.InspectAsync("api.claude.ai", rules, true);
            Check(rules.Any(r => r.Domain == "api.claude.ai" && r.Route == "VEGA"),
                "Service DIRECT default does not suppress subdomain learning");
        }
        rules = new() { new() { Domain = "example.com", Route = "DIRECT", Manual = true } };
        using (var learner = new SmartRouteLearner(Client(_ => throw new Exception("must not probe")), Client(_ => Reply(200))))
            await learner.InspectAsync("example.com", rules, true, force: true);
        Check(rules.Single().Manual && rules.Single().Route == "DIRECT", "Manual choices remain authoritative");
        rules = new() { new() { Domain = "a.googleapis.com", Route = "VEGA" }, new() { Domain = "b.googleapis.com", Route = "VEGA" } };
        Check(ServiceRuleGrouper.CompactStableRules(rules) == 0 && rules.Count == 2,
            "Two blocked endpoints cannot force all Google APIs through VPN");
        Console.WriteLine("ALL_LEARNING_TESTS_PASS");
    }
}
