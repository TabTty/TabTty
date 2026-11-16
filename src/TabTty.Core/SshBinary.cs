using System.Buffers;
using System.Buffers.Binary;
using System.Text;

namespace TabTty.Core;

internal ref struct SshReader(ReadOnlySpan<byte> bytes)
{
    private ReadOnlySpan<byte> remaining = bytes;
    public int Length => remaining.Length;
    public byte Byte() => Take(1)[0];
    public bool Boolean() => Byte() != 0;
    public uint Number() => BinaryPrimitives.ReadUInt32BigEndian(Take(4));
    public ReadOnlySpan<byte> String()
    {
        uint length = Number();
        if (length > remaining.Length) throw new InvalidDataException("Truncated SSH string.");
        return Take((int)length);
    }
    public ReadOnlySpan<byte> Take(int count)
    {
        if ((uint)count > (uint)remaining.Length) throw new InvalidDataException("Truncated SSH message.");
        ReadOnlySpan<byte> result = remaining[..count];
        remaining = remaining[count..];
        return result;
    }
    public void End()
    {
        if (!remaining.IsEmpty) throw new InvalidDataException("Unexpected SSH message fields.");
    }
    public void Expect(byte type)
    {
        if (Byte() != type) throw new InvalidDataException($"Expected SSH message {type}.");
    }
}

internal sealed class SshWriter(int capacity = 256) : IDisposable
{
    private readonly ArrayBufferWriter<byte> buffer = new(capacity);
    public ReadOnlySpan<byte> Span => buffer.WrittenSpan;
    public ReadOnlyMemory<byte> Memory => buffer.WrittenMemory;
    public void Dispose() => buffer.Clear();
    public void Clear() => buffer.Clear();
    public void Byte(byte value)
    {
        buffer.GetSpan(1)[0] = value;
        buffer.Advance(1);
    }
    public void Number(uint value)
    {
        BinaryPrimitives.WriteUInt32BigEndian(buffer.GetSpan(4), value);
        buffer.Advance(4);
    }
    public void String(ReadOnlySpan<byte> value)
    {
        Number((uint)value.Length);
        Raw(value);
    }
    public void Text(string value)
    {
        int length = Encoding.UTF8.GetByteCount(value);
        Number((uint)length);
        buffer.Advance(Encoding.UTF8.GetBytes(value, buffer.GetSpan(length)));
    }
    public void Raw(ReadOnlySpan<byte> value)
    {
        value.CopyTo(buffer.GetSpan(value.Length));
        buffer.Advance(value.Length);
    }
    public void PositiveMpint(ReadOnlySpan<byte> bigEndian)
    {
        while (!bigEndian.IsEmpty && bigEndian[0] == 0) bigEndian = bigEndian[1..];
        bool signByte = !bigEndian.IsEmpty && (bigEndian[0] & 0x80) != 0;
        Number((uint)bigEndian.Length + (signByte ? 1u : 0u));
        if (signByte) Byte(0);
        Raw(bigEndian);
    }
}
