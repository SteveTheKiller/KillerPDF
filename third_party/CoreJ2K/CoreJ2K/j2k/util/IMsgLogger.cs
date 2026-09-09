// Copyright (c) 2007-2016 CSJ2K contributors.
// Licensed under the BSD 3-Clause License.

/*
 * JJ2000 COPYRIGHT:
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
 */

namespace CoreJ2K.j2k.util
{

    /// <summary> This class provides a simple common abstraction of a facility that logs
    /// and/or displays messages or simple strings. The underlying facility can be
    /// a terminal, text file, text area in a GUI display, dialog boxes in a GUI
    /// display, etc., or a combination of those.
    /// Messages are short strings (a couple of lines) that indicate some state
    /// of the program, and that have a severity code associated with them (see
    /// below). Simple strings is text (can be long) that has no severity code
    /// associated with it. Typical use of simple strings is to display help
    /// texts.
    /// Each message has a severity code, which can be one of the following:
    /// LOG, INFO, WARNING, ERROR. Each implementation should treat each severity
    /// code in a way which corresponds to the type of diplay used.
    /// Messages are printed via the 'printmsg()' method. Simple strings are
    /// printed via the 'print()', 'println()' and 'flush()' methods, each simple
    /// string is considered to be terminated once the 'flush()' method has been
    /// called. The 'printmsg()' method should never be called before a previous
    /// simple string has been terminated.
    /// </summary>
    public struct MsgLogger_Fields
    {
        /// <summary>Severity of message. LOG messages are just for bookkeeping and do not
        /// need to be displayed in the majority of cases 
        /// </summary>
        public const int LOG = 0;

        /// <summary>Severity of message. INFO messages should be displayed just for user
        /// feedback. 
        /// </summary>
        public const int INFO = 1;

        /// <summary>Severity of message. WARNING messages denote that an unexpected state
        /// has been reached and should be given as feedback to the user. 
        /// </summary>
        public const int WARNING = 2;

        /// <summary>Severity of message. ERROR messages denote that something has gone
        /// wrong and probably that execution has ended. They should be definetely
        /// displayed to the user. 
        /// </summary>
        public const int ERROR = 3;
    }

    public interface IMsgLogger
    {
        /// <summary> Prints the message 'msg' to the output device, appending a newline,
        /// with severity 'sev'. Some implementations where the appended newline is
        /// irrelevant may not append the newline. Depending on the implementation
        /// the severity of the message may be added to it. The message is
        /// reformatted as appropriate for the output devic, but any newline
        /// characters are respected.
        /// </summary>
        /// <param name="sev">The message severity (LOG, INFO, etc.)
        /// </param>
        /// <param name="msg">The message to display
        /// </param>
        void printmsg(int sev, string msg);

        /// <summary> Prints the string 'str' to the output device, appending a line
        /// return. The message is reformatted as appropriate to the particular
        /// diplaying device, where 'flind' and 'ind' are used as hints for
        /// performing that operation. However, any newlines appearing in 'str' are
        /// respected. The output device may not display the string until flush()
        /// is called. Some implementations may automatically flush when this
        /// method is called. This method just prints the string, the string does
        /// not make part of a "message" in the sense that no severity is
        /// associated to it.
        /// </summary>
        /// <param name="str">The string to print
        /// </param>
        /// <param name="flind">Indentation of the first line
        /// </param>
        /// <param name="ind">Indentation of any other lines.
        /// </param>
        void println(string str, int flind, int ind);

        /// <summary> Writes any buffered data from the println() method to the device.
        /// </summary>
        void flush();
    }
}
