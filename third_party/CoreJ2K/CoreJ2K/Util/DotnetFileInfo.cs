// Copyright (c) 2007-2016 CSJ2K contributors.
// Licensed under the BSD 3-Clause License.

namespace CoreJ2K.Util
{
    using System;
    using System.IO;

    internal class DotnetFileInfo : IFileInfo
    {
        #region FIELDS

        private readonly FileInfo _fileInfo;

        #endregion

        #region CONSTRUCTORS

        internal DotnetFileInfo(string fileName)
        {
            _fileInfo = new FileInfo(fileName);
        }

        #endregion

        #region PROPERTIES

        public string Name => _fileInfo.Name;

        public string FullName => _fileInfo.FullName;

        public bool Exists => _fileInfo.Exists;

        #endregion

        #region METHODS

        public bool Delete()
        {
            try
            {
                _fileInfo.Delete();
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        #endregion
    }
}
