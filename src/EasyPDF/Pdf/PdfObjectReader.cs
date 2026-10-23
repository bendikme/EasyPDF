using System.Buffers;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace EasyPDF.Pdf;

/// <summary>Random access to the bytes of a PDF: the file itself, or a copy of it in memory.</summary>
internal interface IByteSource
{
    long Length { get; }

    /// <summary>Reads up to <paramref name="buffer"/>'s length at <paramref name="position"/>; fewer only at the end, 0 on failure.</summary>
    int ReadAt(long position, Span<byte> buffer);
}

/// <summary>Bytes in native memory, such as a copy PDFium wrote. The memory must outlive the source.</summary>
internal sealed unsafe class MemorySource : IByteSource
{
    private readonly byte* _data;

    public MemorySource(byte* data, long length)
    {
        _data = data;
        Length = length;
    }

    public long Length { get; }

    public int ReadAt(long position, Span<byte> buffer)
    {
        if (position < 0 || position >= Length) return 0;
        var count = (int)Math.Min(buffer.Length, Length - position);
        new ReadOnlySpan<byte>(_data + position, count).CopyTo(buffer);
        return count;
    }
}

/// <summary>A PDF name, without its slash; #xx escapes undone.</summary>
internal sealed record PdfName(string Value);

/// <summary>A reference to an indirect object.</summary>
internal sealed record PdfRef(int Number);

/// <summary>A PDF string: bytes, read as text only when asked.</summary>
internal sealed class PdfString(byte[] bytes)
{
    public byte[] Bytes { get; } = bytes;

    /// <summary>A text string: UTF-16BE or UTF-8 when it starts with their mark, PDFDocEncoding otherwise.</summary>
    public string Text
    {
        get
        {
            var b = Bytes;
            if (b.Length >= 2 && b[0] == 0xFE && b[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(b, 2, (b.Length - 2) & ~1);
            if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF) return Encoding.UTF8.GetString(b, 3, b.Length - 3);
            var chars = new char[b.Length];
            for (var i = 0; i < b.Length; i++) chars[i] = PdfDoc(b[i]);
            return new string(chars);
        }
    }

    /// <summary>An address or a file name: as a text string when it is marked as one, UTF-8 when it is UTF-8, PDFDocEncoding otherwise.</summary>
    public string Lenient
    {
        get
        {
            var b = Bytes;
            if ((b.Length >= 2 && b[0] == 0xFE && b[1] == 0xFF) || (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF)) return Text;
            try
            {
                return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(b);
            }
            catch (DecoderFallbackException)
            {
                return Text;
            }
        }
    }

    private static char PdfDoc(byte c) => c switch
    {
        0x18 => '˘', 0x19 => 'ˇ', 0x1A => 'ˆ', 0x1B => '˙', 0x1C => '˝', 0x1D => '˛', 0x1E => '˚', 0x1F => '˜',
        0x80 => '•', 0x81 => '†', 0x82 => '‡', 0x83 => '…', 0x84 => '—', 0x85 => '–', 0x86 => 'ƒ', 0x87 => '⁄',
        0x88 => '‹', 0x89 => '›', 0x8A => '−', 0x8B => '‰', 0x8C => '„', 0x8D => '“', 0x8E => '”', 0x8F => '‘',
        0x90 => '’', 0x91 => '‚', 0x92 => '™', 0x93 => 'ﬁ', 0x94 => 'ﬂ', 0x95 => 'Ł', 0x96 => 'Œ', 0x97 => 'Š',
        0x98 => 'Ÿ', 0x99 => 'Ž', 0x9A => 'ı', 0x9B => 'ł', 0x9C => 'œ', 0x9D => 'š', 0x9E => 'ž', 0xA0 => '€',
        _ => (char)c,
    };
}

/// <summary>A stream object: its dictionary, and where its data starts in the source.</summary>
internal sealed record PdfStream(Dictionary<string, object?> Dict, long DataOffset);

/// <summary>
/// The model of a 3D annotation: its 3D stream's kind (PRC or U3D) and data, decoded, and the
/// camera-to-world matrix of the view it opens at (12 numbers, PDF 32000 13.6.4), if it names one.
/// </summary>
/// <summary>
/// A 3D annotation's model (PDF 32000 13.6): its decoded stream - PRC or U3D - its views, the
/// first shown at the start, and the shape of the annotation, its height over its width: a view's
/// field of view spans its width.
/// </summary>
internal sealed record Pdf3DModel(string Kind, byte[] Data, IReadOnlyList<Pdf3DView> Views, double Aspect = 1);

/// <summary>
/// A view of a 3D model: its name and where its camera is - twelve numbers, the camera's axes X
/// (right), Y (up) and Z (the way it looks) in world coordinates and then its position - and the
/// camera's field of view in degrees.
/// </summary>
internal sealed record Pdf3DView(string Name, double[] CameraToWorld, double FieldOfView);

/// <summary>
/// Reads a PDF's objects without PDFium, for what PDFium's API will not tell: how big a form is
/// before its form module is let loose on it, and what the document's buttons and named actions
/// do. It reads the file as PDFium does - cross-reference tables and streams, updates, hybrid
/// files, object streams, Flate with PNG predictors - but trusts nothing in it: every size is
/// capped, every loop is cut, each operation has a budget of objects it may load and bytes it
/// may inflate, anything that does not parse is simply missing (and not tried again), and a file
/// it cannot read whole (damaged, encrypted) is not read at all (<see cref="Open"/> returns null).
/// Values are dictionaries (Dictionary&lt;string, object?&gt;), arrays (List&lt;object?&gt;),
/// <see cref="PdfName"/>, <see cref="PdfString"/>, <see cref="PdfRef"/>, double, bool and null.
/// Objects are parsed on demand and not kept, apart from a few decompressed object streams.
/// One thread at a time.
/// </summary>
internal sealed class PdfObjectReader
{
    private const int MaxObjects = 8_000_000;
    private const int MaxSections = 512;
    private const int MaxDepth = 64;
    private const int MaxTreeDepth = 1024;
    private const int MaxFieldDepth = 32;
    private const int MaxPages = 1_000_000;
    private const int MaxPagePushes = 4 * MaxPages;
    private const int FirstWindow = 4096;
    private const int MaxWindow = 64 << 20;
    private const int MaxDecoded = 128 << 20;
    private const int MaxObjectStream = 32 << 20;
    private const long MaxEncoded = 64L << 20;
    private const int StreamCacheBytes = 4 << 20;
    private const int StreamCacheCount = 16;
    private const int MaxNameLength = 1024;

    /// <summary>What one operation (a count, a page's links) may cost at most, whatever the file says.</summary>
    private const int LoadsPerOperation = 4_000_000;
    private const long InflatePerOperation = 512L << 20;

    // Cross-reference entries, one long each: the kind in the top two bits.
    private const ulong KindInFile = 1UL << 62;
    private const ulong KindInStream = 2UL << 62;
    private const ulong KindFree = 3UL << 62;
    private const ulong KindMask = 3UL << 62;

    private readonly IByteSource _source;
    private readonly CancellationToken _cancel;
    private ulong[] _entries = [];
    private Dictionary<string, object?>? _trailer;

    /// <summary>Objects a table marked free in the section being read: its hidden stream may say where they are (a hybrid file).</summary>
    private readonly HashSet<int> _freedInSection = new();

    private readonly Dictionary<int, ObjectStream> _streams = new();
    private readonly LinkedList<int> _streamOrder = new();
    private long _streamBytes;
    private readonly HashSet<int> _loadingStreams = new();
    private readonly HashSet<int> _badStreams = new();
    private readonly HashSet<int> _badObjects = new();
    private int _loadDepth;

    private int _loadsLeft = LoadsPerOperation;
    private long _inflateLeft = InflatePerOperation;

    private List<object>? _pages;
    private bool _pagesComplete;
    private Dictionary<int, int>? _pageIndex;

    private sealed record ObjectStream(byte[] Data, int First, int[] Numbers, int[] Offsets);

    private PdfObjectReader(IByteSource source, CancellationToken cancel)
    {
        _source = source;
        _cancel = cancel;
    }

    /// <summary>A reader for the file, or null when it cannot be read whole this way: damaged, encrypted, not a PDF, or cancelled.</summary>
    public static PdfObjectReader? Open(IByteSource source, CancellationToken cancel = default)
    {
        var reader = new PdfObjectReader(source, cancel);
        try
        {
            return reader.ReadCrossReference() ? reader : null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>The highest object number plus one, as far as the cross-reference goes.</summary>
    public int ObjectCount => _entries.Length;

    public Dictionary<string, object?>? Catalog => Resolve(_trailer?.GetValueOrDefault("Root")) as Dictionary<string, object?>;

    /// <summary>Starts an operation: its own budget of objects to load and bytes to inflate.</summary>
    public void Begin()
    {
        _loadsLeft = LoadsPerOperation;
        _inflateLeft = InflatePerOperation;
    }

    // ===================================================================== resolving

    /// <summary>The value itself, references followed.</summary>
    public object? Resolve(object? value)
    {
        for (var hops = 0; value is PdfRef r && hops < 32; hops++) value = Load(r.Number);
        return value is PdfRef ? null : value;
    }

    public Dictionary<string, object?>? Dict(object? value) => Resolve(value) as Dictionary<string, object?>;

    public List<object?>? Array(object? value) => Resolve(value) as List<object?>;

    public string? Name(object? value) => (Resolve(value) as PdfName)?.Value;

    public double? Number(object? value) => Resolve(value) is double d && double.IsFinite(d) ? d : null;

    /// <summary>A text string, or a stream of text (a script is either): null for anything else, or past <paramref name="cap"/> bytes.</summary>
    public string? Text(object? value, int cap)
    {
        switch (Resolve(value))
        {
            case PdfString s:
                return s.Bytes.Length <= cap ? s.Text : null;
            case PdfStream stream:
                var data = Decode(stream, cap, out _);
                return data is null ? null : new PdfString(data).Text;
            default:
                return null;
        }
    }

    private object? Load(int number)
    {
        if ((uint)number >= (uint)_entries.Length || _loadDepth > MaxDepth || _badObjects.Contains(number)) return null;
        if (--_loadsLeft < 0) return null;
        var entry = _entries[number];
        _loadDepth++;
        try
        {
            switch (entry & KindMask)
            {
                case KindInFile:
                    var value = ReadAt((long)(entry & ~KindMask), header: true, number, out var failed);
                    if (failed) _badObjects.Add(number);
                    return value;
                case KindInStream:
                    return ReadStreamedObject((int)(uint)entry, (int)((entry >> 32) & 0x3FFFFFFF), number);
                default:
                    return null;
            }
        }
        finally
        {
            _loadDepth--;
        }
    }

    // ===================================================================== the cross-reference

    /// <summary>Every section, newest first. One that cannot be read means the file is not read: PDFium would rebuild its view of it.</summary>
    private bool ReadCrossReference()
    {
        try
        {
            var tailLength = (int)Math.Min(_source.Length, 32 * 1024);
            if (tailLength < 9) return false;
            var tail = new byte[tailLength];
            if (_source.ReadAt(_source.Length - tailLength, tail) != tailLength) return false;
            var at = tail.AsSpan().LastIndexOf("startxref"u8);
            if (at < 0) return false;
            var parser = new Parser(tail, at + 9);
            var next = parser.Integer();

            var seen = new HashSet<long>();
            while (next >= 0)
            {
                if (seen.Count >= MaxSections || !seen.Add(next)) return false;
                _freedInSection.Clear();
                var trailer = ReadSection(next, hybrid: false);
                if (trailer is null) return false;
                _trailer ??= trailer;

                // A hybrid file: an older reader's table, and a stream holding what it leaves out.
                if (trailer.ContainsKey("XRefStm"))
                {
                    if (Number(trailer["XRefStm"]) is not { } hidden || hidden <= 0 || !seen.Add((long)hidden)) return false;
                    if (ReadSection((long)hidden, hybrid: true) is null) return false;
                }

                next = Number(trailer.GetValueOrDefault("Prev")) is { } prev ? (long)prev : -1;
            }
            _freedInSection.Clear();

            // An encrypted file's strings and streams are not readable here.
            return _trailer is not null && !_trailer.ContainsKey("Encrypt") && _trailer.ContainsKey("Root");
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>One section - a table and its trailer, or a cross-reference stream - whose entries are taken where newer ones left a gap.</summary>
    private Dictionary<string, object?>? ReadSection(long offset, bool hybrid)
    {
        if (offset < 0 || offset >= _source.Length) return null;
        var head = new byte[64];
        var read = _source.ReadAt(offset, head);
        var p = 0;
        while (p < read && Parser.IsSpace(head[p])) p++;
        if (read - p >= 4 && head.AsSpan(p, 4).SequenceEqual("xref"u8)) return hybrid ? null : ReadTable(offset + p + 4);
        return ReadXrefStream(offset, hybrid);
    }

    private Dictionary<string, object?>? ReadTable(long position)
    {
        var reader = new SequentialReader(_source, position);
        while (true)
        {
            reader.SkipSpace();
            if (reader.StartsWith("trailer"u8)) return ReadAt(reader.Position + 7, header: false, -1, out _) as Dictionary<string, object?>;

            var first = reader.Integer();
            var count = reader.Integer();
            if (first < 0 || count < 0 || first + count > MaxObjects) return null;
            Grow((int)(first + count));

            for (var k = 0; k < count; k++)
            {
                if ((k & 0xFFF) == 0) _cancel.ThrowIfCancellationRequested();
                var offset = reader.Integer();
                var generation = reader.Integer();
                var kind = reader.Letter();
                if (offset < 0 || generation < 0 || kind is not ('n' or 'f')) return null;
                var number = (int)first + k;
                if (kind == 'n' && offset > 0)
                {
                    Set(number, KindInFile | (ulong)offset);
                }
                else if (Set(number, KindFree))
                {
                    _freedInSection.Add(number);
                }
            }
        }
    }

    private Dictionary<string, object?>? ReadXrefStream(long offset, bool hybrid)
    {
        if (ReadAt(offset, header: true, -1, out _) is not PdfStream stream || Name(stream.Dict.GetValueOrDefault("Type")) != "XRef") return null;
        if (Array(stream.Dict.GetValueOrDefault("W")) is not { Count: 3 } w) return null;
        var widths = new int[3];
        for (var i = 0; i < 3; i++)
        {
            if (Number(w[i]) is not { } n || n < 0 || n > 8) return null;
            widths[i] = (int)n;
        }
        var entrySize = widths[0] + widths[1] + widths[2];
        if (entrySize == 0) return null;

        var size = Number(stream.Dict.GetValueOrDefault("Size")) ?? 0;
        if (size < 0 || size > MaxObjects) return null;
        var index = Array(stream.Dict.GetValueOrDefault("Index")) ?? [0.0, size];

        _cancel.ThrowIfCancellationRequested();
        var data = Decode(stream, MaxDecoded, out _);
        if (data is null) return null;

        var at = 0;
        for (var pair = 0; pair + 1 < index.Count; pair += 2)
        {
            if (Number(index[pair]) is not { } first || Number(index[pair + 1]) is not { } count || first < 0 || count < 0 || first + count > MaxObjects) return null;
            Grow((int)(first + count));
            for (var k = 0; k < (int)count && at + entrySize <= data.Length; k++, at += entrySize)
            {
                if ((k & 0xFFFF) == 0) _cancel.ThrowIfCancellationRequested();
                var kind = widths[0] == 0 ? 1 : Field(data, at, widths[0]);
                var f2 = Field(data, at + widths[0], widths[1]);
                var f3 = Field(data, at + widths[0] + widths[1], widths[2]);
                var number = (int)first + k;
                var entry = kind switch
                {
                    0 => KindFree,
                    1 => f2 > 0 && f2 < (1L << 62) ? KindInFile | (ulong)f2 : KindFree,
                    2 => f2 is >= 0 and <= int.MaxValue && f3 is >= 0 and < 0x40000000 ? KindInStream | ((ulong)f3 << 32) | (ulong)f2 : KindFree,
                    _ => 0UL, // other kinds are null objects
                };
                if (entry == 0) continue;

                // In a hybrid file the table marks free what only this stream knows: the stream says where it is.
                if (hybrid && entry != KindFree && _freedInSection.Remove(number)) _entries[number] = entry;
                else Set(number, entry);
            }
        }
        return stream.Dict;

        static long Field(byte[] data, int at, int width)
        {
            long value = 0;
            for (var i = 0; i < width; i++) value = (value << 8) | data[at + i];
            return value;
        }
    }

    private void Grow(int count)
    {
        if (count <= _entries.Length) return;
        var size = Math.Min(MaxObjects, Math.Max(count, _entries.Length * 2));
        System.Array.Resize(ref _entries, size);
    }

    /// <summary>Newer sections are read first; an entry is never overwritten by an older one. Returns whether it was set.</summary>
    private bool Set(int number, ulong entry)
    {
        if ((uint)number >= (uint)_entries.Length || _entries[number] != 0) return false;
        _entries[number] = entry;
        return true;
    }

    // ===================================================================== objects in the file

    /// <summary>
    /// The value at a file position; with a header, the indirect object "N G obj" there, which
    /// must be number N (any number when it is -1). <paramref name="failed"/> when it does not
    /// parse - as opposed to a read cut short by the budget.
    /// </summary>
    private object? ReadAt(long offset, bool header, int number, out bool failed)
    {
        failed = true;
        if (offset < 0 || offset >= _source.Length) return null;
        for (var window = FirstWindow; ; window *= 4)
        {
            var size = (int)Math.Min(window, _source.Length - offset);
            // Small windows come from the pool; a big one is not left in it.
            var pooled = size <= 1 << 20;
            var buffer = pooled ? ArrayPool<byte>.Shared.Rent(size) : new byte[size];
            try
            {
                var read = _source.ReadAt(offset, buffer.AsSpan(0, size));
                // A short read is the end too: the file was cut short, or can no longer be read.
                var atEnd = read < size || offset + read >= _source.Length;
                var parser = new Parser(buffer.AsSpan(0, read));
                try
                {
                    if (header)
                    {
                        var n = parser.Integer();
                        parser.Integer();
                        if (!parser.Keyword("obj"u8) || (number >= 0 && n != number)) return null;
                    }

                    var value = parser.Value(0);
                    failed = false;
                    if (value is not Dictionary<string, object?> dict || !header) return value;

                    // A dictionary followed by "stream" and an end of line is a stream.
                    parser.SkipSpace();
                    if (!atEnd && parser.Remaining < 8) throw parser.End();
                    if (!parser.Keyword("stream"u8)) return dict;
                    while (parser.Remaining >= 1 && parser.Peek(0) is (byte)' ' or (byte)'\t') parser.Skip(1);
                    if (parser.Remaining >= 2 && parser.Peek(0) == '\r' && parser.Peek(1) == '\n') parser.Skip(2);
                    else if (parser.Remaining >= 1 && parser.Peek(0) is (byte)'\n' or (byte)'\r') parser.Skip(1);
                    return new PdfStream(dict, offset + parser.Position);
                }
                catch (FormatException)
                {
                    // Cut off by the window: read more of the file and try again.
                    failed = true;
                    if (!parser.HitEnd || atEnd || window >= MaxWindow) return null;
                }
            }
            finally
            {
                if (pooled) ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }

    // ===================================================================== objects in object streams

    private object? ReadStreamedObject(int streamNumber, int index, int number)
    {
        if (ObjectStreamAt(streamNumber) is not { } stream) return null;

        // The table's index, unless the stream says that is another object.
        var at = index < stream.Numbers.Length && stream.Numbers[index] == number ? index : System.Array.IndexOf(stream.Numbers, number);
        if (at < 0) return null;
        var start = (long)stream.First + stream.Offsets[at];
        if (start < 0 || start >= stream.Data.Length) return null;

        var parser = new Parser(stream.Data, (int)start);
        try
        {
            return parser.Value(0);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private ObjectStream? ObjectStreamAt(int number)
    {
        if (_streams.TryGetValue(number, out var cached))
        {
            _streamOrder.Remove(number);
            _streamOrder.AddFirst(number);
            return cached;
        }

        // A stream whose length is an object inside itself would load itself for ever; one that
        // could not be read is not read again.
        if (_badStreams.Contains(number) || !_loadingStreams.Add(number)) return null;
        var bad = true;
        try
        {
            if ((uint)number >= (uint)_entries.Length || (_entries[number] & KindMask) != KindInFile) return null;
            var stream = ReadAt((long)(_entries[number] & ~KindMask), header: true, number, out _) as PdfStream;
            if (stream is null) return null;
            if (Number(stream.Dict.GetValueOrDefault("N")) is not { } n || n < 0 || n > 1_000_000) return null;
            if (Number(stream.Dict.GetValueOrDefault("First")) is not { } first || first < 0) return null;
            var data = Decode(stream, MaxObjectStream, out var outOfBudget);
            if (data is null)
            {
                bad = !outOfBudget;
                return null;
            }
            if (first > data.Length) return null;

            // As many pairs of object number and offset as there are, up to /N.
            var numbers = new List<int>();
            var offsets = new List<int>();
            var parser = new Parser(data.AsSpan(0, (int)first));
            try
            {
                for (var i = 0; i < (int)n; i++)
                {
                    var objectNumber = parser.Integer();
                    var objectOffset = parser.Integer();
                    if (objectNumber > int.MaxValue || objectOffset > int.MaxValue) break;
                    numbers.Add((int)objectNumber);
                    offsets.Add((int)objectOffset);
                }
            }
            catch (FormatException)
            {
                // The pairs read so far stand.
            }

            bad = false;
            var loaded = new ObjectStream(data, (int)first, numbers.ToArray(), offsets.ToArray());
            _streams[number] = loaded;
            _streamOrder.AddFirst(number);
            _streamBytes += data.Length;
            while ((_streamBytes > StreamCacheBytes || _streams.Count > StreamCacheCount) && _streamOrder.Count > 1 && _streamOrder.Last is { } last)
            {
                _streamOrder.RemoveLast();
                if (_streams.Remove(last.Value, out var gone)) _streamBytes -= gone.Data.Length;
            }
            return loaded;
        }
        finally
        {
            _loadingStreams.Remove(number);
            // Not for want of budget: the next operation may read it.
            if (bad && _loadsLeft > 0) _badStreams.Add(number);
        }
    }

    // ===================================================================== stream data

    /// <summary>
    /// A stream's data, decoded; null when it is too big or in a form this does not read (only
    /// Flate is needed for structure). <paramref name="outOfBudget"/> when the operation's budget,
    /// not the stream, was the limit.
    /// </summary>
    private byte[]? Decode(PdfStream stream, int cap, out bool outOfBudget)
    {
        outOfBudget = false;
        var length = Number(stream.Dict.GetValueOrDefault("Length")) is { } l && l >= 0 && stream.DataOffset + l <= _source.Length && EndsAt(stream.DataOffset + (long)l)
            ? (long)l
            : FindEnd(stream.DataOffset);
        if (length < 0 || length > MaxEncoded) return null;

        var data = new byte[length];
        if (_source.ReadAt(stream.DataOffset, data) != length) return null;

        var filter = Resolve(stream.Dict.GetValueOrDefault("Filter"));
        var parms = Resolve(stream.Dict.GetValueOrDefault("DecodeParms"));
        List<object?>? filters = filter switch
        {
            PdfName single => [single],
            List<object?> list => list.Select(Resolve).ToList(),
            null => [],
            _ => null,
        };
        if (filters is null) return null;

        for (var i = 0; i < filters.Count; i++)
        {
            if (filters[i] is not PdfName { Value: "FlateDecode" or "Fl" }) return null;
            var limit = (int)Math.Min(cap, _inflateLeft);
            var decoded = Inflate(data, limit, out var overLimit);
            if (decoded is null)
            {
                outOfBudget = overLimit && limit < cap;
                return null;
            }
            _inflateLeft -= decoded.Length;
            data = decoded;

            var p = parms is List<object?> each ? (i < each.Count ? Dict(each[i]) : null) : Dict(parms);
            if (p is not null && Number(p.GetValueOrDefault("Predictor")) is { } predictor && predictor > 1)
            {
                var unpredicted = Unpredict(data, (int)predictor, Integer(p, "Colors", 1), Integer(p, "BitsPerComponent", 8), Integer(p, "Columns", 1));
                if (unpredicted is null) return null;
                data = unpredicted;
            }
        }
        return data;

        int Integer(Dictionary<string, object?> dict, string key, int fallback) =>
            Number(dict.GetValueOrDefault(key)) is { } v && v is >= 1 and <= 1 << 24 ? (int)v : fallback;
    }

    /// <summary>Whether "endstream" follows a stream's data where its /Length says it ends (after an end of line).</summary>
    private bool EndsAt(long end)
    {
        Span<byte> probe = stackalloc byte[32];
        var read = _source.ReadAt(end, probe);
        var p = 0;
        while (p < read && Parser.IsSpace(probe[p])) p++;
        return read - p >= 9 && probe.Slice(p, 9).SequenceEqual("endstream"u8);
    }

    /// <summary>Where "endstream" is, for a stream whose /Length is missing or wrong: the data's length, or -1.</summary>
    private long FindEnd(long start)
    {
        var reader = new SequentialReader(_source, start);
        var found = reader.Find("endstream"u8, MaxEncoded);
        if (found < 0) return -1;
        var end = found;
        // The end of line before "endstream" is not data.
        var before = new byte[2];
        if (end - start >= 2 && _source.ReadAt(end - 2, before) == 2)
        {
            if (before[0] == '\r' && before[1] == '\n') end -= 2;
            else if (before[1] is (byte)'\n' or (byte)'\r') end -= 1;
        }
        return end - start;
    }

    /// <summary>zlib data; raw deflate when the header is missing. What decodes before an error is kept, as PDFium keeps it.</summary>
    private static byte[]? Inflate(byte[] input, int cap, out bool overLimit)
    {
        overLimit = false;
        foreach (var zlib in new[] { true, false })
        {
            using var output = new MemoryStream();
            var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
            try
            {
                using var source = new MemoryStream(input, zlib ? 0 : Math.Min(2, input.Length), zlib ? input.Length : Math.Max(0, input.Length - 2), writable: false);
                using Stream inflater = zlib ? new ZLibStream(source, CompressionMode.Decompress) : new DeflateStream(source, CompressionMode.Decompress);
                int read;
                while ((read = inflater.Read(buffer, 0, buffer.Length)) > 0)
                {
                    if (output.Length + read > cap)
                    {
                        overLimit = true;
                        return null;
                    }
                    output.Write(buffer, 0, read);
                }
                return output.ToArray();
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException)
            {
                if (output.Length > 0) return output.ToArray();
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
        return null;
    }

    /// <summary>Undoes a PNG predictor (10-15, a filter byte per row) or the TIFF one (2, 8 bits).</summary>
    private static byte[]? Unpredict(byte[] data, int predictor, int colors, int bits, int columns)
    {
        if (colors > 32 || bits is not (1 or 2 or 4 or 8 or 16) || (long)colors * bits * columns > int.MaxValue / 2) return null;
        var pixelBytes = Math.Max(1, ((colors * bits) + 7) / 8);
        var rowLength = (int)((((long)colors * bits * columns) + 7) / 8);

        // A row longer than all the data is not a row of it.
        if (rowLength > data.Length) return null;

        if (predictor == 2)
        {
            if (bits != 8) return null;
            var output = (byte[])data.Clone();
            for (var row = 0; row + rowLength <= output.Length; row += rowLength)
                for (var i = pixelBytes; i < rowLength; i++)
                    output[row + i] += output[row + i - pixelBytes];
            return output;
        }
        if (predictor < 10) return data;

        var stride = rowLength + 1;
        var rows = (data.Length + stride - 1) / stride;
        var result = new byte[data.Length - rows];
        var previous = new byte[rowLength];
        var current = new byte[rowLength];
        var written = 0;
        for (var row = 0; row < rows; row++)
        {
            var at = row * stride;
            var type = data[at];
            var available = Math.Min(rowLength, data.Length - at - 1);
            System.Array.Clear(current);
            data.AsSpan(at + 1, available).CopyTo(current);
            for (var i = 0; i < rowLength; i++)
            {
                int left = i >= pixelBytes ? current[i - pixelBytes] : 0;
                int up = previous[i];
                int upLeft = i >= pixelBytes ? previous[i - pixelBytes] : 0;
                current[i] = type switch
                {
                    1 => (byte)(current[i] + left),
                    2 => (byte)(current[i] + up),
                    3 => (byte)(current[i] + ((left + up) >> 1)),
                    4 => (byte)(current[i] + Paeth(left, up, upLeft)),
                    _ => current[i],
                };
            }
            current.AsSpan(0, available).CopyTo(result.AsSpan(written));
            written += available;
            (previous, current) = (current, previous);
        }
        return result;

        static int Paeth(int a, int b, int c)
        {
            var p = a + b - c;
            int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
            return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
        }
    }

    // ===================================================================== pages

    /// <summary>How many pages the page tree has; -1 when it could not be read whole.</summary>
    public int PageCount
    {
        get
        {
            var pages = Pages();
            return _pagesComplete ? pages.Count : -1;
        }
    }

    /// <summary>A page's dictionary, or null.</summary>
    public Dictionary<string, object?>? Page(int index)
    {
        var pages = Pages();
        return (uint)index < (uint)pages.Count ? Dict(pages[index]) : null;
    }

    /// <summary>The index of the page a destination names by reference (or by number, as broken files do); -1 if it is no page.</summary>
    public int PageIndexOf(object? page)
    {
        Pages();
        return page switch
        {
            PdfRef r => _pageIndex!.TryGetValue(r.Number, out var index) ? index : -1,
            double d when d >= 0 && d < _pages!.Count && d == Math.Floor(d) => (int)d,
            _ => -1,
        };
    }

    /// <summary>
    /// The pages in order, as references (or a page written inline): a dictionary with /Kids is a
    /// node of the tree, anything else is a page, as PDFium takes them. A node met a second time
    /// is a loop, and is cut; a tree that asks for more work than any real one is not read whole.
    /// </summary>
    private List<object> Pages()
    {
        if (_pages is not null) return _pages;
        Begin();
        _pages = new List<object>();
        _pageIndex = new Dictionary<int, int>();
        _pagesComplete = true;

        var nodes = new HashSet<int>();
        var stack = new Stack<(object? Node, int Depth)>();
        stack.Push((Catalog?.GetValueOrDefault("Pages"), 0));
        var pushes = 0;
        for (var step = 0; stack.Count > 0; step++)
        {
            if (_pages.Count >= MaxPages || pushes > MaxPagePushes || _loadsLeft <= 0)
            {
                _pagesComplete = false;
                break;
            }
            if ((step & 0xFFF) == 0) _cancel.ThrowIfCancellationRequested();

            var (node, depth) = stack.Pop();
            if (depth > MaxTreeDepth || Dict(node) is not { } dict) continue;

            if (dict.ContainsKey("Kids"))
            {
                if (node is PdfRef r && !nodes.Add(r.Number)) continue;
                if (Array(dict["Kids"]) is not { } kids) continue;
                // Pushed last to first, so they come off in order.
                pushes += kids.Count;
                for (var k = kids.Count - 1; k >= 0; k--) stack.Push((kids[k], depth + 1));
                continue;
            }

            if (depth == 0) continue; // the root itself is never a page
            if (node is PdfRef page) _pageIndex.TryAdd(page.Number, _pages.Count);
            _pages.Add(node is PdfRef ? node : dict);
        }
        return _pages;
    }

    // ===================================================================== forms

    /// <summary>
    /// How many fields the form has, counting no further than <paramref name="limit"/>; as PDFium
    /// loads them: a field whose first kid has a name (/T) or kids of its own has fields below
    /// it, any other field is one with its widgets.
    /// </summary>
    public int CountFormFields(int limit)
    {
        Begin();
        if (Dict(Catalog?.GetValueOrDefault("AcroForm")) is not { } form || Array(form.GetValueOrDefault("Fields")) is not { } fields) return 0;
        if (fields.Count >= limit) return fields.Count;

        var count = 0;
        var seen = new HashSet<int>();
        var queue = new Queue<(object? Field, int Depth)>();
        foreach (var field in fields)
        {
            if (field is PdfRef r && !seen.Add(r.Number)) continue;
            queue.Enqueue((field, 0));
        }

        while (queue.Count > 0)
        {
            if (count + queue.Count >= limit) return limit;
            _cancel.ThrowIfCancellationRequested();
            var (field, depth) = queue.Dequeue();
            if (Dict(field) is not { } dict) continue;

            var kids = Array(dict.GetValueOrDefault("Kids"));
            var firstKid = kids is { Count: > 0 } ? Dict(kids[0]) : null;
            if (kids is not { Count: > 0 } || (firstKid is not null && !firstKid.ContainsKey("T") && !firstKid.ContainsKey("Kids")))
            {
                count++;
                continue;
            }
            if (firstKid is null || depth >= MaxFieldDepth) continue;

            foreach (var kid in kids)
            {
                if (kid is PdfRef r && !seen.Add(r.Number)) continue;
                queue.Enqueue((kid, depth + 1));
                if (count + queue.Count >= limit) return limit;
            }
        }
        return count;
    }

    // ===================================================================== 3D annotations

    /// <summary>Where a page's 3D annotations are in its /Annots.</summary>
    public List<int> ThreeDAnnotations(int pageIndex)
    {
        Begin();
        var found = new List<int>();
        if (Page(pageIndex) is not { } page || Array(page.GetValueOrDefault("Annots")) is not { } annots) return found;
        for (var i = 0; i < annots.Count; i++)
            if (Dict(annots[i]) is { } annot && Name(annot.GetValueOrDefault("Subtype")) == "3D") found.Add(i);
        return found;
    }

    /// <summary>
    /// The model of the 3D annotation at <paramref name="annotation"/> in a page's /Annots (PDF 32000
    /// 13.6): its 3D stream, given directly or through a 3D reference, decoded, and those of its
    /// views that place the camera - the one to start with first: the annotation's /3DV, else the
    /// stream's /DV, else its first. Null when that is no 3D annotation, or its stream is over
    /// <paramref name="cap"/> bytes decoded or in a form this does not read.
    /// </summary>
    public Pdf3DModel? ThreeDModel(int pageIndex, int annotation, int cap)
    {
        Begin();
        if (Page(pageIndex) is not { } page || Array(page.GetValueOrDefault("Annots")) is not { } annots || (uint)annotation >= (uint)annots.Count) return null;
        if (Dict(annots[annotation]) is not { } annot || Name(annot.GetValueOrDefault("Subtype")) != "3D") return null;
        var data = Resolve(annot.GetValueOrDefault("3DD"));
        if (data is Dictionary<string, object?> reference) data = Resolve(reference.GetValueOrDefault("3DD"));
        if (data is not PdfStream stream) return null;
        var kind = Name(stream.Dict.GetValueOrDefault("Subtype"));
        if (kind is not ("PRC" or "U3D") || Decode(stream, cap, out _) is not { } bytes) return null;

        var listed = Array(stream.Dict.GetValueOrDefault("VA")) ?? [];
        var views = new List<Pdf3DView>();
        var start = -1;
        for (var i = 0; i < listed.Count && views.Count < 10_000; i++)
        {
            if (View(listed[i], views.Count + 1) is not { } view) continue;
            if (start < 0 && IsStart(listed[i], i)) start = views.Count;
            views.Add(view);
        }
        if (start < 0 && (View(annot.GetValueOrDefault("3DV"), 0) ?? View(stream.Dict.GetValueOrDefault("DV"), 0)) is { } own)
        {
            // A view of the annotation's own, not one of those listed.
            views.Insert(0, own);
            start = 0;
        }
        if (start > 0)
        {
            var first = views[start];
            views.RemoveAt(start);
            views.Insert(0, first);
        }
        var aspect = 1.0;
        if (Array(annot.GetValueOrDefault("Rect")) is { Count: 4 } rect && rect.All(n => Number(n) is not null))
        {
            var width = Math.Abs(Number(rect[2])!.Value - Number(rect[0])!.Value);
            var height = Math.Abs(Number(rect[3])!.Value - Number(rect[1])!.Value);
            if (width > 0 && height > 0) aspect = Math.Clamp(height / width, 0.05, 20);
        }
        return new Pdf3DModel(kind, bytes, views, aspect);

        // Whether the annotation, or else the stream, starts with this listed view: naming it as a
        // view, by its index or its name, or by F, L or D (first, last, the stream's default).
        bool IsStart(object? entry, int index)
        {
            foreach (var choice in (ReadOnlySpan<object?>)[annot.GetValueOrDefault("3DV"), stream.Dict.GetValueOrDefault("DV")])
            {
                if (choice is null || (choice is PdfName { Value: "D" })) continue;
                if (choice is PdfRef r) return entry is PdfRef e && e.Number == r.Number;
                return Resolve(choice) switch
                {
                    double d => (int)d == index,
                    PdfName { Value: "F" } => index == 0,
                    PdfName { Value: "L" } => index == listed.Count - 1,
                    PdfString => Dict(entry) is { } v && Text(v.GetValueOrDefault("IN"), 256) == Text(choice, 256),
                    _ => false,
                };
            }
            return index == 0;
        }

        // A view that places the camera by a matrix (a U3D view can name a node of its model instead).
        Pdf3DView? View(object? value, int number)
        {
            if (Dict(value) is not { } v || Array(v.GetValueOrDefault("C2W")) is not { Count: 12 } c2w) return null;
            var matrix = new double[12];
            for (var k = 0; k < 12; k++)
            {
                if (Number(c2w[k]) is not { } n) return null;
                matrix[k] = n;
            }
            var name = Text(v.GetValueOrDefault("XN"), 256) ?? Text(v.GetValueOrDefault("IN"), 256) ?? number.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var projection = Dict(v.GetValueOrDefault("P"));
            var fov = Name(projection?.GetValueOrDefault("Subtype")) == "P" && Number(projection!.GetValueOrDefault("FOV")) is { } f && f is > 0 and < 180 ? f : 30;
            return new Pdf3DView(name.Trim(), matrix, fov);
        }
    }

    // ===================================================================== reading bytes in order

    /// <summary>Reads the file front to back through a buffer: for cross-reference tables, which can run to many megabytes.</summary>
    private sealed class SequentialReader(IByteSource source, long start)
    {
        private readonly byte[] _buffer = new byte[64 * 1024];
        private long _bufferStart = start;
        private int _count;
        private int _at;

        public long Position => _bufferStart + _at;

        private bool Fill()
        {
            if (_at < _count) return true;
            _bufferStart += _count;
            _at = 0;
            _count = source.ReadAt(_bufferStart, _buffer);
            return _count > 0;
        }

        private int Peek() => Fill() ? _buffer[_at] : -1;

        public void SkipSpace()
        {
            while (Peek() is var c && c >= 0)
            {
                if (Parser.IsSpace((byte)c))
                {
                    _at++;
                }
                else if (c == '%')
                {
                    while (Peek() is var d && d >= 0 && d is not ('\r' or '\n')) _at++;
                }
                else
                {
                    return;
                }
            }
        }

        public bool StartsWith(ReadOnlySpan<byte> word)
        {
            Span<byte> probe = stackalloc byte[word.Length];
            return source.ReadAt(Position, probe) == word.Length && probe.SequenceEqual(word);
        }

        /// <summary>A non-negative integer; -1 when there is none.</summary>
        public long Integer()
        {
            SkipSpace();
            long value = 0;
            var digits = 0;
            while (Peek() is var c && c is >= '0' and <= '9')
            {
                if (++digits > 18) return -1;
                value = (value * 10) + (c - '0');
                _at++;
            }
            return digits > 0 ? value : -1;
        }

        public int Letter()
        {
            SkipSpace();
            var c = Peek();
            if (c >= 0) _at++;
            return c;
        }

        /// <summary>The position of <paramref name="word"/> from here on, looking no further than <paramref name="limit"/> bytes; -1 if absent.</summary>
        public long Find(ReadOnlySpan<byte> word, long limit)
        {
            var window = new byte[64 * 1024 + word.Length];
            var from = Position;
            while (from - Position < limit)
            {
                var read = source.ReadAt(from, window);
                if (read < word.Length) return -1;
                var found = window.AsSpan(0, read).IndexOf(word);
                if (found >= 0) return from + found;
                from += read - word.Length + 1;
            }
            return -1;
        }
    }

    // ===================================================================== the object syntax

    private ref struct Parser
    {
        private readonly ReadOnlySpan<byte> _d;
        private int _p;

        public Parser(ReadOnlySpan<byte> data, int start = 0)
        {
            _d = data;
            _p = start;
            HitEnd = false;
        }

        /// <summary>Whether parsing failed by running out of data (the object may go on past it).</summary>
        public bool HitEnd { get; private set; }

        public readonly int Position => _p;

        public readonly int Remaining => _d.Length - _p;

        public readonly byte Peek(int ahead) => _d[_p + ahead];

        public void Skip(int count) => _p += count;

        public FormatException End()
        {
            HitEnd = true;
            return new FormatException();
        }

        public object? Value(int depth)
        {
            if (depth > MaxDepth) throw new FormatException();
            SkipSpace();
            if (_p >= _d.Length) throw End();

            var c = _d[_p];
            switch (c)
            {
                case (byte)'/':
                    _p++;
                    return new PdfName(ReadName());

                case (byte)'<' when _p + 1 < _d.Length && _d[_p + 1] == '<':
                {
                    _p += 2;
                    var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
                    while (true)
                    {
                        SkipSpace();
                        if (_p + 1 >= _d.Length) throw End();
                        if (_d[_p] == '>' && _d[_p + 1] == '>')
                        {
                            _p += 2;
                            return dict;
                        }
                        if (_d[_p] != '/') throw new FormatException();
                        _p++;
                        var key = ReadName();
                        dict[key] = Value(depth + 1);
                    }
                }

                case (byte)'<':
                    if (_p + 1 >= _d.Length) throw End();
                    return HexString();

                case (byte)'[':
                {
                    _p++;
                    var list = new List<object?>();
                    while (true)
                    {
                        SkipSpace();
                        if (_p >= _d.Length) throw End();
                        if (_d[_p] == ']')
                        {
                            _p++;
                            return list;
                        }
                        list.Add(Value(depth + 1));
                    }
                }

                case (byte)'(':
                    return LiteralString();

                default:
                    if (IsDigit(c) || c is (byte)'+' or (byte)'-' or (byte)'.') return NumberOrRef();
                    if (Keyword("true"u8)) return true;
                    if (Keyword("false"u8)) return false;
                    if (Keyword("null"u8)) return null;
                    throw new FormatException();
            }
        }

        /// <summary>A non-negative integer.</summary>
        public long Integer()
        {
            SkipSpace();
            if (_p >= _d.Length) throw End();
            var start = _p;
            var value = Number(out var isInteger);
            if (!isInteger || value < 0 || value > long.MaxValue / 2 || _d[start] is (byte)'+' or (byte)'-') throw new FormatException();
            return (long)value;
        }

        /// <summary>"12 0 R" is a reference; "12 0 612" is three numbers.</summary>
        private object NumberOrRef()
        {
            var number = Number(out var isInteger);
            if (!isInteger || number < 0 || number > int.MaxValue) return number;

            var after = _p;
            SkipSpace();
            if (_p < _d.Length && IsDigit(_d[_p]))
            {
                Number(out var generationIsInteger);
                SkipSpace();
                if (generationIsInteger && _p < _d.Length && _d[_p] == 'R' && (_p + 1 >= _d.Length || IsDelimiterOrSpace(_d[_p + 1])))
                {
                    _p++;
                    return new PdfRef((int)number);
                }
            }
            _p = after;
            return number;
        }

        private double Number(out bool isInteger)
        {
            var start = _p;
            if (_p < _d.Length && _d[_p] is (byte)'+' or (byte)'-') _p++;
            var digits = 0;
            var dot = false;
            while (_p < _d.Length && (IsDigit(_d[_p]) || (_d[_p] == '.' && !dot)))
            {
                if (_d[_p] == '.') dot = true;
                else digits++;
                _p++;
            }
            if (digits == 0 || _p - start > 40) throw _p >= _d.Length ? End() : new FormatException();
            isInteger = !dot;
            return double.Parse(_d[start.._p], NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        /// <summary>A name; one longer than any real name is read past, and kept to its first part.</summary>
        private string ReadName()
        {
            var sb = new StringBuilder();
            while (_p < _d.Length && !IsDelimiterOrSpace(_d[_p]))
            {
                char c;
                if (_d[_p] == '#' && _p + 2 < _d.Length && Hex(_d[_p + 1]) is var hi and >= 0 && Hex(_d[_p + 2]) is var lo and >= 0)
                {
                    c = (char)((hi << 4) | lo);
                    _p += 3;
                }
                else
                {
                    c = (char)_d[_p];
                    _p++;
                }
                if (sb.Length < MaxNameLength) sb.Append(c);
            }
            return sb.ToString();
        }

        private PdfString LiteralString()
        {
            _p++; // (
            var bytes = new List<byte>();
            var depth = 1;
            while (true)
            {
                if (_p >= _d.Length) throw End();
                var c = _d[_p++];
                if (c == '\\')
                {
                    if (_p >= _d.Length) throw End();
                    var e = _d[_p++];
                    switch (e)
                    {
                        case (byte)'n': bytes.Add((byte)'\n'); break;
                        case (byte)'r': bytes.Add((byte)'\r'); break;
                        case (byte)'t': bytes.Add((byte)'\t'); break;
                        case (byte)'b': bytes.Add((byte)'\b'); break;
                        case (byte)'f': bytes.Add((byte)'\f'); break;
                        case (byte)'\r':
                            if (_p < _d.Length && _d[_p] == '\n') _p++;
                            break; // a line continued
                        case (byte)'\n':
                            break;
                        case >= (byte)'0' and <= (byte)'7':
                        {
                            var value = e - '0';
                            for (var k = 0; k < 2 && _p < _d.Length && _d[_p] is >= (byte)'0' and <= (byte)'7'; k++) value = (value * 8) + (_d[_p++] - '0');
                            bytes.Add((byte)value);
                            break;
                        }
                        default:
                            bytes.Add(e);
                            break;
                    }
                    continue;
                }
                if (c == '(') depth++;
                else if (c == ')' && --depth == 0) return new PdfString(bytes.ToArray());
                bytes.Add(c);
                if (bytes.Count > 16 << 20) throw new FormatException();
            }
        }

        /// <summary>A hex string; anything in it that is not a hex digit is passed over, as PDFium does.</summary>
        private PdfString HexString()
        {
            _p++; // <
            var bytes = new List<byte>();
            var high = -1;
            while (true)
            {
                if (_p >= _d.Length) throw End();
                var c = _d[_p++];
                if (c == '>')
                {
                    if (high >= 0) bytes.Add((byte)(high << 4));
                    return new PdfString(bytes.ToArray());
                }
                var v = Hex(c);
                if (v < 0) continue;
                if (high < 0)
                {
                    high = v;
                }
                else
                {
                    bytes.Add((byte)((high << 4) | v));
                    high = -1;
                }
                if (bytes.Count > 16 << 20) throw new FormatException();
            }
        }

        public bool Keyword(ReadOnlySpan<byte> word)
        {
            SkipSpace();
            if (_p + word.Length > _d.Length)
            {
                if (_d[_p..].SequenceEqual(word[..(_d.Length - _p)])) throw End();
                return false;
            }
            if (!_d.Slice(_p, word.Length).SequenceEqual(word)) return false;
            if (_p + word.Length < _d.Length && !IsDelimiterOrSpace(_d[_p + word.Length])) return false;
            _p += word.Length;
            return true;
        }

        public void SkipSpace()
        {
            while (_p < _d.Length)
            {
                if (IsSpace(_d[_p]))
                {
                    _p++;
                }
                else if (_d[_p] == '%')
                {
                    while (_p < _d.Length && _d[_p] is not ((byte)'\r' or (byte)'\n')) _p++;
                }
                else
                {
                    return;
                }
            }
        }

        public static bool IsSpace(byte c) => c is 0 or 9 or 10 or 12 or 13 or 32;

        private static bool IsDigit(byte c) => c is >= (byte)'0' and <= (byte)'9';

        private static int Hex(byte c) => c switch
        {
            >= (byte)'0' and <= (byte)'9' => c - '0',
            >= (byte)'a' and <= (byte)'f' => c - 'a' + 10,
            >= (byte)'A' and <= (byte)'F' => c - 'A' + 10,
            _ => -1,
        };

        private static bool IsDelimiterOrSpace(byte c) =>
            IsSpace(c) || c is (byte)'(' or (byte)')' or (byte)'<' or (byte)'>' or (byte)'[' or (byte)']' or (byte)'{' or (byte)'}' or (byte)'/' or (byte)'%';
    }
}
