// Copyright (c) 2026 Sjofn LLC.
// Licensed under the BSD 3-Clause License.

using System;
using CoreJ2K.j2k.codestream;

namespace CoreJ2K.j2k.wavelet
{
    /// <summary>
    /// Compiled, immutable form of an <see cref="AtkMarkerSegment"/> used by the
    /// arbitrary-kernel wavelet filters at transform time.
    /// </summary>
    internal sealed class AtkKernel
    {
        internal readonly struct Step
        {
            public readonly long[] IntCoeffs;   // reversible lifting coefficients
            public readonly double[] Coeffs;    // real lifting coefficients
            public readonly int Epsilon;
            public readonly long Beta;
            public readonly int Offset;

            public Step(AtkLiftingStep s, bool reversible)
            {
                Coeffs = (double[])s.Coefficients.Clone();
                IntCoeffs = new long[Coeffs.Length];
                if (reversible)
                {
                    for (var k = 0; k < Coeffs.Length; k++) IntCoeffs[k] = (long)Coeffs[k];
                }
                Epsilon = s.Epsilon;
                Beta = s.Beta;
                Offset = s.Offset;
            }
        }

        public readonly bool Reversible;
        public readonly bool FirstStepUpdatesOdd;
        public readonly double LowGain;
        public readonly double HighGain;
        public readonly Step[] Steps;
        public readonly int Support;

        private AtkKernel(AtkMarkerSegment seg)
        {
            Reversible = seg.Reversible;
            FirstStepUpdatesOdd = seg.FirstStepUpdatesOdd;
            LowGain = seg.LowGain;
            HighGain = seg.HighGain;
            Steps = new Step[seg.Steps.Count];
            for (var s = 0; s < Steps.Length; s++) Steps[s] = new Step(seg.Steps[s], Reversible);
            Support = seg.Support;
        }

        public static AtkKernel Compile(AtkMarkerSegment seg)
        {
            if (seg == null) throw new ArgumentNullException(nameof(seg));
            seg.Validate();
            return new AtkKernel(seg);
        }

        /// <summary>The parity (1 = odd) updated by lifting step <paramref name="s"/>.</summary>
        public int StepParity(int s) => (FirstStepUpdatesOdd ? 1 : 0) ^ (s & 1);
    }

    /// <summary>
    /// The generic lifting engine behind the Part 2 arbitrary transformation kernel (ATK)
    /// wavelet filters. Signals are processed in a working buffer with whole-sample
    /// symmetric boundary extension; samples of even global parity form the low-pass
    /// subband and odd samples the high-pass subband, matching the Part 1 filters.
    /// </summary>
    /// <remarks>
    /// The <c>par</c> argument gives the global parity of the first sample of the signal:
    /// 0 for the low-pass-first variants (<c>analyze_lpf</c>/<c>synthetize_lpf</c>) and
    /// 1 for the high-pass-first variants. Length-1 signals follow the Part 1 degenerate
    /// conventions: low-pass passthrough, high-pass Nyquist gain of 2.
    /// </remarks>
    internal static class AtkLifting
    {
        /// <summary>Whole-sample symmetric extension of index <paramref name="i"/> into [0,len).</summary>
        internal static int Mirror(int i, int len)
        {
            if (len == 1) return 0;
            var period = 2 * len - 2;
            i %= period;
            if (i < 0) i += period;
            return i < len ? i : period - i;
        }

        private static void LiftInt(AtkKernel k, int[] work, int par, bool forward)
        {
            var len = work.Length;
            var n = k.Steps.Length;
            for (var si = 0; si < n; si++)
            {
                var s = forward ? si : n - 1 - si;
                var step = k.Steps[s];
                var start = (k.StepParity(s) - par) & 1;
                var coeffs = step.IntCoeffs;
                for (var p = start; p < len; p += 2)
                {
                    long sum = 0;
                    for (var t = 0; t < coeffs.Length; t++)
                        sum += coeffs[t] * work[Mirror(p + 2 * (t - step.Offset) - 1, len)];
                    var delta = (int)((sum + step.Beta) >> step.Epsilon);
                    work[p] = forward ? work[p] + delta : work[p] - delta;
                }
            }
        }

        private static void LiftFloat(AtkKernel k, float[] work, int par, bool forward)
        {
            var len = work.Length;
            var n = k.Steps.Length;
            for (var si = 0; si < n; si++)
            {
                var s = forward ? si : n - 1 - si;
                var step = k.Steps[s];
                var start = (k.StepParity(s) - par) & 1;
                var coeffs = step.Coeffs;
                for (var p = start; p < len; p += 2)
                {
                    double sum = 0;
                    for (var t = 0; t < coeffs.Length; t++)
                        sum += coeffs[t] * work[Mirror(p + 2 * (t - step.Offset) - 1, len)];
                    var delta = (float)sum;
                    work[p] = forward ? work[p] + delta : work[p] - delta;
                }
            }
        }

        public static void AnalyzeInt(
            AtkKernel k, int[] inSig, int inOff, int inLen, int inStep,
            int[] lowSig, int lowOff, int lowStep, int[] highSig, int highOff, int highStep, int par)
        {
            if (inLen == 1)
            {
                if (par == 0) lowSig[lowOff] = inSig[inOff];
                else highSig[highOff] = inSig[inOff] << 1;
                return;
            }

            var work = new int[inLen];
            for (int i = 0, j = inOff; i < inLen; i++, j += inStep) work[i] = inSig[j];

            LiftInt(k, work, par, forward: true);

            int lk = lowOff, hk = highOff;
            for (var p = 0; p < inLen; p++)
            {
                if (((p + par) & 1) == 0) { lowSig[lk] = work[p]; lk += lowStep; }
                else { highSig[hk] = work[p]; hk += highStep; }
            }
        }

        public static void SynthesizeInt(
            AtkKernel k, int[] lowSig, int lowOff, int lowLen, int lowStep,
            int[] highSig, int highOff, int highLen, int highStep, int[] outSig, int outOff, int outStep, int par)
        {
            var outLen = lowLen + highLen;
            if (outLen == 1)
            {
                outSig[outOff] = par == 0 ? lowSig[lowOff] : highSig[highOff] >> 1;
                return;
            }

            var work = new int[outLen];
            int lk = lowOff, hk = highOff;
            for (var p = 0; p < outLen; p++)
            {
                if (((p + par) & 1) == 0) { work[p] = lowSig[lk]; lk += lowStep; }
                else { work[p] = highSig[hk]; hk += highStep; }
            }

            LiftInt(k, work, par, forward: false);

            for (int p = 0, j = outOff; p < outLen; p++, j += outStep) outSig[j] = work[p];
        }

        public static void AnalyzeFloat(
            AtkKernel k, float[] inSig, int inOff, int inLen, int inStep,
            float[] lowSig, int lowOff, int lowStep, float[] highSig, int highOff, int highStep, int par)
        {
            if (inLen == 1)
            {
                if (par == 0) lowSig[lowOff] = inSig[inOff];
                else highSig[highOff] = inSig[inOff] * 2;
                return;
            }

            var work = new float[inLen];
            for (int i = 0, j = inOff; i < inLen; i++, j += inStep) work[i] = inSig[j];

            LiftFloat(k, work, par, forward: true);

            var lg = (float)k.LowGain;
            var hg = (float)k.HighGain;
            int lk = lowOff, hk = highOff;
            for (var p = 0; p < inLen; p++)
            {
                if (((p + par) & 1) == 0) { lowSig[lk] = work[p] * lg; lk += lowStep; }
                else { highSig[hk] = work[p] * hg; hk += highStep; }
            }
        }

        public static void SynthesizeFloat(
            AtkKernel k, float[] lowSig, int lowOff, int lowLen, int lowStep,
            float[] highSig, int highOff, int highLen, int highStep, float[] outSig, int outOff, int outStep, int par)
        {
            var outLen = lowLen + highLen;
            if (outLen == 1)
            {
                outSig[outOff] = par == 0 ? lowSig[lowOff] : highSig[highOff] * 0.5f;
                return;
            }

            var work = new float[outLen];
            var lg = (float)(1.0 / k.LowGain);
            var hg = (float)(1.0 / k.HighGain);
            int lk = lowOff, hk = highOff;
            for (var p = 0; p < outLen; p++)
            {
                if (((p + par) & 1) == 0) { work[p] = lowSig[lk] * lg; lk += lowStep; }
                else { work[p] = highSig[hk] * hg; hk += highStep; }
            }

            LiftFloat(k, work, par, forward: false);

            for (int p = 0, j = outOff; p < outLen; p++, j += outStep) outSig[j] = work[p];
        }

        /// <summary>
        /// Numerically derives the synthesis impulse response (waveform) of the kernel's
        /// low- or high-pass branch, used for the L2-norm energy weights of the rate
        /// allocator. Reversible steps are linearized (the floor and rounding offset are
        /// dropped, coefficients scaled by 2^-E), which is the standard linear
        /// approximation for norm computation.
        /// </summary>
        public static float[] SynthesisWaveform(AtkKernel k, bool highPass)
        {
            // Odd-length buffer, par = 0: even positions are low-pass samples.
            var len = Math.Max(65, 8 * k.Support + 1);
            if (len % 2 == 0) len++;
            var work = new double[len];
            var centre = len / 2; // even index
            if (highPass) centre--; // nearest odd (high-pass) index

            // A subband coefficient of 1 enters synthesis divided by its subband gain.
            var gain = k.Reversible ? 1.0 : (highPass ? k.HighGain : k.LowGain);
            work[centre] = 1.0 / gain;

            // Linearized inverse lifting: steps in reverse order, subtracting.
            var n = k.Steps.Length;
            for (var si = 0; si < n; si++)
            {
                var s = n - 1 - si;
                var step = k.Steps[s];
                var scale = k.Reversible ? Math.Pow(2, -step.Epsilon) : 1.0;
                var start = k.StepParity(s); // par == 0
                for (var p = start; p < len; p += 2)
                {
                    double sum = 0;
                    for (var t = 0; t < step.Coeffs.Length; t++)
                        sum += step.Coeffs[t] * work[Mirror(p + 2 * (t - step.Offset) - 1, len)];
                    work[p] -= sum * scale;
                }
            }

            // Trim to the non-zero span; keep at least the impulse position.
            const double eps = 1e-12;
            int first = centre, last = centre;
            for (var i = 0; i < len; i++)
            {
                if (Math.Abs(work[i]) > eps)
                {
                    if (i < first) first = i;
                    if (i > last) last = i;
                }
            }
            var wave = new float[last - first + 1];
            for (var i = first; i <= last; i++) wave[i - first] = (float)work[i];
            return wave;
        }
    }
}
