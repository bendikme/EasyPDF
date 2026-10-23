namespace EasyPDF.Prc;

/// <summary>
/// The Huffman coding of PRC's character and short arrays (ISO 14739-1, "Huffman Algorithm"). The
/// coded block is read least significant bit first, unlike the rest of the file: the number of
/// leaves, the longest code's length, each leaf's symbol, code length and code, then the element
/// count and the codes. Ported from prc-rs (MIT, Kristóf Ralovich), including how it builds the
/// tree from the stored codes, which real writers' trees depend on.
/// </summary>
internal static class PrcHuffman
{
    public static int[] Decode(byte[] block, int bitsPerElement, bool signExtend)
    {
        var r = new LsbReader(block);
        var leafCount = (int)r.Read(bitsPerElement + 1);
        if (leafCount == 0) return [];
        var maxCodeLength = (int)r.Read(8);
        if (maxCodeLength is <= 0 or > 32) throw new PrcFormatException("PRC Huffman code length out of range");

        var leaves = new (int Symbol, int Length, uint Code)[leafCount];
        for (var i = 0; i < leaves.Length; i++)
        {
            var symbol = (int)r.Read(bitsPerElement);
            if (signExtend && (symbol & (1 << (bitsPerElement - 1))) != 0) symbol -= 1 << bitsPerElement;
            var length = (int)r.Read(maxCodeLength);
            if (length > 32) throw new PrcFormatException("PRC Huffman code length out of range");
            leaves[i] = (symbol, length, r.Read(length));
        }
        // Inserted in order of their stored code (a stable sort), each code read from its far end.
        var ordered = leaves.Select((leaf, i) => (leaf, i)).OrderBy(x => x.leaf.Code).ThenBy(x => x.i).Select(x => x.leaf);

        var tree = new Tree();
        foreach (var (symbol, length, code) in ordered)
            if (!tree.Insert(Reverse(code, length), length, symbol))
                throw new PrcFormatException("PRC Huffman tree damaged");

        var count = r.Read(32);
        if (count > r.Remaining) throw new PrcFormatException("PRC Huffman array longer than its block");
        var data = new int[count];
        for (var i = 0; i < data.Length; i++) data[i] = tree.Next(ref r);
        return data;
    }

    private static uint Reverse(uint value, int length)
    {
        uint v = 0;
        for (var k = 0; k < length; k++) v = (v << 1) | ((value >> k) & 1);
        return v;
    }

    /// <summary>The nodes in flat arrays: children, and the symbol of a node that is a leaf.</summary>
    private sealed class Tree
    {
        private readonly List<int> _left = [-1];
        private readonly List<int> _right = [-1];
        private readonly List<int> _symbol = [0];
        private readonly List<bool> _leaf = [false];

        public bool Insert(uint code, int length, int symbol)
        {
            var node = 0;
            for (var i = 0; i < length; i++)
            {
                var right = ((code >> i) & 1) != 0;
                var child = right ? _right[node] : _left[node];
                var last = i == length - 1;
                if (last && child >= 0) return false;
                if (child < 0)
                {
                    child = _left.Count;
                    _left.Add(-1);
                    _right.Add(-1);
                    _symbol.Add(last ? symbol : 0);
                    _leaf.Add(last);
                    if (right) _right[node] = child;
                    else _left[node] = child;
                }
                node = child;
            }
            return true;
        }

        public int Next(ref LsbReader r)
        {
            var node = 0;
            while (_left[node] >= 0 || _right[node] >= 0)
            {
                var child = r.Bit() ? _right[node] : _left[node];
                if (child < 0)
                {
                    // A leaf that also has children ends here when the branch taken is missing.
                    if (_leaf[node]) return _symbol[node];
                    throw new PrcFormatException("PRC Huffman code not in its tree");
                }
                node = child;
            }
            if (node == 0) throw new PrcFormatException("PRC Huffman tree empty");
            return _symbol[node];
        }
    }

    private struct LsbReader(byte[] data)
    {
        private readonly long _end = (long)data.Length * 8;
        private long _bit;

        public readonly long Remaining => _end - _bit;

        public bool Bit()
        {
            if (_bit >= _end) throw new PrcFormatException("PRC Huffman block ends early");
            var b = (data[_bit >> 3] >> (int)(_bit & 7)) & 1;
            _bit++;
            return b != 0;
        }

        /// <summary><paramref name="count"/> bits, the first read the least significant.</summary>
        public uint Read(int count)
        {
            uint v = 0;
            for (var i = 0; i < count; i++) if (Bit()) v |= 1u << i;
            return v;
        }
    }
}
