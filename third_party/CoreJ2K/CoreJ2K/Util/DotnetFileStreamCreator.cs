// Copyright (c) 2007-2016 CSJ2K contributors.
// Licensed under the BSD 3-Clause License.

namespace CoreJ2K.Util
{
    using System;
    using System.IO;

    public class DotnetFileStreamCreator : IFileStreamCreator
    {
        #region FIELDS

        private static readonly IFileStreamCreator Instance = new DotnetFileStreamCreator();

        #endregion

        #region METHODS

        public static void Register()
        {
            FileStreamFactory.Register(Instance);
        }

        public Stream Create(string path, string mode)
        {
            if (mode.Equals("rw", StringComparison.OrdinalIgnoreCase)) return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite);
            if (mode.Equals("r", StringComparison.OrdinalIgnoreCase)) return new FileStream(path, FileMode.Open, FileAccess.Read);
            throw new ArgumentException($"File mode: {mode} not supported.", nameof(mode));
        }

        #endregion
    }
}
