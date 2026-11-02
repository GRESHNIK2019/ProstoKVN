using System;
using System.Globalization;
using System.Linq;
using System.Net;

namespace ProstoKVN;

public enum RouteTargetKind { Domain, IPv4, IPv6, IPv4Subnet, IPv6Subnet }

/// <summary>Validation, normalization and matching for a user-created route target.</summary>
public readonly record struct RouteTarget(string Value, RouteTargetKind Kind, int PrefixBits)
{
    public bool IsDomain => Kind == RouteTargetKind.Domain;
    public bool IsAddress => Kind is RouteTargetKind.IPv4 or RouteTargetKind.IPv6;
    public bool IsSubnet => Kind is RouteTargetKind.IPv4Subnet or RouteTargetKind.IPv6Subnet;
    public string DisplayKind => Kind switch
    {
        RouteTargetKind.Domain => "Домен",
        RouteTargetKind.IPv4 or RouteTargetKind.IPv6 => "IP",
        _ => "Подсеть"
    };
    public string MihomoType => Kind switch
    {
        RouteTargetKind.Domain => "DOMAIN-SUFFIX",
        RouteTargetKind.IPv4 or RouteTargetKind.IPv4Subnet => "IP-CIDR",
        _ => "IP-CIDR6"
    };
    public string MihomoValue => IsDomain ? Value : IsAddress ? Value + "/" + PrefixBits : Value;

    public string ToRule(string proxy)
        => IsDomain ? $"DOMAIN-SUFFIX,{Value},{proxy}" : $"{MihomoType},{MihomoValue},{proxy},no-resolve";

    public bool Matches(string? host, string? ip)
    {
        if (IsDomain)
        {
            string h = (host ?? "").TrimEnd('.');
            return h.Equals(Value, StringComparison.OrdinalIgnoreCase)
                || h.EndsWith("." + Value, StringComparison.OrdinalIgnoreCase);
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

    public static bool TryParse(string? input, out RouteTarget target)
    {
        target = default;
        string value = (input ?? "").Trim();
        if (value.Length == 0 || value.Length > 253 || value.Contains('%'))
            return false;
        if (value.StartsWith("*.", StringComparison.Ordinal))
            value = value[2..];
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
            target = new RouteTarget(ascii, RouteTargetKind.Domain, 0);
            return true;
        }
        catch (ArgumentException) { return false; }
    }
}