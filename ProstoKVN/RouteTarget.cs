using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;

namespace ProstoKVN;

public enum RouteTargetKind { Domain, DomainExact, IPv4, IPv6, IPv4Subnet, IPv6Subnet, ProcessName, ProcessPath }

/// <summary>Validation, normalization and matching for a user-created route target.</summary>
public readonly record struct RouteTarget(string Value, RouteTargetKind Kind, int PrefixBits)
{
    public bool IsDomain => Kind is RouteTargetKind.Domain or RouteTargetKind.DomainExact;
    public bool IsAddress => Kind is RouteTargetKind.IPv4 or RouteTargetKind.IPv6;
    public bool IsSubnet => Kind is RouteTargetKind.IPv4Subnet or RouteTargetKind.IPv6Subnet;
    public bool IsApplication => Kind is RouteTargetKind.ProcessName or RouteTargetKind.ProcessPath;
    // Prefixes keep existing routes.json entries backward compatible and avoid
    // interpreting an executable name such as chrome.exe as a domain.
    public string DisplayValue => IsApplication ? Value[(Value.IndexOf(':') + 1)..]
        : Kind == RouteTargetKind.DomainExact ? Value["domain-exact:".Length..] : Value;
    public int Priority => Kind == RouteTargetKind.ProcessPath ? 5000
        : Kind == RouteTargetKind.ProcessName ? 4000
        : IsAddress ? 3000 + PrefixBits : IsSubnet ? 2000 + PrefixBits
        : Kind == RouteTargetKind.DomainExact ? 1500 + DisplayValue.Length : 1000 + Value.Length;
    public string DisplayKind => Kind switch
    {
        RouteTargetKind.Domain => "Домен и поддомены",
        RouteTargetKind.DomainExact => "Точный домен",
        RouteTargetKind.ProcessName or RouteTargetKind.ProcessPath => "Приложение",
        RouteTargetKind.IPv4 or RouteTargetKind.IPv6 => "IP",
        _ => "Подсеть"
    };
    public string MihomoType => Kind switch
    {
        RouteTargetKind.Domain => "DOMAIN-SUFFIX",
        RouteTargetKind.DomainExact => "DOMAIN",
        RouteTargetKind.ProcessName => "PROCESS-NAME",
        RouteTargetKind.ProcessPath => "PROCESS-PATH",
        RouteTargetKind.IPv4 or RouteTargetKind.IPv4Subnet => "IP-CIDR",
        _ => "IP-CIDR6"
    };
    public string MihomoValue => IsApplication || Kind == RouteTargetKind.DomainExact
        ? DisplayValue : IsAddress ? Value + "/" + PrefixBits : Value;

    public string ToRule(string proxy)
        => $"{MihomoType},{MihomoValue},{proxy}" + (IsDomain || IsApplication ? "" : ",no-resolve");

    public bool Matches(string? host, string? ip, string? processName = null, string? processPath = null)
    {
        if (IsApplication)
        {
            string owner = !string.IsNullOrWhiteSpace(processPath) ? processPath : processName ?? "";
            return DisplayValue.Equals(Kind == RouteTargetKind.ProcessName
                ? Path.GetFileName(owner.Replace('/', '\\')) : owner.Replace('/', '\\'),
                StringComparison.OrdinalIgnoreCase);
        }
        if (IsDomain)
        {
            string h = (host ?? "").TrimEnd('.');
            return h.Equals(DisplayValue, StringComparison.OrdinalIgnoreCase)
                || (Kind == RouteTargetKind.Domain
                    && h.EndsWith("." + DisplayValue, StringComparison.OrdinalIgnoreCase));
        }

        string addressText = !string.IsNullOrWhiteSpace(ip) ? ip : host ?? "";
        if (!IPAddress.TryParse(addressText, out var address) ||
            !IPAddress.TryParse(Value.Split('/')[0], out var networkAddress))
            return false;
        byte[] addressBytes = address.GetAddressBytes();
        byte[] networkBytes = networkAddress.GetAddressBytes();
        if (addressBytes.Length != networkBytes.Length) return false;
        int bits = PrefixBits;
        for (int i = 0; i < addressBytes.Length && bits > 0; i++)
        {
            int count = Math.Min(bits, 8);
            byte mask = (byte)(0xff << (8 - count));
            if ((addressBytes[i] & mask) != (networkBytes[i] & mask))
                return false;
            bits -= count;
        }
        return true;
    }

    // The editor accepts a pasted URL, but the persisted route remains a host.
    public static bool TryParseUserInput(string? input, out RouteTarget target)
    {
        string value = (input ?? "").Trim();
        if ((value.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            && Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && string.IsNullOrEmpty(uri.UserInfo)) value = uri.IdnHost.Trim('[', ']');
        return TryParse(value, out target);
    }

    public static bool TryParse(string? input, out RouteTarget target)
    {
        target = default;
        string value = (input ?? "").Trim();
        if (value.StartsWith("process-name:", StringComparison.OrdinalIgnoreCase))
            return TryParseApplication(value[13..], out target, nameOnly: true);
        if (value.StartsWith("process-path:", StringComparison.OrdinalIgnoreCase))
            return TryParseApplication(value[13..], out target, nameOnly: false);
        // A special persisted prefix distinguishes exact-host matches from
        // legacy suffix rules; unchanged routes.json entries keep old semantics.
        bool exactDomain = value.StartsWith("domain-exact:", StringComparison.OrdinalIgnoreCase);
        if (exactDomain) value = value["domain-exact:".Length..];
        if (value.Length == 0 || value.Length > 253 || value.Contains('%') || exactDomain && (value.Contains('/') || IPAddress.TryParse(value, out _)))
            return false;
        if (value.StartsWith("*.", StringComparison.Ordinal))
        {
            if (exactDomain) return false;
            value = value[2..];
        }
        value = value.TrimEnd('.');

        int slash = value.IndexOf('/');
        if (slash >= 0)
        {
            if (slash == 0 || slash != value.LastIndexOf('/')) return false;
            string prefixText = value[(slash + 1)..];
            if (prefixText.Length == 0 || !prefixText.All(char.IsAsciiDigit)
                || !int.TryParse(prefixText, NumberStyles.None, CultureInfo.InvariantCulture, out int prefix))
                return false;
            if (!IPAddress.TryParse(value[..slash], out var address))
                return false;
            byte[] bytes = address.GetAddressBytes();
            if (prefix > bytes.Length * 8) return false;

            // Canonicalize host bits: 192.168.1.77/24 -> 192.168.1.0/24.
            int bits = prefix;
            for (int i = 0; i < bytes.Length; i++)
            {
                int count = Math.Clamp(bits, 0, 8);
                bytes[i] &= (byte)(0xff << (8 - count));
                bits -= 8;
            }
            string normalized = new IPAddress(bytes) + "/" + prefix;
            target = new RouteTarget(normalized, bytes.Length == 4
                ? RouteTargetKind.IPv4Subnet : RouteTargetKind.IPv6Subnet, prefix);
            return true;
        }

        if (IPAddress.TryParse(value, out var ip))
        {
            int bits = ip.GetAddressBytes().Length * 8;
            target = new RouteTarget(ip.ToString(), bits == 32 ? RouteTargetKind.IPv4 : RouteTargetKind.IPv6, bits);
            return true;
        }

        // Don't mistake mistyped numeric IP addresses for domain names.
        if (value.All(x => char.IsAsciiDigit(x) || x == '.'))
            return false;

        try
        {
            string ascii = new IdnMapping().GetAscii(value).ToLowerInvariant();
            if (!VpnCore.IsDomainSafe(ascii)) return false;
            target = new RouteTarget(exactDomain ? "domain-exact:" + ascii : ascii,
                exactDomain ? RouteTargetKind.DomainExact : RouteTargetKind.Domain, 0);
            return true;
        }
        catch (ArgumentException) { return false; }
    }

    public static bool TryParseApplication(string? input, out RouteTarget target, bool? nameOnly = null)
    {
        target = default;
        string value = (input ?? "").Trim();
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"') value = value[1..^1];
        // Commas delimit Mihomo rule fields. Wildcards belong to separate rule
        // types and must never silently change the meaning of an exact match.
        if (value.Length == 0 || value.Length > 32760 || value.Any(char.IsControl)
            || value.IndexOfAny(new[] { ',', '*', '?', '"', '<', '>', '|' }) >= 0
            || !value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return false;
        value = value.Replace('/', '\\');
        bool path = value.Contains('\\') || value.Contains(':');
        if (nameOnly == true && path || nameOnly == false && !path) return false;
        if (path)
        {
            if (!Path.IsPathFullyQualified(value)) return false;
            try { value = Path.GetFullPath(value); }
            catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
            if (value.IndexOf(':', 2) >= 0) return false;
        }
        else if (value.TrimEnd(' ', '.') != value || value == ".exe") return false;
        target = new RouteTarget((path ? "process-path:" : "process-name:") + value,
            path ? RouteTargetKind.ProcessPath : RouteTargetKind.ProcessName, 0);
        return true;
    }
}
