// Derived from Apache PDFBox JBIG2 ImageIO Plugin and its C# port.
// Modified for the KillerPDF engine.

#nullable disable

using KillerPdf.Engine.Filters.Jbig2;

namespace KillerPdf.Engine.Filters.Jbig2
{
    using System;
    using System.Text;
    using static HuffmanTable;

    /// <summary>
    ///  This class represents an internal node of a Huffman tree. It contains two child nodes.
    /// </summary>
    internal sealed class InternalNode : Node
    {
        private readonly int depth;

        private Node zero;
        private Node one;

        public InternalNode()
        {
            depth = 0;
        }

        public InternalNode(int depth)
        {
            this.depth = depth;
        }

        public void Append(Code c)
        {
            // ignore unused codes
            if (c.PrefixLength == 0)
            {
                return;
            }

            int shift = c.PrefixLength - 1 - depth;

            if (shift < 0)
            {
                throw new ArgumentException("Negative shifting is not possible.");
            }

            int bit = c.Value >> shift & 1;
            if (shift == 0)
            {
                if (c.RangeLength == -1)
                {
                    // the child will be a OutOfBand
                    if (bit == 1)
                    {
                        if (one != null)
                        {
                            throw new InvalidOperationException("already have a OOB for " + c);
                        }

                        one = new OutOfBandNode(c);
                    }
                    else
                    {
                        if (zero != null)
                        {
                            throw new InvalidOperationException("already have a OOB for " + c);
                        }

                        zero = new OutOfBandNode(c);
                    }
                }
                else
                {
                    // the child will be a ValueNode
                    if (bit == 1)
                    {
                        if (one != null)
                        {
                            throw new InvalidOperationException("already have a ValueNode for " + c);
                        }

                        one = new ValueNode(c);
                    }
                    else
                    {
                        if (zero != null)
                        {
                            throw new InvalidOperationException("already have a ValueNode for " + c);
                        }

                        zero = new ValueNode(c);
                    }
                }
            }
            else
            {
                // the child will be an InternalNode
                if (bit == 1)
                {
                    if (one is null)
                    {
                        one = new InternalNode(depth + 1);
                    }

                    ((InternalNode)one).Append(c);
                }
                else
                {
                    if (zero is null)
                    {
                        zero = new InternalNode(depth + 1);
                    }

                    ((InternalNode)zero).Append(c);
                }
            }
        }

        public override long Decode(IImageInputStream iis)
        {
            int b = iis.ReadBit();
            Node n = b == 0 ? zero : one;
            return n.Decode(iis);
        }

        public override string ToString()
        {
            var sb = new StringBuilder("\n");

            Pad(sb);
            sb.Append("0: ").Append(zero).Append("\n");
            Pad(sb);
            sb.Append("1: ").Append(one).Append("\n");

            return sb.ToString();
        }

        private void Pad(StringBuilder sb)
        {
            for (int i = 0; i < depth; i++)
            {
                sb.Append("   ");
            }
        }
    }
}

