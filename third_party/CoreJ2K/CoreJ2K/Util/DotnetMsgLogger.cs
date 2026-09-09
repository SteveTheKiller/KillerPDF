// Copyright (c) 2007-2016 CSJ2K contributors.
// Licensed under the BSD 3-Clause License.

namespace CoreJ2K.Util
{
    using j2k.util;
    using System;

    public class DotnetMsgLogger : StreamMsgLogger
    {
        #region FIELDS

        private static readonly IMsgLogger Instance = new DotnetMsgLogger();

        #endregion

        #region CONSTRUCTORS

        public DotnetMsgLogger()
            : base(Console.OpenStandardOutput(), Console.OpenStandardError(), 78)
        {
        }

        #endregion

        #region PROPERTIES

        /// <summary>
        /// Minimum severity level for messages to be printed.
        /// Messages with a severity below this value are silently discarded,
        /// eliminating all string-processing and I/O overhead for suppressed levels.
        /// Defaults to <see cref="MsgLogger_Fields.WARNING"/> so routine INFO-level
        /// validation chatter does not generate allocations during normal decoding.
        /// Set to <see cref="MsgLogger_Fields.INFO"/> or <see cref="MsgLogger_Fields.LOG"/>
        /// to restore verbose output.
        /// </summary>
        public static int MinSeverity { get; set; } = MsgLogger_Fields.WARNING;

        #endregion

        #region METHODS

        public static void Register()
        {
            FacilityManager.DefaultMsgLogger = Instance;
        }

        /// <inheritdoc/>
        public override void printmsg(int sev, string msg)
        {
            if (sev < MinSeverity) return;
            base.printmsg(sev, msg);
        }

        #endregion
    }
}
