// Copyright (c) 2007-2016 CSJ2K contributors.
// Licensed under the BSD 3-Clause License.

namespace CoreJ2K.Util
{
    public class DotnetFileInfoCreator : IFileInfoCreator
    {
        #region FIELDS

        private static readonly IFileInfoCreator Instance = new DotnetFileInfoCreator();

        #endregion

        #region METHODS

        public static void Register()
        {
            FileInfoFactory.Register(Instance);
        }

        public IFileInfo Create(string fileName)
        {
            return new DotnetFileInfo(fileName);
        }

        #endregion
    }
}
