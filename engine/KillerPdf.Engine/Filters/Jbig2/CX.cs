// Derived from Apache PDFBox JBIG2 ImageIO Plugin and its C# port.
// Modified for the KillerPDF engine.

#nullable disable

namespace KillerPdf.Engine.Filters.Jbig2
{
    /// <summary>
    /// CX represents the context used by arithmetic decoding and arithmetic integer decoding. It selects the probability
    /// estimate and statistics used during decoding procedure.
    /// </summary>
    internal sealed class CX
    {
        private readonly byte[] cx;

        public int Index { get; set; }

        public int Cx
        {
            get => cx[Index] & 0x7f;
            set => cx[Index] = (byte)((cx[Index] & 0x80) | (value & 0x7f));
        }

        /// <summary>
        /// Returns the decision. Possible values are 0 or 1.
        /// </summary>
        public byte Mps => (byte)(cx[Index] >> 7);

        /// <summary>
        /// Creates a new <see cref="CX"/> instance
        /// </summary>
        /// <param name="size">Number of context values</param>
        /// <param name="index">Start index</param>
        public CX(int size, int index)
        {
            Index = index;
            cx = new byte[size];
        }

        private CX(byte[] cx, int index)
        {
            this.cx = cx;
            Index = index;
        }

        public CX Copy()
        {
            return new CX((byte[])cx.Clone(), Index);
        }

        /// <summary>
        /// Flips the predicted bit while preserving the probability state.
        /// </summary>
        public void ToggleMps()
        {
            cx[Index] ^= 0x80;
        }
    }
}
