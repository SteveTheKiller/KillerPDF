/*
*
* COPYRIGHT:
* 
* This software module was originally developed by Rapha�l Grosbois and
* Diego Santa Cruz (Swiss Federal Institute of Technology-EPFL); Joel
* Askel�f (Ericsson Radio Systems AB); and Bertrand Berthelot, David
* Bouchard, F�lix Henry, Gerard Mozelle and Patrice Onno (Canon Research
* Centre France S.A) in the course of development of the JPEG2000
* standard as specified by ISO/IEC 15444 (JPEG 2000 Standard). This
* software module is an implementation of a part of the JPEG 2000
* Standard. Swiss Federal Institute of Technology-EPFL, Ericsson Radio
* Systems AB and Canon Research Centre France S.A (collectively JJ2000
* Partners) agree not to assert against ISO/IEC and users of the JPEG
* 2000 Standard (Users) any of their rights under the copyright, not
* including other intellectual property rights, for this software module
* with respect to the usage by ISO/IEC and Users of this software module
* or modifications thereof for use in hardware or software products
* claiming conformance to the JPEG 2000 Standard. Those intending to use
* this software module in hardware or software products are advised that
* their use may infringe existing patents. The original developers of
* this software module, JJ2000 Partners and ISO/IEC assume no liability
* for use of this software module or modifications thereof. No license
* or right to this software module is granted for non JPEG 2000 Standard
* conforming products. JJ2000 Partners have full right to use this
* software module for his/her own purpose, assign or donate this
* software module to any third party and to inhibit third parties from
* using this software module for non JPEG 2000 Standard conforming
* products. This copyright notice must be included in all copies or
* derivative works of this software module.
* 
* Copyright (c) 1999/2000 JJ2000 Partners.
* 
* 
* 
*/
using System;
namespace CoreJ2K.j2k.util
{

    /// <summary> This utility class formats messages to the specified line width, by
    /// inserting line-breaks between words, and printing the resulting
    /// lines.
    /// 
    /// </summary>
    public class MsgPrinter
    {
        /// <summary> Returns the line width that is used for formatting.
        /// 
        /// </summary>
        /// <returns> The line width used for formatting
        /// 
        /// 
        /// 
        /// </returns>
        /// <summary> Sets the line width to the specified value. This new value will 
        /// be used in subsequent calls to the print() message.
        /// 
        /// </summary>
        /// <param name="linewidth">The new line width to use (in cahracters)
        /// 
        /// 
        /// 
        /// </param>
        public virtual int LineWidth
        {
            get => lw;

            set
            {
                if (value < 1)
                {
                    throw new ArgumentException();
                }
                lw = value;
            }

        }

        /// <summary>The line width to use </summary>
        public int lw;

        /// <summary>Signals that a newline was found </summary>
        private const int IS_NEWLINE = -2;

        /// <summary>Signals that the end-of-string was reached </summary>
        private const int IS_EOS = -1;

        /// <summary> Creates a new message printer with the specified line width and
        /// with the default locale.
        /// 
        /// </summary>
        /// <param name="linewidth">The line width for which to format (in
        /// characters)
        /// 
        /// 
        /// 
        /// </param>
        public MsgPrinter(int linewidth)
        {
            lw = linewidth;
        }

        /// <summary> Formats the message to print in the current line width, by
        /// breaking the message into lines between words. The number of
        /// spaces to indent the first line is specified by 'flind' and the
        /// number of spaces to indent each of the following lines is
        /// specified by 'ind'. Newlines in 'msg' are respected. A newline is
        /// always printed at the end.
        /// 
        /// </summary>
        /// <param name="out">Where to print the message.
        /// 
        /// </param>
        /// <param name="flind">The indentation for the first line.
        /// 
        /// </param>
        /// <param name="ind">The indentation for the other lines.
        /// 
        /// </param>
        /// <param name="msg">The message to format and print.
        /// 
        /// 
        /// 
        /// </param>
        public virtual void print(System.IO.StreamWriter writer, int flind, int ind, string msg)
        {
            int start, end, pend, efflw, lind, i;

            start = 0;
            end = 0;
            pend = 0;
            efflw = lw - flind;
            lind = flind;
            while ((end = nextLineEnd(msg, pend)) != IS_EOS)
            {
                if (end == IS_NEWLINE)
                {
                    // Forced line break
                    for (i = 0; i < lind; i++)
                    {
                        writer.Write(" ");
                    }
                    writer.WriteLine(msg.Substring(start, (pend) - (start)));
                    if (nextWord(msg, pend) == msg.Length)
                    {
                        // Traling newline => print it and done
                        writer.WriteLine("");
                        start = pend;
                        break;
                    }
                }
                else
                {
                    if (efflw > end - pend)
                    {
                        // Room left on current line
                        efflw -= (end - pend);
                        pend = end;
                        continue;
                    }
                    else
                    {
                        // Filled-up current line => print it
                        for (i = 0; i < lind; i++)
                        {
                            writer.Write(" ");
                        }
                        if (start == pend)
                        {
                            // Word larger than line width
                            // Print anyways
                            writer.WriteLine(msg.Substring(start, (end) - (start)));
                            pend = end;
                        }
                        else
                        {
                            writer.WriteLine(msg.Substring(start, (pend) - (start)));
                        }
                    }
                }
                // Initialize for next line
                lind = ind;
                efflw = lw - ind;
                start = nextWord(msg, pend);
                pend = start;
                if (start == IS_EOS)
                {
                    break; // Did all the string
                }
            }
            if (pend != start)
            {
                // Part of a line left => print it
                for (i = 0; i < lind; i++)
                {
                    writer.Write(" ");
                }
                writer.WriteLine(msg.Substring(start, (pend) - (start)));
            }
        }

        /// <summary> Returns the index of the last character of the next word, plus 1, or
        /// IS_NEWLINE if a newline character is encountered before the next word,
        /// or IS_EOS if the end of the string is ecnounterd before the next
        /// word. The method first skips all whitespace characters at or after
        /// 'from', except newlines. If a newline is found IS_NEWLINE is
        /// returned. Then it skips all non-whitespace characters and returns the
        /// position of the last non-whitespace character, plus 1. The returned
        /// index may be greater than the last valid index in the tsring, but it is 
        /// always suitable to be used in the String.substring() method.
        /// 
        /// Non-whitespace characters are defined as in the
        /// Character.isWhitespace method (that method is used).
        /// 
        /// </summary>
        /// <param name="str">The string to parse
        /// 
        /// </param>
        /// <param name="from">The index of the first position to search from
        /// 
        /// </param>
        /// <returns> The index of the last character in the next word, plus 1,
        /// IS_NEWLINE, or IS_EOS if there are no more words.
        /// 
        /// 
        /// 
        /// </returns>
        private int nextLineEnd(string str, int from)
        {
            var len = str.Length;
            var c = '\x0000';
            // First skip all whitespace, except new line
            while (from < len && (c = str[from]) != '\n' && char.IsWhiteSpace(c))
            {
                from++;
            }
            if (c == '\n')
            {
                return IS_NEWLINE;
            }
            if (from >= len)
            {
                return IS_EOS;
            }
            // Now skip word characters
            while (from < len && !char.IsWhiteSpace(str[from]))
            {
                from++;
            }
            return from;
        }

        /// <summary> Returns the position of the first character in the next word, starting
        /// from 'from', if a newline is encountered first then the index of the
        /// newline character plus 1 is returned. If the end of the string is
        /// encountered then IS_EOS is returned. Words are defined as any
        /// concatenation of 1 or more characters which are not
        /// whitespace. Whitespace characters are those for which
        /// Character.isWhitespace() returns true (that method is used).
        /// 
        /// Non-whitespace characters are defined as in the
        /// Character.isWhitespace method (that method is used).
        /// 
        /// </summary>
        /// <param name="str">The string to parse
        /// 
        /// </param>
        /// <param name="from">The index where to start parsing
        /// 
        /// </param>
        /// <returns> The index of the first character of the next word, or the index 
        /// of the newline plus 1, or IS_EOS.
        /// 
        /// 
        /// 
        /// </returns>
        private int nextWord(string str, int from)
        {
            var len = str.Length;
            var c = '\x0000';
            // First skip all whitespace, but new lines
            while (from < len && (c = str[from]) != '\n' && char.IsWhiteSpace(c))
            {
                from++;
            }
            if (from >= len)
            {
                return IS_EOS;
            }
            else if (c == '\n')
            {
                return from + 1;
            }
            else
            {
                return from;
            }
        }
    }
}