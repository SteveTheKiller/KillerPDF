// Copyright (c) 2007-2016 CSJ2K contributors.
// Licensed under the BSD 3-Clause License.

namespace CoreJ2K.Util
{
    using j2k.image;

    public interface IImageCreator
    {
        IImage Create(int width, int height, int numComponents, byte[] bytes);

        BlkImgDataSrc ToPortableImageSource(object imageObject);

        System.Type ImageType { get; }
    }
}
