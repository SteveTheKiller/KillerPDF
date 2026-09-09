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
using CoreJ2K.j2k.util;
using System;

namespace CoreJ2K.j2k
{

    /// <summary> This class handles exceptions. It should be used in places where it
    /// is not known how to handle the exception, and the exception can not
    /// be thrown higher in the stack.
    /// 
    /// Different options can be registered for each Thread and
    /// ThreadGroup. <i>This feature is not implemented yet</i>
    /// 
    /// </summary>
    public class JJ2KExceptionHandler
    {

        /// <summary> Handles the exception. If no special action is registered for
        /// the current thread, then the Exception's stack trace and a
        /// descriptive message are printed to standard error and the
        /// current thread is stopped.
        /// 
        /// <i>Registration of special actions is not implemented yet.</i>
        /// 
        /// </summary>
        /// <param name="e">The exception to handle
        /// 
        /// 
        /// 
        /// </param>
        public static void handleException(Exception e)
        {
            // Test if there is an special action (not implemented yet)

            // If no special action

            // Print a terse summary rather than the full stack trace: this
            // handler fires per-decode on ordinary truncated/corrupt input,
            // and a full trace here is both noisy and (under concurrent
            // decodes) prone to interleaving with other threads' output.
            FacilityManager.GetMsgLogger().printmsg(MsgLogger_Fields.ERROR, $"{e.GetType().Name}: {e.Message}");
            // Print an explicative message
            FacilityManager.GetMsgLogger().println("The Thread is being terminated bacause an Exception (shown above)\nhas been thrown and no special action was defined for this Thread.", 0, 0);
            // Stop the thread (do not use stop, since it's deprecated in
            // Java 1.2)
            throw new InvalidOperationException();
        }
    }
}