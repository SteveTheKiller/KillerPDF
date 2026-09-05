// Derived from Apache PDFBox JBIG2 ImageIO Plugin and its C# port.
// Modified for the KillerPDF engine.

#nullable disable


namespace KillerPdf.Engine.Filters.Jbig2
{
    using System.Collections.Generic;

    internal static class StandardTables
    {
        private sealed class StandardTable : HuffmanTable
        {
            public StandardTable(int[][] table)
            {
                var codeTable = new List<Code>(); // TODO - Use array

                for (int i = 0; i < table.Length; i++)
                {
                    int prefixLength = table[i][0];
                    int rangeLength = table[i][1];
                    int rangeLow = table[i][2];
                    bool isLowerRange = table[i].Length > 3;

                    codeTable.Add(new Code(prefixLength, rangeLength, rangeLow, isLowerRange));
                }

                InitTree(codeTable);
            }
        }

        // Fourth Value (999) is used for the LowerRange-line
        private static readonly int[][][] TABLES = new[]
        {
            // B1
            new[]
            {
                new[] { 1, 4, 0 }, //
                new[] { 2, 8, 16 }, //
                new[] { 3, 16, 272 }, //
                new[] { 3, 32, 65808 } /* high */
            },
            // B2
            new[]
            {
                new[] { 1, 0, 0 }, //
                new[] { 2, 0, 1 }, //
                new[] { 3, 0, 2 }, //
                new[] { 4, 3, 3 }, //
                new[] { 5, 6, 11 }, //
                new[] { 6, 32, 75 }, /* high */
                new[] { 6, -1, 0 } /* OOB */
            },
            // B3
            new[]
            {
                new[] { 8, 8, -256 }, //
                new[] { 1, 0, 0 }, //
                new[] { 2, 0, 1 }, //
                new[] { 3, 0, 2 }, //
                new[] { 4, 3, 3 }, //
                new[] { 5, 6, 11 }, //
                new[] { 8, 32, -257, 999 }, /* low */
                new[] { 7, 32, 75 }, /* high */
                new[] { 6, -1, 0 } /* OOB */
            },
            // B4
            new[]
            {
                new[] { 1, 0, 1 }, //
                new[] { 2, 0, 2 }, //
                new[] { 3, 0, 3 }, //
                new[] { 4, 3, 4 }, //
                new[] { 5, 6, 12 }, //
                new[] { 5, 32, 76 } /* high */
            },
            // B5
            new[]
            {
                new[] { 7, 8, -255 }, //
                new[] { 1, 0, 1 }, //
                new[] { 2, 0, 2 }, //
                new[] { 3, 0, 3 }, //
                new[] { 4, 3, 4 }, //
                new[] { 5, 6, 12 }, //
                new[] { 7, 32, -256, 999 }, /* low */
                new[] { 6, 32, 76 } /* high */
            },
            // B6
            new[]
            {
                new[] { 5, 10, -2048 }, //
                new[] { 4, 9, -1024 }, //
                new[] { 4, 8, -512 }, //
                new[] { 4, 7, -256 }, //
                new[] { 5, 6, -128 }, //
                new[] { 5, 5, -64 }, //
                new[] { 4, 5, -32 }, //
                new[] { 2, 7, 0 }, //
                new[] { 3, 7, 128 }, //
                new[] { 3, 8, 256 }, //
                new[] { 4, 9, 512 }, //
                new[] { 4, 10, 1024 }, //
                new[] { 6, 32, -2049, 999 }, /* low */
                new[] { 6, 32, 2048 } /* high */
            },
            // B7
            new[]
            {
                new[] { 4, 9, -1024 }, //
                new[] { 3, 8, -512 }, //
                new[] { 4, 7, -256 }, //
                new[] { 5, 6, -128 }, //
                new[] { 5, 5, -64 }, //
                new[] { 4, 5, -32 }, //
                new[] { 4, 5, 0 }, //
                new[] { 5, 5, 32 }, //
                new[] { 5, 6, 64 }, //
                new[] { 4, 7, 128 }, //
                new[] { 3, 8, 256 }, //
                new[] { 3, 9, 512 }, //
                new[] { 3, 10, 1024 }, //
                new[] { 5, 32, -1025, 999 }, /* low */
                new[] { 5, 32, 2048 } /* high */
            },
            // B8
            new[]
            {
                new[] { 8, 3, -15 }, //
                new[] { 9, 1, -7 }, //
                new[] { 8, 1, -5 }, //
                new[] { 9, 0, -3 }, //
                new[] { 7, 0, -2 }, //
                new[] { 4, 0, -1 }, //
                new[] { 2, 1, 0 }, //
                new[] { 5, 0, 2 }, //
                new[] { 6, 0, 3 }, //
                new[] { 3, 4, 4 }, //
                new[] { 6, 1, 20 }, //
                new[] { 4, 4, 22 }, //
                new[] { 4, 5, 38 }, //
                new[] { 5, 6, 70 }, //
                new[] { 5, 7, 134 }, //
                new[] { 6, 7, 262 }, //
                new[] { 7, 8, 390 }, //
                new[] { 6, 10, 646 }, //
                new[] { 9, 32, -16, 999 }, /* low */
                new[] { 9, 32, 1670 }, /* high */
                new[] { 2, -1, 0 } /* OOB */
            },
            // B9
            new[]
            {
                new[] { 8, 4, -31 }, //
                new[] { 9, 2, -15 }, //
                new[] { 8, 2, -11 }, //
                new[] { 9, 1, -7 }, //
                new[] { 7, 1, -5 }, //
                new[] { 4, 1, -3 }, //
                new[] { 3, 1, -1 }, //
                new[] { 3, 1, 1 }, //
                new[] { 5, 1, 3 }, //
                new[] { 6, 1, 5 }, //
                new[] { 3, 5, 7 }, //
                new[] { 6, 2, 39 }, //
                new[] { 4, 5, 43 }, //
                new[] { 4, 6, 75 }, //
                new[] { 5, 7, 139 }, //
                new[] { 5, 8, 267 }, //
                new[] { 6, 8, 523 }, //
                new[] { 7, 9, 779 }, //
                new[] { 6, 11, 1291 }, //
                new[] { 9, 32, -32, 999 }, /* low */
                new[] { 9, 32, 3339 }, /* high */
                new[] { 2, -1, 0 } /* OOB */
            },
            // B10
            new[]
            {
                new[] { 7, 4, -21 }, //
                new[] { 8, 0, -5 }, //
                new[] { 7, 0, -4 }, //
                new[] { 5, 0, -3 }, //
                new[] { 2, 2, -2 }, //
                new[] { 5, 0, 2 }, //
                new[] { 6, 0, 3 }, //
                new[] { 7, 0, 4 }, //
                new[] { 8, 0, 5 }, //
                new[] { 2, 6, 6 }, //
                new[] { 5, 5, 70 }, //
                new[] { 6, 5, 102 }, //
                new[] { 6, 6, 134 }, //
                new[] { 6, 7, 198 }, //
                new[] { 6, 8, 326 }, //
                new[] { 6, 9, 582 }, //
                new[] { 6, 10, 1094 }, //
                new[] { 7, 11, 2118 }, //
                new[] { 8, 32, -22, 999 }, /* low */
                new[] { 8, 32, 4166 }, /* high */
                new[] { 2, -1, 0 } /* OOB */
            },
            // B11
            new[]
            {
                new[] { 1, 0, 1 }, //
                new[] { 2, 1, 2 }, //
                new[] { 4, 0, 4 }, //
                new[] { 4, 1, 5 }, //
                new[] { 5, 1, 7 }, //
                new[] { 5, 2, 9 }, //
                new[] { 6, 2, 13 }, //
                new[] { 7, 2, 17 }, //
                new[] { 7, 3, 21 }, //
                new[] { 7, 4, 29 }, //
                new[] { 7, 5, 45 }, //
                new[] { 7, 6, 77 }, //
                new[] { 7, 32, 141 } /* high */
            },
            // B12
            new[]
            {
                new[] { 1, 0, 1 }, //
                new[] { 2, 0, 2 }, //
                new[] { 3, 1, 3 }, //
                new[] { 5, 0, 5 }, //
                new[] { 5, 1, 6 }, //
                new[] { 6, 1, 8 }, //
                new[] { 7, 0, 10 }, //
                new[] { 7, 1, 11 }, //
                new[] { 7, 2, 13 }, //
                new[] { 7, 3, 17 }, //
                new[] { 7, 4, 25 }, //
                new[] { 8, 5, 41 }, //
                new[] { 8, 32, 73 } //
            },
            // B13
            new[]
            {
                new[] { 1, 0, 1 }, //
                new[] { 3, 0, 2 }, //
                new[] { 4, 0, 3 }, //
                new[] { 5, 0, 4 }, //
                new[] { 4, 1, 5 }, //
                new[] { 3, 3, 7 }, //
                new[] { 6, 1, 15 }, //
                new[] { 6, 2, 17 }, //
                new[] { 6, 3, 21 }, //
                new[] { 6, 4, 29 }, //
                new[] { 6, 5, 45 }, //
                new[] { 7, 6, 77 }, //
                new[] { 7, 32, 141 } /* high */
            },
            // B14
            new[]
            {
                new[] { 3, 0, -2 }, //
                new[] { 3, 0, -1 }, //
                new[] { 1, 0, 0 }, //
                new[] { 3, 0, 1 }, //
                new[] { 3, 0, 2 } //
            },
            // B15
            new[]
            {
                new[] { 7, 4, -24 }, //
                new[] { 6, 2, -8 }, //
                new[] { 5, 1, -4 }, //
                new[] { 4, 0, -2 }, //
                new[] { 3, 0, -1 }, //
                new[] { 1, 0, 0 }, //
                new[] { 3, 0, 1 }, //
                new[] { 4, 0, 2 }, //
                new[] { 5, 1, 3 }, //
                new[] { 6, 2, 5 }, //
                new[] { 7, 4, 9 }, //
                new[] { 7, 32, -25, 999 }, /* low */
                new[] { 7, 32, 25 } /* high */
            }
        };

        private static readonly HuffmanTable[] STANDARD_TABLES = new HuffmanTable[TABLES.Length];

        public static HuffmanTable GetTable(int number)
        {
            HuffmanTable table = STANDARD_TABLES[number - 1];
            if (table is null)
            {
                table = new StandardTable(TABLES[number - 1]);
                STANDARD_TABLES[number - 1] = table;
            }

            return table;
        }
    }
}

