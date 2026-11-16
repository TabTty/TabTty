using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace TabTty.Core;

public readonly record struct SshDestination(string Kind, string Host, long? ScopeId, int Port)
{
    public string NetworkHost => ScopeId is { } scope ? $"{Host}%{scope.ToString(CultureInfo.InvariantCulture)}" : Host;
    public override string ToString() => Kind == "ipv6" ? $"[{NetworkHost}]:{Port}" : $"{Host}:{Port}";

    public static SshDestination Parse(string host, int port)
    {
        if (port is < 1 or > 65535 || host.Length is < 1 or > 253 ||
            host.Any(character => char.IsWhiteSpace(character) || char.IsControl(character) ||
                character is '/' or '\\' or '@' or '#' or '?'))
            throw new InvalidDataException("Invalid SSH destination.");

        bool bracketed = host[0] == '[' && host[^1] == ']';
        if (bracketed || host.Contains(':'))
        {
            string literal = bracketed ? host[1..^1] : host;
            int delimiter = literal.IndexOf('%');
            long? scope = null;
            if (delimiter >= 0)
            {
                if (!uint.TryParse(literal.AsSpan(delimiter + 1), NumberStyles.None, CultureInfo.InvariantCulture, out uint parsed))
                    throw new InvalidDataException("Invalid IPv6 scope ID.");
                scope = parsed;
                literal = literal[..delimiter];
            }
            if (!IPAddress.TryParse(literal, out IPAddress? address) || address.AddressFamily != AddressFamily.InterNetworkV6)
                throw new InvalidDataException("Invalid SSH IPv6 address.");
            return new SshDestination("ipv6", address.ToString(), scope, port);
        }

        if (IPAddress.TryParse(host, out IPAddress? ipv4))
        {
            if (ipv4.AddressFamily != AddressFamily.InterNetwork || ipv4.ToString() != host)
                throw new InvalidDataException("Use four decimal octets for an SSH IPv4 address.");
            return new SshDestination("ipv4", host, null, port);
        }
        if (host.Any(character => character is '[' or ']' or '%') ||
            host.All(character => char.IsAsciiDigit(character) || character == '.'))
            throw new InvalidDataException("Invalid SSH host.");

        string dns = ToAscii(host).ToLowerInvariant();
        string name = dns.EndsWith('.') ? dns[..^1] : dns;
        if (name.Length > 253 || name.Split('.').Any(label => label.Length is 0 or > 63 ||
            label.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-')))
            throw new InvalidDataException("Invalid SSH DNS name.");
        if (IPAddress.TryParse(name, out _))
            throw new InvalidDataException("Use an explicit address literal for an SSH IP destination.");
        return new SshDestination("dns", dns, null, port);
    }

    private static unsafe string ToAscii(string host)
    {
        const uint std3 = 2;
        fixed (char* input = host)
        {
            Span<char> buffer = stackalloc char[254];
            fixed (char* output = buffer)
            {
                int length = IdnToAscii(std3, input, host.Length, output, buffer.Length);
                if (length <= 0) throw new InvalidDataException("Invalid internationalized SSH host.");
                return new string(buffer[..length]);
            }
        }
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("Normaliz.dll", ExactSpelling = true)]
    private static extern unsafe int IdnToAscii(uint flags, char* source, int length, char* output, int capacity);
}
