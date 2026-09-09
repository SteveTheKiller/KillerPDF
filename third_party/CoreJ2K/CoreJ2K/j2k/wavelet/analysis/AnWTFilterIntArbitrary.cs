// Copyright (c) 2026 Sjofn LLC.
// Licensed under the BSD 3-Clause License.

using System;
using CoreJ2K.j2k.codestream;

namespace CoreJ2K.j2k.wavelet.analysis
{
    /// <summary>
    /// Forward wavelet filter for a reversible JPEG 2000 Part 2 arbitrary transformation
    /// kernel (ATK). The lifting steps come from an <see cref="AtkMarkerSegment"/>; the
    /// filter's <see cref="FilterType"/> is the kernel index written to the SPcod/SPcoc
    /// transformation byte.
    /// </summary>
    /// <seealso cref="AtkMarkerSegment" />
    public class AnWTFilterIntArbitrary : AnWTFilterInt
    {
        private readonly AtkMarkerSegment segment;
        private readonly AtkKernel kernel;
        private float[]? lpWaveform;
        private float[]? hpWaveform;

        public AnWTFilterIntArbitrary(AtkMarkerSegment segment)
        {
            this.segment = segment ?? throw new ArgumentNullException(nameof(segment));
            if (!segment.Reversible)
                throw new ArgumentException("AnWTFilterIntArbitrary requires a reversible ATK kernel.");
            kernel = AtkKernel.Compile(segment);
        }

        /// <summary>The ATK marker segment defining this kernel.</summary>
        public AtkMarkerSegment Segment => segment;

        public override int AnLowNegSupport => kernel.Support;
        public override int AnLowPosSupport => kernel.Support;
        public override int AnHighNegSupport => kernel.Support;
        public override int AnHighPosSupport => kernel.Support;
        public override int SynLowNegSupport => kernel.Support;
        public override int SynLowPosSupport => kernel.Support;
        public override int SynHighNegSupport => kernel.Support;
        public override int SynHighPosSupport => kernel.Support;

        public override int ImplType => WaveletFilter_Fields.WT_FILTER_INT_LIFT;

        public override bool Reversible => true;

        public override int FilterType => segment.Index;

        public override void analyze_lpf(int[] inSig, int inOff, int inLen, int inStep, int[] lowSig, int lowOff, int lowStep, int[] highSig, int highOff, int highStep)
        {
            AtkLifting.AnalyzeInt(kernel, inSig, inOff, inLen, inStep, lowSig, lowOff, lowStep, highSig, highOff, highStep, 0);
        }

        public override void analyze_hpf(int[] inSig, int inOff, int inLen, int inStep, int[] lowSig, int lowOff, int lowStep, int[] highSig, int highOff, int highStep)
        {
            AtkLifting.AnalyzeInt(kernel, inSig, inOff, inLen, inStep, lowSig, lowOff, lowStep, highSig, highOff, highStep, 1);
        }

        public override float[] GetLPSynthesisFilter()
        {
            return lpWaveform ??= AtkLifting.SynthesisWaveform(kernel, highPass: false);
        }

        public override float[] GetHPSynthesisFilter()
        {
            return hpWaveform ??= AtkLifting.SynthesisWaveform(kernel, highPass: true);
        }

        public override bool IsSameAsFullWT(int tailOvrlp, int headOvrlp, int inLen)
        {
            return tailOvrlp >= kernel.Support && headOvrlp >= kernel.Support;
        }

        public override bool Equals(object? obj)
        {
            return obj == this
                   || (obj is AnWTFilterIntArbitrary other && segment.StructurallyEquals(other.segment));
        }

        public override int GetHashCode()
        {
            return segment.Index;
        }

        public override string ToString()
        {
            return $"atk{segment.Index} (reversible lifting)";
        }
    }
}
