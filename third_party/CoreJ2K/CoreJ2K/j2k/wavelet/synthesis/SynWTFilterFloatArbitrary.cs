// Copyright (c) 2026 Sjofn LLC.
// Licensed under the BSD 3-Clause License.

using System;
using CoreJ2K.j2k.codestream;

namespace CoreJ2K.j2k.wavelet.synthesis
{
    /// <summary>
    /// Inverse wavelet filter for an irreversible JPEG 2000 Part 2 arbitrary
    /// transformation kernel (ATK), undoing the subband gains and lifting steps of the
    /// matching analysis filter.
    /// </summary>
    /// <seealso cref="AtkMarkerSegment" />
    public class SynWTFilterFloatArbitrary : SynWTFilterFloat
    {
        private readonly AtkMarkerSegment segment;
        private readonly AtkKernel kernel;

        public SynWTFilterFloatArbitrary(AtkMarkerSegment segment)
        {
            this.segment = segment ?? throw new ArgumentNullException(nameof(segment));
            if (segment.Reversible)
                throw new ArgumentException("SynWTFilterFloatArbitrary requires an irreversible ATK kernel.");
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

        public override void synthetize_lpf(float[] lowSig, int lowOff, int lowLen, int lowStep, float[] highSig, int highOff, int highLen, int highStep, float[] outSig, int outOff, int outStep)
        {
            AtkLifting.SynthesizeFloat(kernel, lowSig, lowOff, lowLen, lowStep, highSig, highOff, highLen, highStep, outSig, outOff, outStep, 0);
        }

        public override void synthetize_hpf(float[] lowSig, int lowOff, int lowLen, int lowStep, float[] highSig, int highOff, int highLen, int highStep, float[] outSig, int outOff, int outStep)
        {
            AtkLifting.SynthesizeFloat(kernel, lowSig, lowOff, lowLen, lowStep, highSig, highOff, highLen, highStep, outSig, outOff, outStep, 1);
        }

        public override bool IsSameAsFullWT(int tailOvrlp, int headOvrlp, int inLen)
        {
            return tailOvrlp >= kernel.Support && headOvrlp >= kernel.Support;
        }

        public override string ToString()
        {
            return $"atk{segment.Index} (irreversible lifting)";
        }
    }
}
