// Copyright (c) 2026 Sjofn LLC.
// Licensed under the BSD 3-Clause License.

using System;
using CoreJ2K.j2k.codestream;

namespace CoreJ2K.j2k.wavelet.analysis
{
    /// <summary>
    /// Forward wavelet filter for an irreversible JPEG 2000 Part 2 arbitrary
    /// transformation kernel (ATK). The lifting steps and subband gains come from an
    /// <see cref="AtkMarkerSegment"/>; the filter's <see cref="FilterType"/> is the
    /// kernel index written to the SPcod/SPcoc transformation byte.
    /// </summary>
    /// <seealso cref="AtkMarkerSegment" />
    public class AnWTFilterFloatArbitrary : AnWTFilterFloat
    {
        private readonly AtkMarkerSegment segment;
        private readonly AtkKernel kernel;
        private float[]? lpWaveform;
        private float[]? hpWaveform;

        public AnWTFilterFloatArbitrary(AtkMarkerSegment segment)
        {
            this.segment = segment ?? throw new ArgumentNullException(nameof(segment));
            if (segment.Reversible)
                throw new ArgumentException("AnWTFilterFloatArbitrary requires an irreversible ATK kernel.");
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

        public override int ImplType => WaveletFilter_Fields.WT_FILTER_FLOAT_LIFT;

        public override bool Reversible => false;

        public override int FilterType => segment.Index;

        public override void analyze_lpf(float[] inSig, int inOff, int inLen, int inStep, float[] lowSig, int lowOff, int lowStep, float[] highSig, int highOff, int highStep)
        {
            AtkLifting.AnalyzeFloat(kernel, inSig, inOff, inLen, inStep, lowSig, lowOff, lowStep, highSig, highOff, highStep, 0);
        }

        public override void analyze_hpf(float[] inSig, int inOff, int inLen, int inStep, float[] lowSig, int lowOff, int lowStep, float[] highSig, int highOff, int highStep)
        {
            AtkLifting.AnalyzeFloat(kernel, inSig, inOff, inLen, inStep, lowSig, lowOff, lowStep, highSig, highOff, highStep, 1);
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
                   || (obj is AnWTFilterFloatArbitrary other && segment.StructurallyEquals(other.segment));
        }

        public override int GetHashCode()
        {
            return segment.Index;
        }

        public override string ToString()
        {
            return $"atk{segment.Index} (irreversible lifting)";
        }
    }
}
