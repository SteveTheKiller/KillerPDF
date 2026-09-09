// Copyright (c) 2007-2016 CSJ2K contributors.
namespace CoreJ2K.Util
{
    using System;
    using System.Collections;

    public class Tokenizer : System.Collections.IEnumerator
    {
        /// Position over the string
        private long currentPos = 0;

        /// Include delimiters in the results.
        private readonly bool includeDelims = false;

        /// Char representation of the String to tokenize.
        private readonly char[]? chars = null;

        //The tokenizer uses the default delimiter set: the space character, the tab character, the newline character, and the carriage-return character and the form-feed character
        private string delimiters = " \t\n\r\f";

        /// <summary>
        /// Initializes a new class instance with a specified string to process
        /// </summary>
        /// <param name="source">String to tokenize</param>
        public Tokenizer(string source)
        {
            chars = source.ToCharArray();
        }

        /// <summary>
        /// Initializes a new class instance with a specified string to process
        /// and the specified token delimiters to use
        /// </summary>
        /// <param name="source">String to tokenize</param>
        /// <param name="delimiters">String containing the delimiters</param>
        public Tokenizer(string source, string delimiters) : this(source)
        {
            this.delimiters = delimiters;
        }


        /// <summary>
        /// Initializes a new class instance with a specified string to process, the specified token 
        /// delimiters to use, and whether the delimiters must be included in the results.
        /// </summary>
        /// <param name="source">String to tokenize</param>
        /// <param name="delimiters">String containing the delimiters</param>
        /// <param name="includeDelims">Determines if delimiters are included in the results.</param>
        public Tokenizer(string source, string delimiters, bool includeDelims) : this(source, delimiters)
        {
            this.includeDelims = includeDelims;
        }


        /// <summary>
        /// Returns the next token from the token list
        /// </summary>
        /// <returns>The string value of the token</returns>
        public string NextToken()
        {
            return NextToken(delimiters);
        }

        /// <summary>
        /// Returns the next token from the source string, using the provided
        /// token delimiters
        /// </summary>
        /// <param name="delimiter">String containing the delimiters to use</param>
        /// <returns>The string value of the token</returns>
        public string NextToken(string delimiter)
        {
            //According to documentation, the usage of the received delimiters should be temporary (only for this call).
            //However, it seems it is not true, so the following line is necessary.
            this.delimiters = delimiter;

            //at the end 
            if (chars == null || currentPos >= chars.Length)
                throw new InvalidOperationException("No more tokens available");
            //if over a delimiter and delimiters must be returned
            else if ((Array.IndexOf(delimiters.ToCharArray(), chars[currentPos]) != -1)
                     && includeDelims)
            {
                var single = new char[1] { chars[currentPos++] };
                return new string(single);
            }
            //need to get the token wo delimiters.
            else
                return nextToken(delimiters.ToCharArray());
        }

        //Returns the nextToken wo delimiters
        private string nextToken(char[] delimiter)
        {
            var pos = currentPos;

            //skip possible delimiters
            while (currentPos < chars.Length && Array.IndexOf(delimiter, chars[currentPos]) != -1)
            {
                currentPos++;
            }
            // The last one was a delimiter (i.e. there are no more tokens)
            if (currentPos >= chars.Length)
            {
                currentPos = pos;
                throw new InvalidOperationException("No more tokens available");
            }

            //getting the token: compute start and length to avoid repeated concatenation
            var start = currentPos;
            while (currentPos < chars.Length && Array.IndexOf(delimiter, chars[currentPos]) == -1)
                currentPos++;

            var length = (int)(currentPos - start);
            if (length <= 0) return string.Empty;
            return new string(chars, (int)start, length);
        }


        /// <summary>
        /// Determines if there are more tokens to return from the source string
        /// </summary>
        /// <returns>True or false, depending on if there are more tokens</returns>
        public bool HasMoreTokens()
        {
            if (chars == null) { return false; }
            if (currentPos >= chars.Length) { return false; }

            var delims = delimiters.ToCharArray();

            // If current position is on a delimiter and delimiters are returned, there's a token
            if (currentPos < chars.Length && Array.IndexOf(delims, chars[currentPos]) != -1 && includeDelims)
            {
                return true;
            }

            // Otherwise, scan forward to find a non-delimiter character
            var pos = (int)currentPos;
            while (pos < chars.Length && Array.IndexOf(delims, chars[pos]) != -1)
            {
                pos++;
            }

            return pos < chars.Length;
        }

        /// <summary>
        /// Remaining tokens count
        /// </summary>
        public int Count
        {
            get
            {
                // Compute remaining tokens without modifying currentPos and without using exceptions
                if (chars == null) return 0;
                if (currentPos >= chars.Length) return 0;

                var delims = delimiters.ToCharArray();
                var pos = (int)currentPos;
                var i = 0;

                while (pos < chars.Length)
                {
                    // If delimiters are returned and current char is a delimiter, it's a token of length 1
                    if (Array.IndexOf(delims, chars[pos]) != -1)
                    {
                        if (includeDelims)
                        {
                            i++;
                            pos++;
                            continue;
                        }

                        // Skip delimiters
                        while (pos < chars.Length && Array.IndexOf(delims, chars[pos]) != -1)
                            pos++;
                    }
                    else
                    {
                        // Found start of token, advance until next delimiter
                        while (pos < chars.Length && Array.IndexOf(delims, chars[pos]) == -1)
                            pos++;
                        i++;
                    }
                }

                return i;
            }
        }

        private string? currentToken = null;
        private bool hasCurrent = false;

        /// <summary>
        ///  Performs the same action as NextToken.
        /// </summary>
        public object Current
        {
            get
            {
                if (!hasCurrent)
                    throw new InvalidOperationException("Enumeration has not started. Call MoveNext().");
                return currentToken!;
            }
        }

        /// <summary>
        ///  Performs the same action as HasMoreTokens.
        /// </summary>
        /// <returns>True or false, depending on if there are more tokens</returns>
        public bool MoveNext()
        {
            if (!HasMoreTokens())
            {
                currentToken = null;
                hasCurrent = false;
                return false;
            }

            try
            {
                currentToken = NextToken();
                hasCurrent = true;
                return true;
            }
            catch (InvalidOperationException)
            {
                // Should not happen because HasMoreTokens checked, but return false defensively
                currentToken = null;
                hasCurrent = false;
                return false;
            }
        }

        /// <summary>
        /// Does nothing.
        /// </summary>
        public void Reset()
        {
            currentPos = 0;
            currentToken = null;
            hasCurrent = false;
        }
    }
}
