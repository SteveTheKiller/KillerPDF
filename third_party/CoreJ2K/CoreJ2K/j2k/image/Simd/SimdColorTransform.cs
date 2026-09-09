// Copyright (c) 2026 Sjofn LLC.
// Licensed under the BSD 3-Clause License.
//
// SIMD-accelerated implementations of the JPEG 2000 Part-1 multi-component
// transforms (RCT and ICT) for both decode and encode hot paths.
//
// The code is structured as row-based primitives operating on Span<T>, so
// callers (InvCompTransf / ForwCompTransf) can dispatch row-by-row regardless
// of the underlying block stride (scanw). The vectorized path uses
// System.Numerics.Vector<T>, which the JIT compiles to the widest available
// SIMD ISA (SSE2/AVX2 on x64, NEON on ARM64). A scalar tail loop preserves
// correctness for non-aligned widths and for runtimes where hardware
// acceleration is unavailable.
//
// IMPORTANT: arithmetic is intentionally identical to the historical scalar
// code so the resulting bitstream / decoded pixel values are bit-exact.

using System;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace CoreJ2K.j2k.image.Simd
{
    internal static class SimdColorTransform
    {
        /// <summary>
        /// True when <see cref="Vector{T}"/> is JIT-compiled to hardware SIMD
        /// on the current runtime. The vectorized routines below remain
        /// correct when this is false; they simply degrade to scalar.
        /// </summary>
        public static bool IsHardwareAccelerated => Vector.IsHardwareAccelerated;

        // ----------------------------------------------------------------
        //  Inverse RCT  (lossless / reversible)
        //      Y, Cb, Cr  -> R, G, B   (all int)
        //
        //      G = Y - ((Cb + Cr) >> 2)
        //      R = Cr + G
        //      B = Cb + G
        // ----------------------------------------------------------------
        public static void InvRctRow(
            ReadOnlySpan<int> y,
            ReadOnlySpan<int> cb,
            ReadOnlySpan<int> cr,
            Span<int> r,
            Span<int> g,
            Span<int> b)
        {
            int n = y.Length;
            if (cb.Length < n || cr.Length < n || r.Length < n || g.Length < n || b.Length < n)
                throw new ArgumentException("InvRctRow: all spans must have at least y.Length elements.");

            int i = 0;
#if NET8_0_OR_GREATER
            if (Vector.IsHardwareAccelerated)
            {
                int step = Vector<int>.Count;
                int last = n - step;
                for (; i <= last; i += step)
                {
                    var vY  = new Vector<int>(y.Slice(i, step));
                    var vCb = new Vector<int>(cb.Slice(i, step));
                    var vCr = new Vector<int>(cr.Slice(i, step));
                    var vG = vY - ((vCb + vCr) >> 2);
                    var vR = vCr + vG;
                    var vB = vCb + vG;
                    vG.CopyTo(g.Slice(i, step));
                    vR.CopyTo(r.Slice(i, step));
                    vB.CopyTo(b.Slice(i, step));
                }
            }
#endif
            // Scalar tail / fallback. Identical arithmetic to InvCompTransf.invRCT.
            for (; i < n; i++)
            {
                int gv = y[i] - ((cb[i] + cr[i]) >> 2);
                g[i] = gv;
                r[i] = cr[i] + gv;
                b[i] = cb[i] + gv;
            }
        }

        // ----------------------------------------------------------------
        //  Inverse ICT  (lossy / irreversible)
        //      Y, Cb, Cr (float)  ->  R, G, B (int, truncated)
        //
        //      R = (int)(Y + 1.402   * Cr + 0.5)
        //      G = (int)(Y - 0.34413 * Cb - 0.71414 * Cr + 0.5)
        //      B = (int)(Y + 1.772   * Cb + 0.5)
        //
        //  The +0.5 followed by (int) truncation toward zero is the exact
        //  rounding used by the legacy scalar code; Vector.ConvertToInt32
        //  also truncates toward zero, so the result matches bit-for-bit.
        // ----------------------------------------------------------------
        public static void InvIctRow(
            ReadOnlySpan<float> y,
            ReadOnlySpan<float> cb,
            ReadOnlySpan<float> cr,
            Span<int> r,
            Span<int> g,
            Span<int> b)
        {
            int n = y.Length;
            if (cb.Length < n || cr.Length < n || r.Length < n || g.Length < n || b.Length < n)
                throw new ArgumentException("InvIctRow: all spans must have at least y.Length elements.");

            int i = 0;
#if NET8_0_OR_GREATER
            if (Vector.IsHardwareAccelerated && Vector<float>.Count == Vector<int>.Count)
            {
                int step = Vector<float>.Count;
                var k_RCr   = new Vector<float>(1.402f);
                var k_GCb   = new Vector<float>(0.34413f);
                var k_GCr   = new Vector<float>(0.71414f);
                var k_BCb   = new Vector<float>(1.772f);
                var k_half  = new Vector<float>(0.5f);

                int last = n - step;
                for (; i <= last; i += step)
                {
                    var vY  = new Vector<float>(y.Slice(i, step));
                    var vCb = new Vector<float>(cb.Slice(i, step));
                    var vCr = new Vector<float>(cr.Slice(i, step));

                    var vR = vY + k_RCr * vCr + k_half;
                    var vG = vY - k_GCb * vCb - k_GCr * vCr + k_half;
                    var vB = vY + k_BCb * vCb + k_half;

                    Vector.ConvertToInt32(vR).CopyTo(r.Slice(i, step));
                    Vector.ConvertToInt32(vG).CopyTo(g.Slice(i, step));
                    Vector.ConvertToInt32(vB).CopyTo(b.Slice(i, step));
                }
            }
#endif
            // Scalar tail / fallback. Identical arithmetic to InvCompTransf.invICT.
            for (; i < n; i++)
            {
                r[i] = (int)(y[i] + 1.402f * cr[i] + 0.5f);
                g[i] = (int)(y[i] - 0.34413f * cb[i] - 0.71414f * cr[i] + 0.5f);
                b[i] = (int)(y[i] + 1.772f * cb[i] + 0.5f);
            }
        }

        // ----------------------------------------------------------------
        //  Forward RCT  (lossless / reversible)
        //      R, G, B  -> Yr, Ur, Vr  (all int)
        //
        //      Yr = (R + 2*G + B) >> 2
        //      Ur = B - G
        //      Vr = R - G
        //
        //  Each output is computed in a single pass over independent inputs.
        // ----------------------------------------------------------------
        public static void ForwRctRow(
            ReadOnlySpan<int> r,
            ReadOnlySpan<int> g,
            ReadOnlySpan<int> b,
            Span<int> yr,
            Span<int> ur,
            Span<int> vr)
        {
            int n = r.Length;
            if (g.Length < n || b.Length < n || yr.Length < n || ur.Length < n || vr.Length < n)
                throw new ArgumentException("ForwRctRow: all spans must have at least r.Length elements.");

            int i = 0;
#if NET8_0_OR_GREATER
            if (Vector.IsHardwareAccelerated)
            {
                int step = Vector<int>.Count;
                int last = n - step;
                for (; i <= last; i += step)
                {
                    var vR = new Vector<int>(r.Slice(i, step));
                    var vG = new Vector<int>(g.Slice(i, step));
                    var vB = new Vector<int>(b.Slice(i, step));

                    var vY = (vR + (vG << 1) + vB) >> 2;
                    (vB - vG).CopyTo(ur.Slice(i, step));
                    (vR - vG).CopyTo(vr.Slice(i, step));
                    vY.CopyTo(yr.Slice(i, step));
                }
            }
#endif
            for (; i < n; i++)
            {
                yr[i] = (r[i] + 2 * g[i] + b[i]) >> 2;
                ur[i] = b[i] - g[i];
                vr[i] = r[i] - g[i];
            }
        }

        // ----------------------------------------------------------------
        //  Forward ICT  (lossy / irreversible)
        //      R, G, B (int)  ->  Y, Cb, Cr (float)
        //
        //      Y  =  0.299   * R + 0.587   * G + 0.114   * B
        //      Cb = -0.16875 * R - 0.33126 * G + 0.5     * B
        //      Cr =  0.5     * R - 0.41869 * G - 0.08131 * B
        // ----------------------------------------------------------------
        public static void ForwIctRow(
            ReadOnlySpan<int> r,
            ReadOnlySpan<int> g,
            ReadOnlySpan<int> b,
            Span<float> yOut,
            Span<float> cbOut,
            Span<float> crOut)
        {
            int n = r.Length;
            if (g.Length < n || b.Length < n || yOut.Length < n || cbOut.Length < n || crOut.Length < n)
                throw new ArgumentException("ForwIctRow: all spans must have at least r.Length elements.");

            int i = 0;
#if NET8_0_OR_GREATER
            if (Vector.IsHardwareAccelerated && Vector<float>.Count == Vector<int>.Count)
            {
                int step = Vector<float>.Count;

                var kYr  = new Vector<float>(0.299f);
                var kYg  = new Vector<float>(0.587f);
                var kYb  = new Vector<float>(0.114f);
                var kCbr = new Vector<float>(-0.16875f);
                var kCbg = new Vector<float>(0.33126f);
                var kCbb = new Vector<float>(0.5f);
                var kCrr = new Vector<float>(0.5f);
                var kCrg = new Vector<float>(0.41869f);
                var kCrb = new Vector<float>(0.08131f);

                int last = n - step;
                for (; i <= last; i += step)
                {
                    var vR = Vector.ConvertToSingle(new Vector<int>(r.Slice(i, step)));
                    var vG = Vector.ConvertToSingle(new Vector<int>(g.Slice(i, step)));
                    var vB = Vector.ConvertToSingle(new Vector<int>(b.Slice(i, step)));

                    (kYr * vR + kYg * vG + kYb * vB).CopyTo(yOut.Slice(i, step));
                    (kCbr * vR - kCbg * vG + kCbb * vB).CopyTo(cbOut.Slice(i, step));
                    (kCrr * vR - kCrg * vG - kCrb * vB).CopyTo(crOut.Slice(i, step));
                }
            }
#endif
            for (; i < n; i++)
            {
                yOut[i]  =   0.299f   * r[i] + 0.587f   * g[i] + 0.114f   * b[i];
                cbOut[i] = (-0.16875f) * r[i] - 0.33126f * g[i] + 0.5f     * b[i];
                crOut[i] =   0.5f     * r[i] - 0.41869f * g[i] - 0.08131f * b[i];
            }
        }
    }
}
