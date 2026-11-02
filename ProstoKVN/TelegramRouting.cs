using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;

namespace ProstoKVN;

public static class TelegramRouting
{
    // Telegram-published DC ranges (core.telegram.org/resources/cidr.txt).
    // The process rule also covers newly assigned IPs when Windows TUN can
    // identify the owning process; CIDRs remain essential for MTProto direct IP.
    public static readonly string[] IPv4 =
    {
        "91.105.192.0/23",
        "91.108.4.0/22",
        "91.108.8.0/22",
        "91.108.12.0/22",
        "91.108.16.0/22",
        "91.108.20.0/22",
        "91.108.56.0/22",
        "149.154.160.0/20",
        "185.76.151.0/24"
    };
    public static readonly string[] IPv6 =
    {
        "2001:67c:4e8::/48",
        "2001:b28:f23c::/48",
        "2001:b28:f23d::/48",
        "2001:b28:f23f::/48",
        "2a0a:f280::/32"
    };
    public static readonly string[] Domains =
    {
        "telegram.org", "telegram.me", "telegram.dog",
        "t.me", "telegra.ph", "telesco.pe"
    };
    private static readonly IPNetwork[] Networks = IPv4.Concat(IPv6)
        .Select(IPNetwork.Parse).ToArray();
    public static bool IsTelegramAddress(string? address)
        => IPAddress.TryParse(address, out var ip)
           && Networks.Any(cidr => cidr.Contains(ip));
    public static bool IsTelegramProcess(string? name)
        => (name ?? "").EndsWith("Telegram.exe", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "Telegram", StringComparison.OrdinalIgnoreCase);
    public static IReadOnlyList<string> BuildRules()
    {
        var rules = new List<string>
        {
            "PROCESS-NAME,Telegram.exe,VEGA",
            "PROCESS-NAME,Telegram,VEGA"
        };
        foreach (string domain in Domains)
            rules.Add("DOMAIN-SUFFIX," + domain + ",VEGA");
        foreach (string cidr in IPv4)
            rules.Add("IP-CIDR," + cidr + ",VEGA,no-resolve");
        foreach (string cidr in IPv6)
            rules.Add("IP-CIDR6," + cidr + ",VEGA,no-resolve");
        return rules;
    }
}
