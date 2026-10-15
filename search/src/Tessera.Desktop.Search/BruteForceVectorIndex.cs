using System.Buffers.Binary;
using System.Numerics.Tensors;
using System.Runtime.InteropServices;

namespace Tessera.Desktop.Search;

/// <summary>
/// Brute-force cosine search over int8 vectors, one scale per vector, float
/// accumulation. Every query touches every row, which at 200,000 rows of 768
/// dimensions is 150 MB of sequential reads split across the cores.
/// </summary>
/// <remarks>
/// <para>Storage: rows are packed into blocks of <see cref="BlockRows"/>
/// vectors so growth never copies the whole matrix and no single array goes
/// past a few megabytes. Ids and scales are flat arrays. A removed row is
/// filled by the last row, so the matrix stays dense.</para>
/// <para>Quantisation: a vector is scaled so its largest component maps to
/// ±127 and rounded. The stored scale is <c>1 / |q|</c> where <c>q</c> is
/// the int8 vector, so <c>dot(query, q) * scale</c> is exactly the cosine
/// between the query and the vector the int8 row reconstructs to.</para>
/// <para>Locking: a reader-writer lock. Searches share the read side;
/// <see cref="Upsert"/>, <see cref="Remove"/> and <see cref="LoadAsync"/>
/// take the write side. <see cref="SaveAsync"/> reads under the read lock
/// on a worker thread, so writers wait for a save but searches do not.</para>
/// <para>File format (little-endian): magic <c>TSVX</c>, version u32, dims
/// i32, count i32, 16 reserved bytes, then ids (i64 × count), scales (f32 ×
/// count), vectors (i8 × count × dims).</para>
/// </remarks>
public sealed class BruteForceVectorIndex : IVectorIndex, IDisposable
{
    /// <summary>"TSVX" as little-endian bytes.</summary>
    public const uint Magic = 0x58565354;
    public const uint FormatVersion = 1;
    public const int HeaderBytes = 32;

    /// <summary>Rows per block: 4096 × 768 bytes is a 3 MB array.</summary>
    internal const int BlockRows = 4096;

    private readonly ReaderWriterLockSlim _lock = new(LockRecursionPolicy.NoRecursion);
    private readonly List<sbyte[]> _blocks = new();
    private readonly Dictionary<long, int> _rowOf = new();
    private long[] _ids = new long[BlockRows];
    private float[] _scales = new float[BlockRows];
    private int _count;
    private readonly int _dims;

    public BruteForceVectorIndex(int dimensions)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(dimensions);
        _dims = dimensions;
    }

    public int Count => Volatile.Read(ref _count);
    public int Dimensions => _dims;

    /// <summary>Bytes held by vectors, ids and scales (not the id map).</summary>
    public long ApproximateBytes
    {
        get
        {
            _lock.EnterReadLock();
            try { return (long)_blocks.Count * BlockRows * _dims + (long)_ids.Length * 8 + (long)_scales.Length * 4; }
            finally { _lock.ExitReadLock(); }
        }
    }

    // ------------------------------------------------------------------ writes

    public void Upsert(long id, ReadOnlySpan<float> vector)
    {
        if (vector.Length != _dims)
            throw new ArgumentException($"Expected {_dims} dimensions, got {vector.Length}.", nameof(vector));

        Span<sbyte> q = _dims <= 4096 ? stackalloc sbyte[_dims] : new sbyte[_dims];
        float scale = Quantize(vector, q);

        _lock.EnterWriteLock();
        try
        {
            if (!_rowOf.TryGetValue(id, out int row))
            {
                row = _count;
                EnsureCapacity(row + 1);
                _rowOf[id] = row;
                _count = row + 1;
            }
            q.CopyTo(RowSpan(row));
            _ids[row] = id;
            _scales[row] = scale;
        }
        finally { _lock.ExitWriteLock(); }
    }

    public bool Remove(long id)
    {
        _lock.EnterWriteLock();
        try
        {
            if (!_rowOf.Remove(id, out int row)) return false;
            int last = _count - 1;
            if (row != last)
            {
                RowSpan(last).CopyTo(RowSpan(row));
                _ids[row] = _ids[last];
                _scales[row] = _scales[last];
                _rowOf[_ids[row]] = row;
            }
            RowSpan(last).Clear();
            _ids[last] = 0;
            _scales[last] = 0;
            _count = last;
            return true;
        }
        finally { _lock.ExitWriteLock(); }
    }

    public bool Contains(long id)
    {
        _lock.EnterReadLock();
        try { return _rowOf.ContainsKey(id); }
        finally { _lock.ExitReadLock(); }
    }

    /// <summary>Drops every row.</summary>
    public void Clear()
    {
        _lock.EnterWriteLock();
        try { ClearUnlocked(); }
        finally { _lock.ExitWriteLock(); }
    }

    private void ClearUnlocked()
    {
        _blocks.Clear();
        _rowOf.Clear();
        _ids = new long[BlockRows];
        _scales = new float[BlockRows];
        _count = 0;
    }

    // ------------------------------------------------------------------ search

    /// <summary>
    /// Best <paramref name="k"/> rows by cosine. The query is normalised
    /// here, so callers may pass a raw vector. <paramref name="filter"/> runs
    /// before the dot product and from several threads at once.
    /// </summary>
    public IReadOnlyList<VectorHit> Search(ReadOnlySpan<float> query, int k, Func<long, bool>? filter = null)
    {
        if (query.Length != _dims)
            throw new ArgumentException($"Expected {_dims} dimensions, got {query.Length}.", nameof(query));
        if (k <= 0) return Array.Empty<VectorHit>();

        float[] q = query.ToArray();
        float norm = TensorPrimitives.Norm(q);
        if (norm > 0 && Math.Abs(norm - 1f) > 1e-4f) TensorPrimitives.Divide(q, norm, q);

        _lock.EnterReadLock();
        try
        {
            int count = _count;
            if (count == 0) return Array.Empty<VectorHit>();
            int blocks = (count + BlockRows - 1) / BlockRows;
            int workers = Math.Min(blocks, Environment.ProcessorCount);

            TopK merged;
            if (workers <= 1 || count < 2 * BlockRows)
            {
                merged = new TopK(k);
                ScanBlocks(q, 0, blocks, count, filter, merged);
            }
            else
            {
                merged = new TopK(k);
                object gate = new();
                Parallel.For(0, blocks, new ParallelOptions { MaxDegreeOfParallelism = workers },
                    () => new TopK(k),
                    (b, _, local) => { ScanBlocks(q, b, b + 1, count, filter, local); return local; },
                    local => { lock (gate) merged.Merge(local); });
            }
            return merged.ToSortedHits();
        }
        finally { _lock.ExitReadLock(); }
    }

    private void ScanBlocks(float[] q, int fromBlock, int toBlock, int count, Func<long, bool>? filter, TopK top)
    {
        for (int b = fromBlock; b < toBlock; b++)
        {
            sbyte[] block = _blocks[b];
            int first = b * BlockRows;
            int rows = Math.Min(BlockRows, count - first);
            for (int r = 0; r < rows; r++)
            {
                int row = first + r;
                long id = _ids[row];
                if (filter is not null && !filter(id)) continue;
                float dot = Int8Dot.Dot(q, block.AsSpan(r * _dims, _dims));
                top.TryAdd(dot * _scales[row], id);
            }
        }
    }

    // ------------------------------------------------------------------ persistence

    public Task SaveAsync(Stream stream, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return Task.Run(() =>
        {
            _lock.EnterReadLock();
            try
            {
                ct.ThrowIfCancellationRequested();
                Span<byte> header = stackalloc byte[HeaderBytes];
                header.Clear();
                BinaryPrimitives.WriteUInt32LittleEndian(header, Magic);
                BinaryPrimitives.WriteUInt32LittleEndian(header[4..], FormatVersion);
                BinaryPrimitives.WriteInt32LittleEndian(header[8..], _dims);
                BinaryPrimitives.WriteInt32LittleEndian(header[12..], _count);
                stream.Write(header);

                WriteLittleEndian(stream, _ids.AsSpan(0, _count));
                WriteLittleEndian(stream, _scales.AsSpan(0, _count));
                int remaining = _count;
                for (int b = 0; b < _blocks.Count && remaining > 0; b++)
                {
                    ct.ThrowIfCancellationRequested();
                    int rows = Math.Min(BlockRows, remaining);
                    stream.Write(MemoryMarshal.AsBytes(_blocks[b].AsSpan(0, rows * _dims)));
                    remaining -= rows;
                }
                stream.Flush();
            }
            finally { _lock.ExitReadLock(); }
        }, ct);
    }

    public async Task LoadAsync(Stream stream, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(stream);
        byte[] header = new byte[HeaderBytes];
        await stream.ReadExactlyAsync(header, ct).ConfigureAwait(false);
        if (BinaryPrimitives.ReadUInt32LittleEndian(header) != Magic)
            throw new InvalidDataException("Not a Tessera vector index (bad magic).");
        uint version = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4));
        if (version != FormatVersion)
            throw new InvalidDataException($"Vector index format {version} is not supported (expected {FormatVersion}).");
        int dims = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(8));
        int count = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(12));
        if (dims != _dims)
            throw new InvalidDataException($"Vector index has {dims} dimensions; this index expects {_dims}.");
        if (count < 0) throw new InvalidDataException("Negative row count.");

        int capacity = Math.Max(BlockRows, (count + BlockRows - 1) / BlockRows * BlockRows);
        long[] ids = new long[capacity];
        float[] scales = new float[capacity];
        await ReadLittleEndianAsync(stream, ids, count, ct).ConfigureAwait(false);
        await ReadLittleEndianAsync(stream, scales, count, ct).ConfigureAwait(false);

        List<sbyte[]> blocks = new();
        byte[] scratch = new byte[BlockRows * _dims];
        int remaining = count;
        while (remaining > 0)
        {
            ct.ThrowIfCancellationRequested();
            int rows = Math.Min(BlockRows, remaining);
            int bytes = rows * _dims;
            await stream.ReadExactlyAsync(scratch.AsMemory(0, bytes), ct).ConfigureAwait(false);
            sbyte[] block = new sbyte[BlockRows * _dims];
            Buffer.BlockCopy(scratch, 0, block, 0, bytes);
            blocks.Add(block);
            remaining -= rows;
        }

        Dictionary<long, int> rowOf = new(count);
        for (int i = 0; i < count; i++)
        {
            if (!rowOf.TryAdd(ids[i], i))
                throw new InvalidDataException($"Duplicate id {ids[i]} in vector index.");
        }

        _lock.EnterWriteLock();
        try
        {
            _blocks.Clear();
            _blocks.AddRange(blocks);
            _rowOf.Clear();
            foreach (var kv in rowOf) _rowOf[kv.Key] = kv.Value;
            _ids = ids;
            _scales = scales;
            _count = count;
        }
        finally { _lock.ExitWriteLock(); }
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>
    /// Scales <paramref name="v"/> so its largest magnitude is 127, rounds to
    /// int8, and returns <c>1/|q|</c> so that <c>dot(x, q) * scale</c> is the
    /// cosine between a unit <c>x</c> and the reconstructed vector.
    /// </summary>
    internal static float Quantize(ReadOnlySpan<float> v, Span<sbyte> q)
    {
        float maxAbs = TensorPrimitives.MaxMagnitude(v);
        if (maxAbs <= 0 || float.IsNaN(maxAbs))
        {
            q.Clear();
            return 0f;
        }
        float inv = 127f / maxAbs;
        double sumSq = 0;
        for (int i = 0; i < v.Length; i++)
        {
            int r = (int)MathF.Round(v[i] * inv);
            if (r > 127) r = 127; else if (r < -127) r = -127;
            q[i] = (sbyte)r;
            sumSq += (double)r * r;
        }
        return sumSq > 0 ? (float)(1.0 / Math.Sqrt(sumSq)) : 0f;
    }

    private Span<sbyte> RowSpan(int row)
        => _blocks[row / BlockRows].AsSpan(row % BlockRows * _dims, _dims);

    private void EnsureCapacity(int rows)
    {
        while (_blocks.Count * BlockRows < rows) _blocks.Add(new sbyte[BlockRows * _dims]);
        if (_ids.Length < rows)
        {
            int cap = Math.Max(rows, _ids.Length * 2);
            Array.Resize(ref _ids, cap);
            Array.Resize(ref _scales, cap);
        }
    }

    private static void WriteLittleEndian<T>(Stream stream, ReadOnlySpan<T> values) where T : unmanaged
    {
        if (BitConverter.IsLittleEndian)
        {
            stream.Write(MemoryMarshal.AsBytes(values));
            return;
        }
        // Big-endian hosts: write element by element.
        Span<byte> buf = stackalloc byte[8];
        foreach (T v in values)
        {
            if (typeof(T) == typeof(long)) BinaryPrimitives.WriteInt64LittleEndian(buf, (long)(object)v!);
            else if (typeof(T) == typeof(float)) BinaryPrimitives.WriteSingleLittleEndian(buf, (float)(object)v!);
            else throw new NotSupportedException(typeof(T).Name);
            stream.Write(buf[..Marshal.SizeOf<T>()]);
        }
    }

    /// <summary>Reads <paramref name="count"/> little-endian values of <typeparamref name="T"/> into <paramref name="target"/>.</summary>
    private static async Task ReadLittleEndianAsync<T>(Stream stream, T[] target, int count, CancellationToken ct) where T : unmanaged
    {
        int size = Marshal.SizeOf<T>();
        byte[] scratch = new byte[count * size];
        await stream.ReadExactlyAsync(scratch, ct).ConfigureAwait(false);
        if (BitConverter.IsLittleEndian)
        {
            Buffer.BlockCopy(scratch, 0, target, 0, scratch.Length);
            return;
        }
        for (int i = 0; i < count; i++)
        {
            ReadOnlySpan<byte> b = scratch.AsSpan(i * size, size);
            if (typeof(T) == typeof(long)) target[i] = (T)(object)BinaryPrimitives.ReadInt64LittleEndian(b);
            else if (typeof(T) == typeof(float)) target[i] = (T)(object)BinaryPrimitives.ReadSingleLittleEndian(b);
            else throw new NotSupportedException(typeof(T).Name);
        }
    }

    public void Dispose() => _lock.Dispose();
}

/// <summary>A fixed-capacity min-heap of the best scores seen so far.</summary>
internal sealed class TopK
{
    private readonly float[] _scores;
    private readonly long[] _ids;
    private int _n;

    public TopK(int k)
    {
        _scores = new float[k];
        _ids = new long[k];
    }

    public int Count => _n;
    public int Capacity => _scores.Length;

    public void TryAdd(float score, long id)
    {
        if (_n < _scores.Length)
        {
            int i = _n++;
            _scores[i] = score;
            _ids[i] = id;
            // sift up
            while (i > 0)
            {
                int p = (i - 1) >> 1;
                if (_scores[p] <= _scores[i]) break;
                Swap(p, i);
                i = p;
            }
            return;
        }
        if (score <= _scores[0]) return;
        _scores[0] = score;
        _ids[0] = id;
        SiftDown(0);
    }

    public void Merge(TopK other)
    {
        for (int i = 0; i < other._n; i++) TryAdd(other._scores[i], other._ids[i]);
    }

    public VectorHit[] ToSortedHits()
    {
        var hits = new VectorHit[_n];
        for (int i = 0; i < _n; i++) hits[i] = new VectorHit(_ids[i], _scores[i]);
        Array.Sort(hits, static (a, b) =>
        {
            int c = b.Score.CompareTo(a.Score);
            return c != 0 ? c : a.Id.CompareTo(b.Id);
        });
        return hits;
    }

    private void SiftDown(int i)
    {
        int n = _n;
        while (true)
        {
            int l = 2 * i + 1, r = l + 1, m = i;
            if (l < n && _scores[l] < _scores[m]) m = l;
            if (r < n && _scores[r] < _scores[m]) m = r;
            if (m == i) return;
            Swap(m, i);
            i = m;
        }
    }

    private void Swap(int a, int b)
    {
        (_scores[a], _scores[b]) = (_scores[b], _scores[a]);
        (_ids[a], _ids[b]) = (_ids[b], _ids[a]);
    }
}
