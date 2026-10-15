using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;
using Tessera.Desktop.Search;

namespace Tessera.Api.Search;

/// <summary>
/// A brute-force cosine index over L2-normalised vectors, keyed by content id.
/// </summary>
/// <remarks>
/// <para>
/// The plan's own estimate: 200,000 × 768 floats is 600 MB as fp32 and a
/// brute-force dot product over it is tens of milliseconds with SIMD. This is
/// the trivial implementation the contract asks the core to carry until the
/// search library's own index is merged; it keeps fp32 and does not quantise.
/// </para>
/// <para>
/// One file, one format: <c>TVI1</c>, dims, count, then id + floats per row.
/// Writers take the write lock; searches take the read lock and may run
/// concurrently with each other.
/// </para>
/// </remarks>
public sealed class InMemoryVectorIndex(int dimensions) : IVectorIndex, IDisposable
{
    private static readonly byte[] Magic = "TVI1"u8.ToArray();

    private readonly ReaderWriterLockSlim _lock = new(LockRecursionPolicy.NoRecursion);
    private readonly Dictionary<long, int> _rows = [];
    private readonly List<long> _ids = [];
    private readonly List<float[]> _vectors = [];

    public int Count
    {
        get
        {
            _lock.EnterReadLock();
            try
            {
                return _ids.Count;
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }
    }

    public int Dimensions => dimensions;

    public void Upsert(long id, ReadOnlySpan<float> vector)
    {
        if (vector.Length != dimensions)
        {
            throw new ArgumentException($"Expected {dimensions} dimensions, got {vector.Length}.", nameof(vector));
        }

        var copy = vector.ToArray();
        _lock.EnterWriteLock();
        try
        {
            if (_rows.TryGetValue(id, out var row))
            {
                _vectors[row] = copy;
            }
            else
            {
                _rows[id] = _ids.Count;
                _ids.Add(id);
                _vectors.Add(copy);
            }
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    public bool Remove(long id)
    {
        _lock.EnterWriteLock();
        try
        {
            if (!_rows.Remove(id, out var row))
            {
                return false;
            }

            // Swap the last row into the hole so the lists stay dense.
            var last = _ids.Count - 1;
            if (row != last)
            {
                _ids[row] = _ids[last];
                _vectors[row] = _vectors[last];
                _rows[_ids[row]] = row;
            }

            _ids.RemoveAt(last);
            _vectors.RemoveAt(last);
            return true;
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    public bool Contains(long id)
    {
        _lock.EnterReadLock();
        try
        {
            return _rows.ContainsKey(id);
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    public IReadOnlyList<VectorHit> Search(ReadOnlySpan<float> query, int k, Func<long, bool>? filter = null)
    {
        if (query.Length != dimensions)
        {
            throw new ArgumentException($"Expected {dimensions} dimensions, got {query.Length}.", nameof(query));
        }

        if (k <= 0)
        {
            return [];
        }

        // Smallest score at the top, so the worst of the current best k is
        // what a new candidate has to beat.
        var best = new PriorityQueue<VectorHit, float>(k + 1);

        _lock.EnterReadLock();
        try
        {
            for (var i = 0; i < _ids.Count; i++)
            {
                var id = _ids[i];
                if (filter is not null && !filter(id))
                {
                    continue;
                }

                var score = Dot(query, _vectors[i]);
                if (best.Count < k)
                {
                    best.Enqueue(new VectorHit(id, score), score);
                }
                else if (score > best.Peek().Score)
                {
                    best.EnqueueDequeue(new VectorHit(id, score), score);
                }
            }
        }
        finally
        {
            _lock.ExitReadLock();
        }

        var hits = new VectorHit[best.Count];
        for (var i = hits.Length - 1; i >= 0; i--)
        {
            hits[i] = best.Dequeue();
        }

        return hits;
    }

    public async Task SaveAsync(Stream stream, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(stream);

        long[] ids;
        float[][] vectors;
        _lock.EnterReadLock();
        try
        {
            ids = [.. _ids];
            vectors = [.. _vectors];
        }
        finally
        {
            _lock.ExitReadLock();
        }

        var header = new byte[12];
        Magic.CopyTo(header, 0);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4), dimensions);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8), ids.Length);
        await stream.WriteAsync(header, ct);

        var row = new byte[8 + dimensions * 4];
        for (var i = 0; i < ids.Length; i++)
        {
            BinaryPrimitives.WriteInt64LittleEndian(row, ids[i]);
            MemoryMarshal.AsBytes(vectors[i].AsSpan()).CopyTo(row.AsSpan(8));
            await stream.WriteAsync(row, ct);
        }

        await stream.FlushAsync(ct);
    }

    public async Task LoadAsync(Stream stream, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var header = new byte[12];
        await stream.ReadExactlyAsync(header, ct);
        if (!header.AsSpan(0, 4).SequenceEqual(Magic))
        {
            throw new InvalidDataException("Not a Tessera vector index file.");
        }

        var dims = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4));
        var count = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(8));
        if (dims != dimensions)
        {
            throw new InvalidDataException($"Index has {dims} dimensions; this model has {dimensions}.");
        }

        if (count < 0)
        {
            throw new InvalidDataException("Negative row count.");
        }

        var ids = new List<long>(count);
        var vectors = new List<float[]>(count);
        var row = new byte[8 + dims * 4];
        for (var i = 0; i < count; i++)
        {
            await stream.ReadExactlyAsync(row, ct);
            ids.Add(BinaryPrimitives.ReadInt64LittleEndian(row));
            var v = new float[dims];
            MemoryMarshal.Cast<byte, float>(row.AsSpan(8)).CopyTo(v);
            vectors.Add(v);
        }

        _lock.EnterWriteLock();
        try
        {
            _rows.Clear();
            _ids.Clear();
            _vectors.Clear();
            for (var i = 0; i < ids.Count; i++)
            {
                if (_rows.TryAdd(ids[i], _ids.Count))
                {
                    _ids.Add(ids[i]);
                    _vectors.Add(vectors[i]);
                }
            }
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>Dot product with hardware vectors; cosine, since both sides are unit length.</summary>
    private static float Dot(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        var va = MemoryMarshal.Cast<float, Vector<float>>(a);
        var vb = MemoryMarshal.Cast<float, Vector<float>>(b);
        var acc = Vector<float>.Zero;
        for (var i = 0; i < va.Length; i++)
        {
            acc += va[i] * vb[i];
        }

        var sum = Vector.Sum(acc);
        for (var i = va.Length * Vector<float>.Count; i < a.Length; i++)
        {
            sum += a[i] * b[i];
        }

        return sum;
    }

    public void Dispose() => _lock.Dispose();
}
