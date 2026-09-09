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
* */
using System;
namespace CoreJ2K.j2k.util
{

    /// <summary>
    /// This class holds modules options and parameters as they are provided to the
    /// encoder or the decoder. Each option and its associated parameters are
    /// stored as strings.
    /// 
    /// This class is built on the standard Java Properties class. Consequently,
    /// it offers facilities to load and write parameters from/to a file. In the
    /// meantime, a ParameterList object can also handle default parameters for
    /// each option.
    /// 
    /// Each parameter can be retrieved as a string or as an specific primitive
    /// type (int, float, etc).
    /// 
    /// For more details see the Properties class.
    /// 
    /// Note that this class does not support multiple occurrences of parameters
    /// (for a parameter name, only one value is possible). Also there is no
    /// particular order of the parameters.
    /// </summary>
    public class ParameterList : System.Collections.Generic.Dictionary<string, string>
    {
        private readonly ParameterList defaults;

        /// <summary> Returns the default ParameterList.</summary>
        /// <returns> Default ParameterList</returns>
        public virtual ParameterList DefaultParameterList => defaults;

        /// <summary>
        /// Constructs an empty ParameterList object. It can be later completed by
        /// adding elements one by one, by loading them from a file, or by
        /// initializing them from an argument string.</summary>
        public ParameterList() { }

        /// <summary>
        /// Constructs an empty ParameterList object with the provided default
        /// parameters. The list can be later updated by adding elements one by
        /// one, by loading them from a file, or by initializing them from an
        /// argument string.
        /// </summary>
        /// <param name="def">The defaults parameters</param>
        public ParameterList(ParameterList def) { defaults = def; }

        /// <summary>
        /// Parses the parameters from an argument list, such as the one in the
        /// command line, and integrates them in this parameter list.
        /// 
        /// All options must be preceded by '-' and then followed by one or more
        /// words, which constitutes the values. The name of the options constitute
        /// the name of the parameters. The only exception is for boolean options,
        /// in which case if they are preceded by '-' they will be turned on, and
        /// if preceded by '+' they will be turned off. The string value of a
        /// boolean option is "on" or "off". Note that the '-' and '+' characters
        /// can not precede any word which would be a value for an option unless
        /// they are numeric values (otherwise it would be considered as a boolean
        /// option). Note also that the name of an option can not start with a
        /// number.
        /// 
        /// No option can appear more than once or an exception is thrown.
        /// 
        /// For instance the string:
        /// 
        /// <quote> "-Ffilters w5x3 -Wlev 5 -Qtype reversible </quote>
        /// 
        /// will create the following parameter list:
        /// 
        /// <pre>
        /// Ffilers  w5x3
        /// Wlev     5
        /// Qtype    reversible
        /// </pre>
        /// </summary>
        /// <param name="argv">The argument list.</param>
        /// <exception cref="StringFormatException">if there are invalid arguments in 'argv'</exception>
        public virtual void parseArgs(string[] argv)
        {
            // Read options
            var k = -1;
            // Skip empty arguments
            do
            {
                k++;
                if (k >= argv.Length)
                {
                    // Nothing to put in parameters
                    return;
                }
            }
            while (argv[k].Length <= 0);

            // Check that we start with an option and that its is not a number
            var c = argv[k][0];
            if (c != '-' && c != '+')
            {
                // It's not an option
                throw new StringFormatException($"Argument list does not start with an option: {argv[k]}");
            }
            if (argv[k].Length >= 2 && char.IsDigit(argv[k][1]))
            {
                throw new StringFormatException($"Numeric option name: {argv[k]}");
            }
            var pvalue = new System.Text.StringBuilder();
            while (k < argv.Length)
            {
                // Read parameter name
                if (argv[k].Length <= 1)
                {
                    throw new StringFormatException($"Option \"{argv[k]}\" is too short.");
                }
                c = argv[k][0];
                var pname = argv[k++];
                pvalue.Length = 0;
                // Are there any more arguments?
                if (k >= argv.Length)
                {
                    // No more words in argument list => must be boolean
                    pvalue.Append((c == '-') ? "on" : "off");
                }
                else
                {
                    var c2 = argv[k][0];
                    // Is next word an option or a value?
                    if (c2 == '-' || c2 == '+')
                    {
                        // Next word could be an option
                        if (argv[k].Length <= 1)
                        {
                            throw new StringFormatException($"Option or argument \"{argv[k]}\" too short");
                        }
                        if (!char.IsDigit(argv[k][1]))
                        {
                            // Not a number => we have a boolean option in pname
                            pvalue.Append((c == '-') ? "on" : "off");
                        }
                    }
                    if (pvalue.Length == 0)
                    {
                        // No value yet
                        // It should not a boolean option, read the values
                        if (c == '+')
                        {
                            throw new StringFormatException($"Boolean option \"{pname}\" has a value");
                        }
                        // We have at least one value
                        pvalue.Append(argv[k++]);
                        while (k < argv.Length)
                        {
                            // If empty string skip it
                            if (argv[k].Length == 0)
                            {
                                k++;
                                continue;
                            }
                            c = argv[k][0];
                            if (c == '-' || c == '+')
                            {
                                // Next word could be an option
                                if (argv[k].Length <= 1)
                                {
                                    throw new StringFormatException($"Option or argument \"{argv[k]}\" too short");
                                }
                                if (!char.IsDigit(argv[k][1]))
                                {
                                    // It's an option => stop
                                    break;
                                }
                            }
                            pvalue.Append(' '); // Add a space
                            pvalue.Append(argv[k++]);
                        }
                    }
                }
                // Now put parameter and value in the list
                /*
				if (this[(System.String) pname.Substring(1)] != null)
				{
					// Option is repeated => ERROR
					throw new StringFormatException("Option \"" + pname + "\" appears more than once");
				}
                 */
                object tempObject = this[pname.Substring(1)];
                this[pname.Substring(1)] = pvalue.ToString();
                var generatedAux4 = tempObject;
            }
        }

        /// <summary>
        /// Returns the value of the named parameter, as a string. The value can
        /// come from the defaults, if they exist.
        /// </summary>
        /// <param name="pname">The parameter name.</param>
        /// <returns> the value of the parameter as a string, or null if there is no
        /// parameter with the name 'pname'.</returns>
        public virtual string? GetParameter(string pname)
        {
            if (TryGetValue(pname, out var pval) || defaults == null) { return pval; }

            // if parameter is not there, check in default params
            return !defaults.TryGetValue(pname, out pval) ? null : pval;
        }

        /// <summary>
        /// Returns the value of the named parameter as a boolean. The value "on"
        /// is interpreted as 'true', while the value "off" is interpreted as
        /// 'false'. If the parameter has another value then an
        /// StringFormatException is thrown. If the parameter 'pname' is not in the
        /// parameter list, an IllegalArgumentException is thrown.
        /// </summary>
        /// <param name="pname">The parameter name.</param>
        /// <returns> the value of the parameter as a boolean.</returns>
        /// <exception cref="StringFormatException">If the parameter has a value which is
        /// neither "on" nor "off".</exception>
        /// <exception cref="ArgumentException">If there is no parameter with the
        /// name 'pname' in the parameter list.</exception>
        public virtual bool GetBooleanParameter(string pname)
        {
            var s = GetParameter(pname);

            switch (s)
            {
                case null:
                    throw new ArgumentException($"No parameter with name {pname}");
                case "on":
                    return true;
                case "off":
                    return false;
                default:
                    throw new StringFormatException($"Parameter \"{pname}\" is not boolean: {s}");
            }
        }

        /// <summary>
        /// Returns the value of the named parameter as an int. If the parameter
        /// has a non-numeric value a NumberFormatException is thrown. If the
        /// parameter has a multiple word value than the first word is returned as
        /// an int, others are ignored. If the parameter 'pname' is not in the
        /// parameter list, an IllegalArgumentException is thrown.
        /// </summary>
        /// <param name="pname">The parameter name.</param>
        /// <returns>the value of the parameter as an int.</returns>
        /// <exception cref="FormatException">If the parameter has a non-numeric
        /// value.</exception>
        /// <exception cref="ArgumentException">If there is no parameter with the
        /// name 'pname' in the parameter list.</exception>
        public virtual int GetIntParameter(string pname)
        {
            var s = GetParameter(pname);

            if (s == null)
            {
                throw new ArgumentException($"No parameter with name {pname}");
            }

            try
            {
                return int.Parse(s);
            }
            catch (FormatException e)
            {
                throw new FormatException($"Parameter \"{pname}\" is not integer: {e.Message}");
            }
        }

        /// <summary>
        /// Returns the value of the named parameter as a float. If the parameter
        /// has a non-numeric value a NumberFormatException is thrown. If the
        /// parameter has a multiple word value than the first word is returned as
        /// an int, others are ignored. If the parameter 'pname' is not in the
        /// parameter list, an IllegalArgumentException is thrown.
        /// </summary>
        /// <param name="pname">The parameter name.</param>
        /// <exception cref="FormatException">If the parameter has a non-numeric value.</exception>
        /// <exception cref="ArgumentException">If there is no parameter with the
        /// name 'pname' in the parameter list.</exception>
        /// <returns> the value of the parameter as a float.</returns>
        public virtual float GetFloatParameter(string pname)
        {
            var s = GetParameter(pname);

            if (s == null)
            {
                throw new ArgumentException($"No parameter with name {pname}");
            }

            try
            {
                return float.Parse(s);
            }
            catch (FormatException e)
            {
                throw new FormatException($"Parameter \"{pname}\" is not floating-point: {e.Message}");
            }
        }

        /// <summary>
        /// Checks if the parameters which name starts with the prefix 'prfx' in
        /// the parameter list are all in the list of valid parameter names
        /// 'plist'. If there is a parameter that is not in 'plist' an
        /// <see cref="ArgumentException"/> is thrown with an explanation message. The
        /// default parameters are also included in the check.
        /// </summary>
        /// <param name="prfx">The prefix of parameters to check.</param>
        /// <param name="plist">The list of valid parameter names for the 'prfx'
        /// prefix. If null it is considered that no names are valid.</param>
        /// <exception cref="ArgumentException">If there's a parameter name
        /// starting with 'prfx' which is not in the valid list of parameter names.</exception>
        public virtual void checkList(char prfx, string[] plist)
        {
            System.Collections.IEnumerator args = Keys.GetEnumerator();

            while (args.MoveNext())
            {
                var val = (string)args.Current;
                if (!string.IsNullOrEmpty(val) && val[0] == prfx)
                {
                    var isvalid = false;
                    if (plist != null)
                    {
                        int i;
                        for (i = plist.Length - 1; i >= 0; i--)
                        {
                            if (val.Equals(plist[i]))
                            {
                                isvalid = true;
                                break;
                            }
                        }
                    }
                    if (!isvalid)
                    {
                        // Did not find valid flag
                        throw new ArgumentException($"Option '{val}' is not a valid one.");
                    }
                }
            }
        }

        /// <summary>
        /// Checks if the parameters which names do not start with any of the
        /// prefixes in 'prfxs' in this ParameterList are all in the list of valid
        /// parameter names 'plist'. If there is a parameter that is not in 'plist'
        /// an IllegalArgumentException is thrown with an explanation message. The
        /// default parameters are also included in the check.
        /// </summary>
        /// <param name="prfxs">The prefixes of parameters to ignore.</param>
        /// <param name="plist">The list of valid parameter names. If null it is
        /// considered that no names are valid.</param>
        /// <exception cref="ArgumentException">If there's a parameter name not
        /// starting with 'prfx' which is not in the valid list of parameter names.</exception>
        public virtual void checkList(char[] prfxs, string[] plist)
        {
            System.Collections.IEnumerator args = Keys.GetEnumerator();
            var strprfxs = new string(prfxs);

            while (args.MoveNext())
            {
                var val = ((string)args.Current);
                if (!string.IsNullOrEmpty(val) && strprfxs.IndexOf(val[0]) == -1)
                {
                    var isvalid = false;
                    if (plist != null)
                    {
                        int i;
                        for (i = plist.Length - 1; i >= 0; i--)
                        {
                            if (val.Equals(plist[i]))
                            {
                                isvalid = true;
                                break;
                            }
                        }
                    }
                    if (!isvalid)
                    {
                        throw new ArgumentException($"Option '{val}' is not a valid one.");
                    }
                }
            }
        }

        /// <summary>
        /// Converts the usage information to a list of parameter names in a single
        /// array. The usage information appears in a 2D array of String. The first
        /// dimensions contains the different options, the second dimension
        /// contains the name of the option (first element), the synopsis and the
        /// explanation. This method takes the names of the different options in
        /// 'pinfo' and returns them in a single array of String.
        /// </summary>
        /// <param name="pinfo">The list of options and their usage info (see above).</param>
        /// <returns> An array with the names of the options in pinfo. If pinfo is
        /// null, null is returned.</returns>
        public static string[] toNameArray(string[][] pinfo)
        {
            if (pinfo == null)
            {
                return null;
            }

            var pnames = new string[pinfo.Length];

            for (var i = pinfo.Length - 1; i >= 0; i--)
            {
                pnames[i] = pinfo[i][0];
            }
            return pnames;
        }
    }
}