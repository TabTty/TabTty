using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using TabTty.Core;

internal static class SingleByteMappings
{
    internal static void Run(string referenceDirectory)
    {
        var excluded = new HashSet<string> { "utf-8", "cp932", "euc-jp", "jis", "cp949", "cp936", "cp950" };
        string[] identities = TerminalEncodings.Identifiers.Where(id => !excluded.Contains(id)).ToArray();
        if (identities.Length != 40) throw new InvalidOperationException("Expected the 40 accepted single-byte identities.");

        byte[] input = Enumerable.Range(0, 256).Select(value => (byte)value).ToArray();
        byte[] referenceBytes = new byte[256 * sizeof(ushort)];
        foreach (string id in identities)
        {
            ushort[] expected = ReadExpected(id, referenceDirectory, input);
            var actual = new List<Rune>(input.Length);
            using var decoder = new LegacyReceiver(new TerminalEncoding(id), sevenBitKana: false);
            decoder.Decode(input, flush: true, 1, (rune, interval) =>
            {
                if (interval != 1) throw new InvalidOperationException($"{id} lost its receive interval.");
                actual.Add(rune);
            }, () => false);
            if (decoder.HasIncompleteUnit || actual.Count != 256)
                throw new InvalidOperationException($"{id} did not decode one scalar per input byte.");
            foreach (byte value in input)
            {
                if (actual[value].Value != expected[value])
                    throw new InvalidOperationException($"{id} byte {value:X2} produced U+{actual[value].Value:X4}, expected U+{expected[value]:X4}.");
                BinaryPrimitives.WriteUInt16LittleEndian(referenceBytes.AsSpan(value * sizeof(ushort)), expected[value]);
            }
            Console.WriteLine($"{id} 256 positions {Convert.ToHexString(SHA256.HashData(referenceBytes))}");
            if (id == "cp864")
                Console.WriteLine($"cp864 Win32 low-byte vector 25 -> U+{actual[0x25].Value:X4}");
            if (id == "cp1258" && actual[0xEC].Value != 0x0301)
                throw new InvalidOperationException("cp1258 did not preserve its combining acute accent.");
        }

        var terminal = new TerminalOutput(20, 100, 4, receiveEncoding: "iso-8859-1");
        try
        {
            int bells = 0;
            terminal.Bell += () => bells++;
            terminal.Receive(TerminalReceiveSource.Data, [(byte)'A', 0, 0x9B, (byte)'2', (byte)'J', (byte)'B', 7]);
            if (terminal.GetRows()[0] != "A2JB" || bells != 1)
                throw new InvalidOperationException("Decoded C1 executed VT or embedded NUL prevented the C0 bell.");
        }
        finally { terminal.ReleaseNativeState(); }
    }

    private static ushort[] ReadExpected(string id, string directory, byte[] input)
    {
        var result = new ushort[input.Length];
        if (id is "iso-8859-10" or "iso-8859-11" or "iso-8859-14")
        {
            Array.Fill(result, (ushort)0xFFFD);
            foreach (string line in File.ReadLines(Path.Combine(directory, id[4..] + ".txt")))
            {
                var columns = line.Split('#', 2)[0].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (columns.Length == 2)
                    result[Convert.ToByte(columns[0], 16)] = Convert.ToUInt16(columns[1], 16);
            }
            return result;
        }

        uint codePage = id == "koi8-r" ? 20866 : id.StartsWith("cp", StringComparison.Ordinal)
            ? uint.Parse(id.AsSpan(2)) : 28590 + uint.Parse(id.AsSpan("iso-8859-".Length));
        if (!IsValidCodePage(codePage)) throw new InvalidOperationException($"Windows does not offer code page {codePage}.");
        foreach (byte value in input)
        {
            byte raw = value;
            result[value] = value < 0x20 || value == 0x7F ? value
                : MultiByteToWideChar(codePage, 8, ref raw, 1, out ushort converted, 1) == 1 ? converted : (ushort)0xFFFD;
        }
        return result;
    }

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsValidCodePage(uint codePage);

    [DllImport("kernel32.dll")]
    private static extern int MultiByteToWideChar(uint codePage, uint flags, ref byte input, int inputLength,
        out ushort output, int outputLength);
}
