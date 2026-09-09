// Copyright (c) 2007-2016 CSJ2K contributors.
// Licensed under the BSD 3-Clause License.

namespace CoreJ2K.Util
{
    using System.IO;

    public interface IFileStreamCreator
    {
        Stream Create(string path, string mode);
    }
}
