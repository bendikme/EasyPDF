using System.Text;

namespace EasyPDF.Prc;

/// <summary>A PRC file that cannot be read: damaged, or using something this reader does not know.</summary>
internal sealed class PrcFormatException(string message) : Exception(message);

/// <summary>
/// Reads the bit stream of a decompressed PRC section (ISO 14739-1, "Compressed Types"): bits most
/// significant first within each byte, and the format's own variable-length integers, doubles,
/// strings and Huffman-coded arrays on top of them. Ported from prc-rs (MIT, Kristóf Ralovich),
/// which was checked against a large set of real files.
/// </summary>
internal sealed class PrcBitReader(byte[] data)
{
    private readonly long _end = (long)data.Length * 8;
    private long _bit;

    public long Position => _bit;

    public long Remaining => _end - _bit;

    public bool Bit()
    {
        if (_bit >= _end) throw new PrcFormatException("PRC section ends early");
        var b = (data[_bit >> 3] >> (7 - (int)(_bit & 7))) & 1;
        _bit++;
        return b != 0;
    }

    /// <summary>An unsigned value of <paramref name="count"/> bits, the first read the most significant.</summary>
    public uint Bits(int count)
    {
        uint v = 0;
        for (var i = 0; i < count; i++) v = (v << 1) | (Bit() ? 1u : 0u);
        return v;
    }

    public void Skip(long bits)
    {
        if (bits < 0 || bits > Remaining) throw new PrcFormatException("PRC section ends early");
        _bit += bits;
    }

    public bool Boolean() => Bit();

    public byte Byte() => (byte)Bits(8);

    public sbyte Character() => (sbyte)Bits(8);

    public ushort UnsignedShort()
    {
        var lo = Byte();
        var hi = Byte();
        return (ushort)(hi << 8 | lo);
    }

    /// <summary>Up to four bytes, least significant first, each announced by a 1 bit; a 0 bit ends it.</summary>
    public uint UnsignedInteger()
    {
        uint v = 0;
        var i = 0;
        while (Bit())
        {
            if (i == 4) throw new PrcFormatException("PRC integer longer than 32 bits");
            v |= (uint)Byte() << (8 * i++);
        }
        return v;
    }

    /// <summary>As <see cref="UnsignedInteger"/>, sign-extended from the bytes present.</summary>
    public int Integer()
    {
        uint v = 0;
        var i = 0;
        while (Bit())
        {
            if (i == 4) throw new PrcFormatException("PRC integer longer than 32 bits");
            v |= (uint)Byte() << (8 * i++);
        }
        if (i is > 0 and < 4)
        {
            var shift = (4 - i) * 8;
            return (int)(v << shift) >> shift;
        }
        return (int)v;
    }

    public double Double() => PrcDoubles.Read(this);

    /// <summary>A null or a byte string, read as UTF-8 (the text of PRC files written today).</summary>
    public string String()
    {
        if (!Bit()) return "";
        var length = UnsignedInteger();
        if (length > Remaining / 8) throw new PrcFormatException("PRC string longer than its section");
        var bytes = new byte[length];
        for (var i = 0; i < bytes.Length; i++) bytes[i] = Byte();
        return Encoding.UTF8.GetString(bytes);
    }

    /// <summary>Four bytes of an IEEE single, least significant first.</summary>
    public float FloatAsBytes()
    {
        Span<byte> b = stackalloc byte[4];
        for (var i = 0; i < 4; i++) b[i] = Byte();
        return BitConverter.ToSingle(b);
    }

    /// <summary>Data of an application's own: a bit count, then the bits, which are skipped.</summary>
    public void UserData() => Skip(UnsignedInteger());

    public uint UnsignedIntegerWithVariableBitNumber(int bits) => Bits(bits);

    /// <summary>A sign bit, then the magnitude in <paramref name="bits"/> - 1 bits.</summary>
    public int IntegerWithVariableBitNumber(int bits)
    {
        if (bits <= 0) return 0;
        var negative = Bit();
        var magnitude = bits == 1 ? 0 : (int)Bits(bits - 1);
        return negative ? -magnitude : magnitude;
    }

    public uint NumberOfBitsThenUnsignedInteger() => Bits((int)Bits(5));

    public bool[] BoolArray(uint count)
    {
        if (count > Remaining) throw new PrcFormatException("PRC array longer than its section");
        var a = new bool[count];
        for (var i = 0; i < a.Length; i++) a[i] = Bit();
        return a;
    }

    /// <summary>
    /// An array of values of <paramref name="bitsPerElement"/> bits: stored plainly (a byte each), or
    /// Huffman-coded. <paramref name="hasCompressedBit"/> false means the choice is not stored and is
    /// <paramref name="compressed"/>.
    /// </summary>
    public int[] CharacterArray(int bitsPerElement, bool signExtend = false, bool hasCompressedBit = true, bool compressed = true)
        => ElementArray(bitsPerElement, signExtend, hasCompressedBit, compressed, wide: false);

    /// <summary>As <see cref="CharacterArray"/>, with 16-bit elements stored plainly as shorts.</summary>
    public int[] ShortArray(int bitsPerElement, bool signExtend = true)
        => ElementArray(bitsPerElement, signExtend, true, true, wide: true);

    /// <summary>Integers each stored in only as many bits as it needs; the bit counts are an array of their own.</summary>
    public int[] CompressedIntegerArray()
    {
        var bitCounts = CharacterArray(6);
        var a = new int[bitCounts.Length];
        for (var i = 0; i < a.Length; i++) a[i] = IntegerWithVariableBitNumber(bitCounts[i] & 0x3F);
        return a;
    }

    /// <summary>
    /// Indices stored as differences from the one before, each in a bit count that is itself stored
    /// as a difference from the one before.
    /// </summary>
    public int[] CompressedIndiceArray(bool hasCompressedBit = true, bool compressed = true)
    {
        var bitCountSteps = CharacterArray(6, signExtend: true, hasCompressedBit, compressed);
        var a = new int[bitCountSteps.Length];
        var bits = 0;
        for (var i = 0; i < a.Length; i++)
        {
            bits += bitCountSteps[i];
            var v = IntegerWithVariableBitNumber(bits);
            a[i] = i == 0 ? v : v + a[i - 1];
        }
        return a;
    }

    private int[] ElementArray(int bitsPerElement, bool signExtend, bool hasCompressedBit, bool compressed, bool wide)
    {
        if (hasCompressedBit) compressed = Bit();
        if (!compressed)
        {
            var count = UnsignedInteger();
            if (count > Remaining / (wide ? 16 : 8)) throw new PrcFormatException("PRC array longer than its section");
            var plain = new int[count];
            for (var i = 0; i < plain.Length; i++) plain[i] = wide ? (short)UnsignedShort() : (sbyte)Byte();
            return plain;
        }

        var words = UnsignedInteger();
        if (words == 0) return [];
        if (words > Remaining / 32) throw new PrcFormatException("PRC array longer than its section");
        var bytes = new byte[words * 4];
        for (var i = 0; i < bytes.Length; i++) bytes[i] = Byte();
        if (UnsignedInteger() > 32) throw new PrcFormatException("PRC Huffman array damaged");
        return PrcHuffman.Decode(bytes, bitsPerElement, signExtend);
    }
}
