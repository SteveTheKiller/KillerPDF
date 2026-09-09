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

using System;

namespace CoreJ2K.j2k.util
{
    /// <summary> This class manages common facilities for multi-threaded
    /// environments, It can register different facilities for each thread,
    /// and also a default one, so that they can be referred by static
    /// methods, while possibly having different ones for different
    /// threads. Also a default facility exists that is used for threads
    /// for which no particular facility has been registerd registered.
    /// Currently the only kind of facilities managed is MsgLogger.
    /// An example use of this class is if 2 instances of a decoder are running
    /// in different threads and the messages of the 2 instances should be
    /// separated.
    /// The default MsgLogger is a StreamMsgLogger that uses System.out as
    /// the 'out' stream and System.err as the 'err' stream, and a line width of
    /// 78. This can be changed using the registerMsgLogger() method.
    /// </summary>
    /// <seealso cref="IMsgLogger">
    /// </seealso>
    /// <seealso cref="StreamMsgLogger">
    /// </seealso>
    public static class FacilityManager
    {
        #region FIELDS


        private static IMsgLogger _defMsgLogger;
        private static readonly object _loggerLock = new object();

        #endregion

        #region CONSTRUCTORS

        static FacilityManager()
        {
#if NET8_0_OR_GREATER
            _defMsgLogger = new CoreJ2K.Util.DotnetMsgLogger();
#else
            _defMsgLogger = J2kSetup.GetSinglePlatformInstance<IMsgLogger>();
#endif
        }

        #endregion

        #region PROPERTIES

        /// <summary>
        /// The default logger, for threads that have none associated with them.
        /// Thread-safe property with locking to prevent race conditions.
        /// </summary>
        public static IMsgLogger DefaultMsgLogger
        {
            set
            {
                if (value == null)
                {
                    throw new ArgumentNullException(nameof(value), "Logger cannot be null");
                }
                
                lock (_loggerLock)
                {
                    _defMsgLogger = value;
                }
            }
        }

        #endregion

        /// <summary>
        /// Returns the MsgLogger registered with the current thread (the
        /// thread that calls this method). If the current thread has no
        /// registered MsgLogger then the default message logger is
        /// returned.
        /// 
        /// Thread-safe: Uses locking to ensure consistent reads across threads.
        /// </summary>
        /// <returns>
        /// The MsgLogger registered for the current thread, or the
        /// default one if there is none registered for it. Never returns null.
        /// </returns>
        public static IMsgLogger GetMsgLogger()
        {
            lock (_loggerLock)
            {
                return _defMsgLogger ?? throw new InvalidOperationException("Logger not properly initialized");
            }
        }
    }
}
