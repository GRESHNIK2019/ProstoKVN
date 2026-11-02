using System;
using System.Collections.Generic;

namespace ProstoKVN;

/// <summary>
/// Russian .ru domains prefer DIRECT in Smart mode. HTTP 403 from a CDN,
/// anti-bot challenge or HEAD/GET probe is not evidence that a VPN helps.
/// Users can still set a manual VPN route for a specific .ru site.
/// </summary>
public static class RuDomainPolicy
{
    public static bool IsRuDomain(string? domain)
    {
        if (string.IsNullOrWhiteSpace(domain)) return false;
        string normalized = domain.Trim().TrimEnd('.');
        return normalized.EndsWith(".ru", StringComparison.OrdinalIgnoreCase);
    }

    public static int RemoveAutomaticRoutes(List<RouteRule> rules) =>
        rules.RemoveAll(rule => !rule.Manual && IsRuDomain(rule.Domain));
}
