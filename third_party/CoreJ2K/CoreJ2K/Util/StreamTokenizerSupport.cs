// Copyright (c) 2007-2016 CSJ2K contributors.
namespace CoreJ2K.Util
{
    using System;
    using System.IO;
    using System.Text;

    /// </summary>
    internal class BackStringReader : System.IO.StringReader
    {
        private readonly char[] buffer;
        private int position = 1;

        /// <summary>
        /// Constructor. Calls the base constructor.
        /// </summary>
        /// <param name="s">The buffer from which chars will be read.</param>
        public BackStringReader(string s) : base(s)
        {
            buffer = new char[position];
        }


        /// <summary>
        /// Reads a character.
        /// </summary>
        /// <returns>The character read.</returns>
        public override int Read()
        {
            if (position >= 0 && position < buffer.Length)
                return buffer[position++];
            return base.Read();
        }

        /// <summary>
        /// Reads an amount of characters from the buffer and copies the values to the array passed.
        /// </summary>
        /// <param name="array">Array where the characters will be stored.</param>
        /// <param name="index">The beginning index to read.</param>
        /// <param name="count">The number of characters to read.</param>
        /// <returns>The number of characters read.</returns>
        public override int Read(char[] array, int index, int count)
        {
            var readFromBuffer = 0;

            if (count <= 0)
                return 0;

            var available = buffer.Length - position;
            if (available > 0)
            {
                var toCopy = Math.Min(count, available);
                Array.Copy(buffer, position, array, index, toCopy);
                position += toCopy;
                index += toCopy;
                count -= toCopy;
                readFromBuffer = toCopy;
            }

            if (count > 0)
            {
                // base.Read returns number of chars read (0 at EOF)
                var n = base.Read(array, index, count);
                if (n > 0)
                    return readFromBuffer + n;
                // nothing more read from base, return whatever we read from buffer (may be 0)
                return readFromBuffer;
            }

            return readFromBuffer;
        }

        /// <summary>
        /// Unreads a character.
        /// </summary>
        /// <param name="unReadChar">The character to be unread.</param>
        public void UnRead(int unReadChar)
        {
            position--;
            buffer[position] = (char)unReadChar;
        }

        /// <summary>
        /// Unreads an amount of characters by moving these to the buffer.
        /// </summary>
        /// <param name="array">The character array to be unread.</param>
        /// <param name="index">The beginning index to unread.</param>
        /// <param name="count">The number of characters to unread.</param>
        public void UnRead(char[] array, int index, int count)
        {
            Move(array, index, count);
        }

        /// <summary>
        /// Unreads an amount of characters by moving these to the buffer.
        /// </summary>
        /// <param name="array">The character array to be unread.</param>
        public void UnRead(char[] array)
        {
            Move(array, 0, array.Length - 1);
        }

        /// <summary>
        /// Moves the array of characters to the buffer.
        /// </summary>
        /// <param name="array">Array of characters to move.</param>
        /// <param name="index">Offset of the beginning.</param>
        /// <param name="count">Amount of characters to move.</param>
        private void Move(char[] array, int index, int count)
        {
            for (var arrayPosition = index + count; arrayPosition >= index; arrayPosition--)
                UnRead(array[arrayPosition]);
        }
    }

    /*******************************/

    /// <summary>
    /// The StreamTokenizerSupport class takes an input stream and parses it into "tokens".
    /// The stream tokenizer can recognize identifiers, numbers, quoted strings, and various comment styles. 
    /// </summary>
    public class StreamTokenizerSupport
    {

        /// <summary>
        /// Internal constants and fields
        /// </summary>

        private const string TOKEN = "Token[";
        private const string NOTHING = "NOTHING";
        private const string NUMBER = "number=";
        private const string EOF = "EOF";
        private const string EOL = "EOL";
        private const string QUOTED = "quoted string=";
        private const string LINE = "], Line ";
        private const string DASH = "-.";
        private const string DOT = ".";

        private const int TT_NOTHING = -4;

        private const sbyte ORDINARYCHAR = 0x00;
        private const sbyte WORDCHAR = 0x01;
        private const sbyte WHITESPACECHAR = 0x02;
        private const sbyte COMMENTCHAR = 0x04;
        private const sbyte QUOTECHAR = 0x08;
        private const sbyte NUMBERCHAR = 0x10;

        private const int STATE_NEUTRAL = 0;
        private const int STATE_WORD = 1;
        private const int STATE_NUMBER1 = 2;
        private const int STATE_NUMBER2 = 3;
        private const int STATE_NUMBER3 = 4;
        private const int STATE_NUMBER4 = 5;
        private const int STATE_STRING = 6;
        private const int STATE_LINECOMMENT = 7;
        private const int STATE_DONE_ON_EOL = 8;

        private const int STATE_PROCEED_ON_EOL = 9;
        private const int STATE_POSSIBLEC_COMMENT = 10;
        private const int STATE_POSSIBLEC_COMMENT_END = 11;
        private const int STATE_C_COMMENT = 12;
        private const int STATE_STRING_ESCAPE_SEQ = 13;
        private const int STATE_STRING_ESCAPE_SEQ_OCTAL = 14;

        private const int STATE_DONE = 100;

        private readonly sbyte[] attribute = new sbyte[256];
        private bool eolIsSignificant = false;
        private bool slashStarComments = false;
        private bool slashSlashComments = false;
        private bool lowerCaseMode = false;
        private bool pushedback = false;
        private int lineno = 1;

        private readonly BackReader? inReader;
        private readonly BackStringReader? inStringReader;
        private readonly BackInputStream? inStream;
        private System.Text.StringBuilder buf = null!;


        /// <summary>
        /// Indicates that the end of the stream has been read.
        /// </summary>
        public const int TT_EOF = -1;

        /// <summary>
        /// Indicates that the end of the line has been read.
        /// </summary>
        public const int TT_EOL = '\n';

        /// <summary>
        /// Indicates that a number token has been read.
        /// </summary>
        public const int TT_NUMBER = -2;

        /// <summary>
        /// Indicates that a word token has been read.
        /// </summary>
        public const int TT_WORD = -3;

        /// <summary>
        /// If the current token is a number, this field contains the value of that number.
        /// </summary>
        public double nval;

        /// <summary>
        /// If the current token is a word token, this field contains a string giving the characters of the word 
        /// token.
        /// </summary>
        public string? sval;

        /// <summary>
        /// After a call to the nextToken method, this field contains the type of the token just read.
        /// </summary>
        public int ttype;


        /// <summary>
        /// Internal methods
        /// </summary>

        private int read()
        {
            if (inReader != null)
                return inReader.Read();
            else if (inStream != null)
                return inStream.Read();
            else
                return inStringReader!.Read();
        }

        private void unread(int ch)
        {
            if (inReader != null)
                inReader.UnRead(ch);
            else if (inStream != null)
                inStream.UnRead(ch);
            else
                inStringReader!.UnRead(ch);
        }

        private void init()
        {
            buf = new System.Text.StringBuilder();
            ttype = TT_NOTHING;

            WordChars('A', 'Z');
            WordChars('a', 'z');
            WordChars(160, 255);
            WhitespaceChars(0x00, 0x20);
            CommentChar('/');
            QuoteChar('\'');
            QuoteChar('"');
            ParseNumbers();
        }

        private void SetAttributes(int low, int hi, sbyte attrib)
        {
            var l = Math.Max(0, low);
            var h = Math.Min(255, hi);
            for (var i = l; i <= h; i++)
                attribute[i] = attrib;
        }

        private bool IsWordChar(int data)
        {
            var ch = (char)data;
            return (data != -1 && (ch > 255 || attribute[ch] == WORDCHAR || attribute[ch] == NUMBERCHAR));
        }

        /// <summary>
        /// Creates a StreamTokenizerSupport that parses the given string.
        /// </summary>
        /// <param name="reader">The System.IO.StringReader that contains the String to be parsed.</param>
        public StreamTokenizerSupport(System.IO.StringReader reader)
        {
            // Use ReadToEnd to avoid per-char concatenation
            var s = reader.ReadToEnd();
            reader.Dispose();
            inStringReader = new BackStringReader(s);
            init();
        }

        /// <summary>
        /// Creates a StreamTokenizerSupport that parses the given stream.
        /// </summary>
        /// <param name="reader">Reader to be parsed.</param>
        public StreamTokenizerSupport(System.IO.StreamReader reader)
        {
            inReader = new BackReader(new System.IO.StreamReader(reader.BaseStream, reader.CurrentEncoding).BaseStream, 2, reader.CurrentEncoding);
            init();
        }

        /// <summary>
        /// Creates a StreamTokenizerSupport that parses the given stream.
        /// </summary>
        /// <param name="stream">Stream to be parsed.</param>
        public StreamTokenizerSupport(System.IO.Stream stream)
        {
            inStream = new BackInputStream(stream, 2);
            init();
        }

        /// <summary>
        /// Specified that the character argument starts a single-line comment.
        /// </summary>
        /// <param name="ch">The character.</param>
        public virtual void CommentChar(int ch)
        {
            if (ch >= 0 && ch <= 255)
                attribute[ch] = COMMENTCHAR;
        }

        /// <summary>
        /// Determines whether ends of line are treated as tokens.
        /// </summary>
        /// <param name="flag">True indicates that end-of-line characters are separate tokens; False indicates 
        /// that end-of-line characters are white space.</param>
        public virtual void EOLIsSignificant(bool flag)
        {
            eolIsSignificant = flag;
        }

        /// <summary>
        /// Return the current line number.
        /// </summary>
        /// <returns>Current line number</returns>
        public virtual int LineNo()
        {
            return lineno;
        }

        /// <summary>
        /// Determines whether word token are automatically lowercased.
        /// </summary>
        /// <param name="flag">True indicates that all word tokens should be lowercased.</param>
        public virtual void LowerCaseMode(bool flag)
        {
            lowerCaseMode = flag;
        }

        /// <summary>
        /// Parses the next token from the input stream of this tokenizer.
        /// </summary>
        /// <returns>The value of the ttype field.</returns>
        public virtual int NextToken()
        {
            var prevChar = (char)(0);
            var ch = (char)(0);
            var qChar = (char)(0);
            var octalNumber = 0;
            int state;

            if (pushedback)
            {
                pushedback = false;
                return ttype;
            }

            ttype = TT_NOTHING;
            state = STATE_NEUTRAL;
            nval = 0.0;
            sval = null;
            buf.Length = 0;

            do
            {
                var data = read();
                prevChar = ch;
                ch = (char)data;

                switch (state)
                {
                    case STATE_NEUTRAL:
                        {
                            if (data == -1)
                            {
                                ttype = TT_EOF;
                                state = STATE_DONE;
                            }
                            else if (ch > 255)
                            {
                                buf.Append(ch);
                                ttype = TT_WORD;
                                state = STATE_WORD;
                            }
                            else if (attribute[ch] == COMMENTCHAR)
                            {
                                state = STATE_LINECOMMENT;
                            }
                            else if (attribute[ch] == WORDCHAR)
                            {
                                buf.Append(ch);
                                ttype = TT_WORD;
                                state = STATE_WORD;
                            }
                            else if (attribute[ch] == NUMBERCHAR)
                            {
                                ttype = TT_NUMBER;
                                buf.Append(ch);
                                if (ch == '-')
                                    state = STATE_NUMBER1;
                                else if (ch == '.')
                                    state = STATE_NUMBER3;
                                else
                                    state = STATE_NUMBER2;
                            }
                            else if (attribute[ch] == QUOTECHAR)
                            {
                                qChar = ch;
                                ttype = ch;
                                state = STATE_STRING;
                            }
                            else if ((slashSlashComments || slashStarComments) && ch == '/')
                                state = STATE_POSSIBLEC_COMMENT;
                            else if (attribute[ch] == ORDINARYCHAR)
                            {
                                ttype = ch;
                                state = STATE_DONE;
                            }
                            else if (ch == '\n' || ch == '\r')
                            {
                                lineno++;
                                if (eolIsSignificant)
                                {
                                    ttype = TT_EOL;
                                    if (ch == '\n')
                                        state = STATE_DONE;
                                    else if (ch == '\r')
                                        state = STATE_DONE_ON_EOL;
                                }
                                else if (ch == '\r')
                                    state = STATE_PROCEED_ON_EOL;
                            }
                            break;
                        }
                    case STATE_WORD:
                        {
                            if (IsWordChar(data))
                                buf.Append(ch);
                            else
                            {
                                if (data != -1)
                                    unread(ch);
                                sval = buf.ToString();
                                state = STATE_DONE;
                            }
                            break;
                        }
                    case STATE_NUMBER1:
                        {
                            if (data == -1 || attribute[ch] != NUMBERCHAR || ch == '-')
                            {
                                if (attribute[ch] == COMMENTCHAR && char.IsNumber(ch))
                                {
                                    buf.Append(ch);
                                    state = STATE_NUMBER2;
                                }
                                else
                                {
                                    if (data != -1)
                                        unread(ch);
                                    ttype = '-';
                                    state = STATE_DONE;
                                }
                            }
                            else
                            {
                                buf.Append(ch);
                                state = ch == '.' ? STATE_NUMBER3 : STATE_NUMBER2;
                            }
                            break;
                        }
                    case STATE_NUMBER2:
                        {
                            if (data == -1 || attribute[ch] != NUMBERCHAR || ch == '-')
                            {
                                if (char.IsNumber(ch) && attribute[ch] == WORDCHAR)
                                {
                                    buf.Append(ch);
                                }
                                else if (ch == '.' && attribute[ch] == WHITESPACECHAR)
                                {
                                    buf.Append(ch);
                                }

                                else if ((data != -1) && (attribute[ch] == COMMENTCHAR && char.IsNumber(ch)))
                                {
                                    buf.Append(ch);
                                }
                                else
                                {
                                    if (data != -1)
                                        unread(ch);
                                    try
                                    {
                                        nval = double.Parse(buf.ToString());
                                    }
                                    catch (FormatException) { }
                                    state = STATE_DONE;
                                }
                            }
                            else
                            {
                                buf.Append(ch);
                                if (ch == '.')
                                    state = STATE_NUMBER3;
                            }
                            break;
                        }
                    case STATE_NUMBER3:
                        {
                            if (data == -1 || attribute[ch] != NUMBERCHAR || ch == '-' || ch == '.')
                            {
                                if (attribute[ch] == COMMENTCHAR && char.IsNumber(ch))
                                {
                                    buf.Append(ch);
                                }
                                else
                                {
                                    if (data != -1)
                                        unread(ch);
                                    var str = buf.ToString();
                                    if (str.Equals(DASH))
                                    {
                                        unread('.');
                                        ttype = '-';
                                    }
                                    else if (str.Equals(DOT) && !(WORDCHAR != attribute[prevChar]))
                                        ttype = '.';
                                    else
                                    {
                                        try
                                        {
                                            nval = double.Parse(str);
                                        }
                                        catch (FormatException) { }
                                    }
                                    state = STATE_DONE;
                                }
                            }
                            else
                            {
                                buf.Append(ch);
                                state = STATE_NUMBER4;
                            }
                            break;
                        }
                    case STATE_NUMBER4:
                        {
                            if (data == -1 || attribute[ch] != NUMBERCHAR || ch == '-' || ch == '.')
                            {
                                if (data != -1)
                                    unread(ch);
                                try
                                {
                                    nval = double.Parse(buf.ToString());
                                }
                                catch (FormatException) { }
                                state = STATE_DONE;
                            }
                            else
                                buf.Append(ch);
                            break;
                        }
                    case STATE_LINECOMMENT:
                        {
                            if (data == -1)
                            {
                                ttype = TT_EOF;
                                state = STATE_DONE;
                            }
                            else if (ch == '\n' || ch == '\r')
                            {
                                unread(ch);
                                state = STATE_NEUTRAL;
                            }
                            break;
                        }
                    case STATE_DONE_ON_EOL:
                        {
                            if (ch != '\n' && data != -1)
                                unread(ch);
                            state = STATE_DONE;
                            break;
                        }
                    case STATE_PROCEED_ON_EOL:
                        {
                            if (ch != '\n' && data != -1)
                                unread(ch);
                            state = STATE_NEUTRAL;
                            break;
                        }
                    case STATE_STRING:
                        {
                            if (data == -1 || ch == qChar || ch == '\r' || ch == '\n')
                            {
                                sval = buf.ToString();
                                if (ch == '\r' || ch == '\n')
                                    unread(ch);
                                state = STATE_DONE;
                            }
                            else if (ch == '\\')
                                state = STATE_STRING_ESCAPE_SEQ;
                            else
                                buf.Append(ch);
                            break;
                        }
                    case STATE_STRING_ESCAPE_SEQ:
                        {
                            if (data == -1)
                            {
                                sval = buf.ToString();
                                state = STATE_DONE;
                                break;
                            }

                            state = STATE_STRING;
                            if (ch == 'a')
                                buf.Append(0x7);
                            else if (ch == 'b')
                                buf.Append('\b');
                            else if (ch == 'f')
                                buf.Append(0xC);
                            else if (ch == 'n')
                                buf.Append('\n');
                            else if (ch == 'r')
                                buf.Append('\r');
                            else if (ch == 't')
                                buf.Append('\t');
                            else if (ch == 'v')
                                buf.Append(0xB);
                            else if (ch >= '0' && ch <= '7')
                            {
                                octalNumber = ch - '0';
                                state = STATE_STRING_ESCAPE_SEQ_OCTAL;
                            }
                            else
                                buf.Append(ch);
                            break;
                        }
                    case STATE_STRING_ESCAPE_SEQ_OCTAL:
                        {
                            if (data == -1 || ch < '0' || ch > '7')
                            {
                                buf.Append((char)octalNumber);
                                if (data == -1)
                                {
                                    sval = buf.ToString();
                                    state = STATE_DONE;
                                }
                                else
                                {
                                    unread(ch);
                                    state = STATE_STRING;
                                }
                            }
                            else
                            {
                                var temp = octalNumber * 8 + (ch - '0');
                                if (temp < 256)
                                    octalNumber = temp;
                                else
                                {
                                    buf.Append((char)octalNumber);
                                    buf.Append(ch);
                                    state = STATE_STRING;
                                }
                            }
                            break;
                        }
                    case STATE_POSSIBLEC_COMMENT:
                        {
                            if (ch == '*')
                                state = STATE_C_COMMENT;
                            else if (ch == '/')
                                state = STATE_LINECOMMENT;
                            else
                            {
                                if (data != -1)
                                    unread(ch);
                                ttype = '/';
                                state = STATE_DONE;
                            }
                            break;
                        }
                    case STATE_C_COMMENT:
                        {
                            if (ch == '*')
                                state = STATE_POSSIBLEC_COMMENT_END;
                            if (ch == '\n')
                                lineno++;
                            else if (data == -1)
                            {
                                ttype = TT_EOF;
                                state = STATE_DONE;
                            }
                            break;
                        }
                    case STATE_POSSIBLEC_COMMENT_END:
                        {
                            if (data == -1)
                            {
                                ttype = TT_EOF;
                                state = STATE_DONE;
                            }
                            else if (ch == '/')
                                state = STATE_NEUTRAL;
                            else if (ch != '*')
                                state = STATE_C_COMMENT;
                            break;
                        }
                }
            }
            while (state != STATE_DONE);

            if (ttype == TT_WORD && lowerCaseMode)
            {
                sval = sval?.ToLower();
            }

            return ttype;
        }

        /// <summary>
        /// Specifies that the character argument is "ordinary" in this tokenizer.
        /// </summary>
        /// <param name="ch">The character.</param>
        public virtual void OrdinaryChar(int ch)
        {
            if (ch >= 0 && ch <= 255)
                attribute[ch] = ORDINARYCHAR;
        }

        /// <summary>
        /// Specifies that all characters c in the range low less-equal c less-equal high are "ordinary" in this 
        /// tokenizer.
        /// </summary>
        /// <param name="low">Low end of the range.</param>
        /// <param name="hi">High end of the range.</param>
        public virtual void OrdinaryChars(int low, int hi)
        {
            SetAttributes(low, hi, ORDINARYCHAR);
        }

        /// <summary>
        /// Specifies that numbers should be parsed by this tokenizer.
        /// </summary>
        public virtual void ParseNumbers()
        {
            for (int i = '0'; i <= '9'; i++)
                attribute[i] = NUMBERCHAR;
            attribute['.'] = NUMBERCHAR;
            attribute['-'] = NUMBERCHAR;
        }

        /// <summary>
        /// Causes the next call to the nextToken method of this tokenizer to return the current value in the 
        /// ttype field, and not to modify the value in the nval or sval field.
        /// </summary>
        public virtual void PushBack()
        {
            if (ttype != TT_NOTHING)
                pushedback = true;
        }

        /// <summary>
        /// Specifies that matching pairs of this character delimit string constants in this tokenizer.
        /// </summary>
        /// <param name="ch">The character.</param>
        public virtual void QuoteChar(int ch)
        {
            if (ch >= 0 && ch <= 255)
                attribute[ch] = QUOTECHAR;
        }

        /// <summary>
        /// Resets this tokenizer's syntax table so that all characters are "ordinary." See the ordinaryChar 
        /// method for more information on a character being ordinary.
        /// </summary>
        public virtual void ResetSyntax()
        {
            OrdinaryChars(0x00, 0xff);
        }

        /// <summary>
        /// Determines whether the tokenizer recognizes C++-style comments.
        /// </summary>
        /// <param name="flag">True indicates to recognize and ignore C++-style comments.</param>
        public virtual void SlashSlashComments(bool flag)
        {
            slashSlashComments = flag;
        }

        /// <summary>
        /// Determines whether the tokenizer recognizes C-style comments.
        /// </summary>
        /// <param name="flag">True indicates to recognize and ignore C-style comments.</param>
        public virtual void SlashStarComments(bool flag)
        {
            slashStarComments = flag;
        }

        /// <summary>
        /// Returns the string representation of the current stream token.
        /// </summary>
        /// <returns>A String representation of the current stream token.</returns>
        public override string ToString()
        {
            var buffer = new System.Text.StringBuilder(TOKEN);

            switch (ttype)
            {
                case TT_NOTHING:
                    {
                        buffer.Append(NOTHING);
                        break;
                    }
                case TT_WORD:
                    {
                        buffer.Append(sval);
                        break;
                    }
                case TT_NUMBER:
                    {
                        buffer.Append(NUMBER);
                        buffer.Append(nval);
                        break;
                    }
                case TT_EOF:
                    {
                        buffer.Append(EOF);
                        break;
                    }
                case TT_EOL:
                    {
                        buffer.Append(EOL);
                        break;
                    }
            }

            if (ttype > 0)
            {
                if (attribute[ttype] == QUOTECHAR)
                {
                    buffer.Append(QUOTED);
                    buffer.Append(sval);
                }
                else
                {
                    buffer.Append('\'');
                    buffer.Append((char)ttype);
                    buffer.Append('\'');
                }
            }

            buffer.Append(LINE);
            buffer.Append(lineno);
            return buffer.ToString();
        }

        /// <summary>
        /// Specifies that all characters c in the range low less-equal c less-equal high are white space 
        /// characters.
        /// </summary>
        /// <param name="low">The low end of the range.</param>
        /// <param name="hi">The high end of the range.</param>
        public virtual void WhitespaceChars(int low, int hi)
        {
            SetAttributes(low, hi, WHITESPACECHAR);
        }

        /// <summary>
        /// Specifies that all characters c in the range low less-equal c less-equal high are word constituents.
        /// </summary>
        /// <param name="low">The low end of the range.</param>
        /// <param name="hi">The high end of the range.</param>
        public virtual void WordChars(int low, int hi)
        {
            SetAttributes(low, hi, WORDCHAR);
        }
    }


    /*******************************/
    /// <summary>
    /// This class provides functionality to reads and unread characters into a buffer.
    /// </summary>
    public class BackReader : System.IO.StreamReader
    {
        private readonly char[] buffer;
        private int position = 1;
        //private int markedPosition;

        /// <summary>
        /// Constructor. Calls the base constructor.
        /// </summary>
        /// <param name="streamReader">The buffer from which chars will be read.</param>
        /// <param name="size">The size of the Back buffer.</param>
        /// <param name="encoding">Character encoding of the buffer</param>
        public BackReader(System.IO.Stream streamReader, int size, System.Text.Encoding encoding) : base(streamReader, encoding)
        {
            buffer = new char[size];
            position = size;
        }

        /// <summary>
        /// Constructor. Calls the base constructor.
        /// </summary>
        /// <param name="streamReader">The buffer from which chars will be read.</param>
        /// <param name="encoding">character encoding for the buffer</param>
        public BackReader(System.IO.Stream streamReader, System.Text.Encoding encoding) : base(streamReader, encoding)
        {
            buffer = new char[position];
        }

        /// <summary>
        /// Checks if this stream support mark and reset methods.
        /// </summary>
        /// <remarks>
        /// This method isn't supported.
        /// </remarks>
        /// <returns>Always false.</returns>
        public bool MarkSupported()
        {
            return false;
        }

        /// <summary>
        /// Marks the element at the corresponding position.
        /// </summary>
        /// <remarks>
        /// This method isn't supported.
        /// </remarks>
        public void Mark(int pos)
        {
            throw new System.IO.IOException("Mark operations are not allowed");
        }

        /// <summary>
        /// Resets the current stream.
        /// </summary>
        /// <remarks>
        /// This method isn't supported.
        /// </remarks>
        public void Reset()
        {
            throw new System.IO.IOException("Mark operations are not allowed");
        }

        /// <summary>
        /// Reads a character.
        /// </summary>
        /// <returns>The character read.</returns>
        public override int Read()
        {
            if (position >= 0 && position < buffer.Length)
                return buffer[position++];
            return base.Read();
        }

        /// <summary>
        /// Reads an amount of characters from the buffer and copies the values to the array passed.
        /// </summary>
        /// <param name="array">Array where the characters will be stored.</param>
        /// <param name="index">The beginning index to read.</param>
        /// <param name="count">The number of characters to read.</param>
        /// <returns>The number of characters read.</returns>
        public override int Read(char[] array, int index, int count)
        {
            var readFromBuffer = 0;

            if (count <= 0)
                return 0;

            var available = buffer.Length - position;
            if (available > 0)
            {
                var toCopy = Math.Min(count, available);
                Array.Copy(buffer, position, array, index, toCopy);
                position += toCopy;
                index += toCopy;
                count -= toCopy;
                readFromBuffer = toCopy;
            }

            if (count > 0)
            {
                // base.Read returns number of chars read (0 at EOF)
                var n = base.Read(array, index, count);
                if (n > 0)
                    return readFromBuffer + n;
                // nothing more read from base, return whatever we read from buffer (may be 0)
                return readFromBuffer;
            }

            return readFromBuffer;
        }

        /// <summary>
        /// Checks if this buffer is ready to be read.
        /// </summary>
        /// <returns>True if the position is less than the length, otherwise false.</returns>
        public bool IsReady()
        {
            return (position >= buffer.Length || BaseStream.Position >= BaseStream.Length);
        }

        /// <summary>
        /// Unreads a character.
        /// </summary>
        /// <param name="unReadChar">The character to be unread.</param>
        public void UnRead(int unReadChar)
        {
            position--;
            buffer[position] = (char)unReadChar;
        }

        /// <summary>
        /// Unreads an amount of characters by moving these to the buffer.
        /// </summary>
        /// <param name="array">The character array to be unread.</param>
        /// <param name="index">The beginning index to unread.</param>
        /// <param name="count">The number of characters to unread.</param>
        public void UnRead(char[] array, int index, int count)
        {
            Move(array, index, count);
        }

        /// <summary>
        /// Unreads an amount of characters by moving these to the buffer.
        /// </summary>
        /// <param name="array">The character array to be unread.</param>
        public void UnRead(char[] array)
        {
            Move(array, 0, array.Length - 1);
        }

        /// <summary>
        /// Moves the array of characters to the buffer.
        /// </summary>
        /// <param name="array">Array of characters to move.</param>
        /// <param name="index">Offset of the beginning.</param>
        /// <param name="count">Amount of characters to move.</param>
        private void Move(char[] array, int index, int count)
        {
            for (var arrayPosition = index + count; arrayPosition >= index; arrayPosition--)
                UnRead(array[arrayPosition]);
        }
    }


    /*******************************/
    /// <summary>
    /// Provides functionality to read and unread from a Stream.
    /// </summary>
    public class BackInputStream : System.IO.BinaryReader
    {
        private readonly byte[] buffer;
        private int position = 1;

        /// <summary>
        /// Creates a BackInputStream with the specified stream and size for the buffer.
        /// </summary>
        /// <param name="streamReader">The stream to use.</param>
        /// <param name="size">The specific size of the buffer.</param>
        public BackInputStream(System.IO.Stream streamReader, int size) : base(streamReader)
        {
            buffer = new byte[size];
            position = size;
        }

        /// <summary>
        /// Creates a BackInputStream with the specified stream.
        /// </summary>
        /// <param name="streamReader">The stream to use.</param>
        public BackInputStream(System.IO.Stream streamReader) : base(streamReader)
        {
            buffer = new byte[position];
        }

        /// <summary>
        /// Checks if this stream support mark and reset methods.
        /// </summary>
        /// <returns>Always false, these methods aren't supported.</returns>
        public bool MarkSupported()
        {
            return false;
        }

        /// <summary>
        /// Reads the next bytes in the stream.
        /// </summary>
        /// <returns>The next byte readed</returns>
        public override int Read()
        {
            if (position >= 0 && position < buffer.Length)
                return buffer[position++];
            return base.Read();
        }

        /// <summary>
        /// Reads the amount of bytes specified from the stream.
        /// </summary>
        /// <param name="array">The buffer to read data into.</param>
        /// <param name="index">The beginning point to read.</param>
        /// <param name="count">The number of characters to read.</param>
        /// <returns>The number of characters read into buffer.</returns>
        public virtual int Read(sbyte[] array, int index, int count)
        {
            if (array == null) throw new ArgumentNullException(nameof(array));
            if (index < 0 || count < 0) throw new ArgumentOutOfRangeException((index < 0) ? nameof(index) : nameof(count));
            if (index >= array.Length) return 0;

            // Limit to available space
            var toRead = Math.Min(count, array.Length - index);
            if (toRead == 0) return 0;

            var bytesRead = 0;

            // First take bytes from internal buffer (unread area)
            while (position < buffer.Length && bytesRead < toRead)
            {
                array[index + bytesRead] = (sbyte)buffer[position++];
                bytesRead++;
            }

            // If still need more, read from the underlying stream
            if (bytesRead < toRead)
            {
                var remaining = toRead - bytesRead;
                var tmp = new byte[remaining];
                var n = base.Read(tmp, 0, remaining);
                if (n > 0)
                {
                    for (var i = 0; i < n; i++)
                        array[index + bytesRead + i] = (sbyte)tmp[i];
                    bytesRead += n;
                }
            }

            return bytesRead;
        }

        /// <summary>
        /// Unreads a byte from the stream.
        /// </summary>
        /// <param name="element">The value to be unread.</param>
        public void UnRead(int element)
        {
            position--;
            if (position >= 0)
                buffer[position] = (byte)element;
        }

        /// <summary>
        /// Unreads an amount of bytes from the stream.
        /// </summary>
        /// <param name="array">The byte array to be unread.</param>
        /// <param name="index">The beginning index to unread.</param>
        /// <param name="count">The number of bytes to be unread.</param>
        public void UnRead(byte[] array, int index, int count)
        {
            Move(array, index, count);
        }

        /// <summary>
        /// Unreads an array of bytes from the stream.
        /// </summary>
        /// <param name="array">The byte array to be unread.</param>
        public void UnRead(byte[] array)
        {
            Move(array, 0, array.Length - 1);
        }

        /// <summary>
        /// Skips the specified number of bytes from the underlying stream.
        /// </summary>
        /// <param name="numberOfBytes">The number of bytes to be skipped.</param>
        /// <returns>The number of bytes actually skipped</returns>
        public long Skip(long numberOfBytes)
        {
            return BaseStream.Seek(numberOfBytes, System.IO.SeekOrigin.Current) - BaseStream.Position;
        }

        /// <summary>
        /// Moves data from the array to the buffer field.
        /// </summary>
        /// <param name="array">The array of bytes to be unread.</param>
        /// <param name="index">The beginning index to unread.</param>
        /// <param name="count">The amount of bytes to be unread.</param>
        private void Move(byte[] array, int index, int count)
        {
            for (var arrayPosition = index + count; arrayPosition >= index; arrayPosition--)
                UnRead(array[arrayPosition]);
        }
    }

}
