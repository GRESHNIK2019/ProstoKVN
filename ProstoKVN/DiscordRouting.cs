using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ProstoKVN;

/// <summary>
/// Discord voice and screen share use WebRTC (often UDP to an IP address).
/// DNS-only split routing misses those flows in TUN smart mode.
/// Only Discord's own desktop processes are routed by process; other apps
/// keep their normal direct/VPN rules. Explicit manual exceptions take priority.
/// </summary>
public static class DiscordRouting
{
    public static readonly string[] Processes =
    {
        "Discord.exe", "DiscordPTB.exe", "DiscordCanary.exe", "DiscordDevelopment.exe"
    };

    public static readonly string[] Domains =
    {
        "discord.com", "discord.gg", "discordapp.com",
        "discordapp.net", "discord.media", "discordcdn.com"
    };

    public static bool IsDiscordProcess(string? process)
    {
        if (string.IsNullOrWhiteSpace(process)) return false;
        var fileName = Path.GetFileName(process.Replace('/', '\\'));
        return Processes.Contains(fileName, StringComparer.OrdinalIgnoreCase);
    }

    public static bool IsDiscordDomain(string? domain)
    {
        if (string.IsNullOrWhiteSpace(domain)) return false;
        var host = domain.Trim().TrimEnd('.');
        return Domains.Any(suffix => host.Equals(suffix, StringComparison.OrdinalIgnoreCase)
            || host.EndsWith("." + suffix, StringComparison.OrdinalIgnoreCase));
    }

    public static IReadOnlyList<string> BuildRules()
    {
        var rules = new List<string>();
        foreach (var process in Processes)
            rules.Add("PROCESS-NAME," + process + ",VEGA");
        foreach (var domain in Domains)
            rules.Add("DOMAIN-SUFFIX," + domain + ",VEGA");
        return rules;
    }
}
