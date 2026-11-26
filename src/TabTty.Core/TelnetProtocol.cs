using System.Text;

namespace TabTty.Core;

public sealed class TelnetProtocol
{
    private const byte Iac = 255, Dont = 254, Do = 253, Wont = 252, Will = 251, Sb = 250, Se = 240;
    private const byte Binary = 0, Echo = 1, SuppressGoAhead = 3, TerminalType = 24, WindowSize = 31;
    private readonly OptionState[] local = new OptionState[256];
    private readonly OptionState[] remote = new OptionState[256];
    private readonly byte[] terminalType;
    private readonly bool initiateBinary;
    private readonly List<byte> subnegotiation = [];
    private ParseState state;
    private byte command;
    private bool pendingCr;
    private bool started;
    private ushort columns;
    private ushort rows;

    public TelnetProtocol(string terminalType, ushort columns, ushort rows, bool initiateBinary = false)
    {
        if (terminalType.Length is 0 or > 64 || terminalType.Any(character => character is < '!' or > '~'))
            throw new ArgumentException("Invalid Telnet terminal type.", nameof(terminalType));
        this.terminalType = Encoding.ASCII.GetBytes(terminalType);
        this.columns = columns;
        this.rows = rows;
        this.initiateBinary = initiateBinary;
    }

    public bool RemoteEcho => remote[Echo] == OptionState.Enabled;

    public byte[] Start()
    {
        if (started) return [];
        started = true;
        List<byte> output = [];
        Offer(local, Will, TerminalType, output);
        Offer(local, Will, WindowSize, output);
        Offer(remote, Do, Echo, output);
        Offer(local, Will, SuppressGoAhead, output);
        Offer(remote, Do, SuppressGoAhead, output);
        if (initiateBinary)
        {
            Offer(local, Will, Binary, output);
            Offer(remote, Do, Binary, output);
        }

        return output.ToArray();
    }

    public (byte[] Payload, byte[] Reply) Receive(ReadOnlySpan<byte> bytes) => Receive(bytes, null);

    internal (byte[] Payload, byte[] Reply) Receive(ReadOnlySpan<byte> bytes,
        Action<byte[], bool>? echoBoundary)
    {
        List<byte> payload = [];
        List<byte> reply = [];
        foreach (byte value in bytes)
        {
            switch (state)
            {
                case ParseState.Data:
                    if (value == Iac) state = ParseState.Command;
                    else AppendData(value, payload);
                    break;
                case ParseState.Command:
                    ReadCommand(value, payload);
                    break;
                case ParseState.Option:
                    bool previousEcho = RemoteEcho;
                    Negotiate(value, payload, reply);
                    state = ParseState.Data;
                    if (echoBoundary is not null && previousEcho != RemoteEcho)
                    {
                        echoBoundary(payload.ToArray(), RemoteEcho);
                        payload.Clear();
                    }
                    break;
                case ParseState.Subnegotiation:
                    if (value == Iac) state = ParseState.SubnegotiationCommand;
                    else AppendSubnegotiation(value);
                    break;
                case ParseState.SubnegotiationCommand:
                    if (value == Iac)
                    {
                        AppendSubnegotiation(Iac);
                        state = ParseState.Subnegotiation;
                    }
                    else if (value == Se && subnegotiation.Count > 0)
                    {
                        RespondToSubnegotiation(reply);
                        subnegotiation.Clear();
                        state = ParseState.Data;
                    }
                    else
                    {
                        throw new InvalidDataException("Malformed Telnet subnegotiation.");
                    }
                    break;
            }
        }

        return (payload.ToArray(), reply.ToArray());
    }

    public byte[] CompleteReceive()
    {
        if (state != ParseState.Data) throw new InvalidDataException("Incomplete Telnet command.");
        if (!pendingCr) return [];
        pendingCr = false;
        return [13];
    }

    public byte[] EncodeInput(ReadOnlySpan<byte> input) => CaptureInputFraming().Encode(input);

    internal InputFraming CaptureInputFraming() => new(local[Binary] == OptionState.Enabled);

    internal readonly record struct InputFraming(bool Binary)
    {
        private int Width(ReadOnlySpan<byte> input, int index) => input[index] == Iac ||
            (!Binary && input[index] == 13 && (index + 1 == input.Length || input[index + 1] != 10)) ? 2 : 1;

        internal int PayloadPrefix(ReadOnlySpan<byte> input, int confirmedWireBytes)
        {
            int index = 0;
            while (index < input.Length && (confirmedWireBytes -= Width(input, index)) >= 0) index++;
            return index;
        }

        internal int WirePrefix(ReadOnlySpan<byte> input, int payloadBytes)
        {
            int length = 0;
            for (int index = 0; index < payloadBytes; index++) length += Width(input, index);
            return length;
        }

        internal byte[] Encode(ReadOnlySpan<byte> input, Span<int> boundaries = default, byte[]? reusable = null)
        {
            int length = WirePrefix(input, input.Length);
            if (reusable is not null && length == input.Length && boundaries.IsEmpty) return reusable;
            byte[] output = new byte[length];
            int position = 0, boundary = 0;
            for (int index = 0; index < input.Length; index++)
            {
                byte value = input[index];
                output[position++] = value;
                if (Width(input, index) == 2) output[position++] = value == Iac ? Iac : (byte)0;
                if (boundary < boundaries.Length && boundaries[boundary] == index + 1)
                    boundaries[boundary++] = position;
            }
            return output;
        }
    }

    public byte[] SetWindowSize(ushort width, ushort height)
    {
        if (columns == width && rows == height) return [];
        columns = width;
        rows = height;
        return local[WindowSize] == OptionState.Enabled ? WindowSizeMessage() : [];
    }

    public byte[] AreYouThere() => [Iac, 246];
    public byte[] Break() => [Iac, 243];

    private void ReadCommand(byte value, List<byte> payload)
    {
        state = ParseState.Data;
        switch (value)
        {
            case Iac:
                AppendData(Iac, payload);
                break;
            case Will or Wont or Do or Dont:
                command = value;
                state = ParseState.Option;
                break;
            case Sb:
                subnegotiation.Clear();
                state = ParseState.Subnegotiation;
                break;
            case >= 236 and <= 239 or >= 241 and <= 249:
                break;
            default:
                throw new InvalidDataException("Malformed Telnet command.");
        }
    }

    private void AppendData(byte value, List<byte> payload)
    {
        if (pendingCr)
        {
            payload.Add(13);
            pendingCr = false;
            if (value == 0) return;
        }

        if (value == 13 && remote[Binary] != OptionState.Enabled)
            pendingCr = true;
        else
            payload.Add(value);
    }

    private void AppendSubnegotiation(byte value)
    {
        if (subnegotiation.Count == 64 * 1024)
            throw new InvalidDataException("Telnet subnegotiation exceeds its size limit.");
        subnegotiation.Add(value);
    }

    private void Negotiate(byte option, List<byte> payload, List<byte> reply)
    {
        bool isLocal = command is Do or Dont;
        bool enable = command is Do or Will;
        var options = isLocal ? local : remote;
        var previous = options[option];
        bool allowed = option is Binary or SuppressGoAhead ||
            (isLocal ? option is TerminalType or WindowSize : option == Echo);
        byte positive = isLocal ? Will : Do;
        byte negative = isLocal ? Wont : Dont;

        // The only initiated requests occur at startup, so no reversal is queued.
        if (enable && previous == OptionState.Disabled)
        {
            if (allowed) options[option] = OptionState.Enabled;
            reply.AddRange([Iac, allowed ? positive : negative, option]);
        }
        else if (enable && previous == OptionState.Requested)
        {
            options[option] = OptionState.Enabled;
        }
        else if (!enable)
        {
            options[option] = OptionState.Disabled;
            if (previous == OptionState.Enabled) reply.AddRange([Iac, negative, option]);
        }

        if (options[option] != previous)
        {
            if (!isLocal && option == Binary && pendingCr)
            {
                payload.Add(13);
                pendingCr = false;
            }

            if (isLocal && option == WindowSize && options[option] == OptionState.Enabled)
                reply.AddRange(WindowSizeMessage());
        }
    }

    private void RespondToSubnegotiation(List<byte> reply)
    {
        if (local[TerminalType] == OptionState.Enabled && subnegotiation.Count == 2 &&
            subnegotiation[0] == TerminalType && subnegotiation[1] == 1)
        {
            reply.AddRange([Iac, Sb, TerminalType, 0]);
            foreach (byte value in terminalType) AppendEscaped(reply, value);
            reply.AddRange([Iac, Se]);
        }
    }

    private byte[] WindowSizeMessage()
    {
        List<byte> message = [Iac, Sb, WindowSize];
        AppendEscaped(message, (byte)(columns >> 8));
        AppendEscaped(message, (byte)columns);
        AppendEscaped(message, (byte)(rows >> 8));
        AppendEscaped(message, (byte)rows);
        message.AddRange([Iac, Se]);
        return message.ToArray();
    }

    private static void Offer(OptionState[] states, byte verb, byte option, List<byte> output)
    {
        if (states[option] != OptionState.Disabled) return;
        states[option] = OptionState.Requested;
        output.AddRange([Iac, verb, option]);
    }

    private static void AppendEscaped(List<byte> output, byte value)
    {
        output.Add(value);
        if (value == Iac) output.Add(value);
    }

    private enum ParseState { Data, Command, Option, Subnegotiation, SubnegotiationCommand }
    private enum OptionState { Disabled, Requested, Enabled }
}
