using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Tessera.Desktop.Search;

/// <summary>
/// Dot product of a float query with an int8 row, accumulated in float.
/// AVX2+FMA widens eight bytes at a time; the portable path widens sixteen
/// bytes through <see cref="Vector128"/> and runs on NEON too.
/// </summary>
internal static class Int8Dot
{
    public static float Dot(ReadOnlySpan<float> q, ReadOnlySpan<sbyte> v)
    {
        if (q.Length != v.Length) throw new ArgumentException("Length mismatch.");
        if (Avx2.IsSupported && Fma.IsSupported && q.Length >= 16) return DotAvx2(q, v);
        if (Vector128.IsHardwareAccelerated && q.Length >= 16) return DotVector128(q, v);
        return DotScalar(q, v);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static float DotScalar(ReadOnlySpan<float> q, ReadOnlySpan<sbyte> v)
    {
        float sum = 0f;
        for (int i = 0; i < q.Length; i++) sum += q[i] * v[i];
        return sum;
    }

    internal static unsafe float DotAvx2(ReadOnlySpan<float> q, ReadOnlySpan<sbyte> v)
    {
        int n = q.Length;
        var acc0 = Vector256<float>.Zero;
        var acc1 = Vector256<float>.Zero;
        int i = 0;
        fixed (float* pq = q)
        fixed (sbyte* pv = v)
        {
            for (; i <= n - 16; i += 16)
            {
                var a = Avx.ConvertToVector256Single(Avx2.ConvertToVector256Int32(pv + i));
                var b = Avx.ConvertToVector256Single(Avx2.ConvertToVector256Int32(pv + i + 8));
                acc0 = Fma.MultiplyAdd(a, Avx.LoadVector256(pq + i), acc0);
                acc1 = Fma.MultiplyAdd(b, Avx.LoadVector256(pq + i + 8), acc1);
            }
        }
        float sum = Vector256.Sum(acc0 + acc1);
        for (; i < n; i++) sum += q[i] * v[i];
        return sum;
    }

    internal static unsafe float DotVector128(ReadOnlySpan<float> q, ReadOnlySpan<sbyte> v)
    {
        int n = q.Length;
        var acc0 = Vector128<float>.Zero;
        var acc1 = Vector128<float>.Zero;
        int i = 0;
        fixed (float* pq = q)
        fixed (sbyte* pv = v)
        {
            for (; i <= n - 16; i += 16)
            {
                var bytes = Vector128.Load(pv + i);
                var (lo16, hi16) = Vector128.Widen(bytes);
                var (a, b) = Vector128.Widen(lo16);
                var (c, d) = Vector128.Widen(hi16);
                acc0 += Vector128.ConvertToSingle(a) * Vector128.Load(pq + i);
                acc1 += Vector128.ConvertToSingle(b) * Vector128.Load(pq + i + 4);
                acc0 += Vector128.ConvertToSingle(c) * Vector128.Load(pq + i + 8);
                acc1 += Vector128.ConvertToSingle(d) * Vector128.Load(pq + i + 12);
            }
        }
        float sum = Vector128.Sum(acc0 + acc1);
        for (; i < n; i++) sum += q[i] * v[i];
        return sum;
    }
}
