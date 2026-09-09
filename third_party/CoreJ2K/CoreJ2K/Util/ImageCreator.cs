// Copyright (c) 2025 Sjofn LLC.
// Licensed under the BSD 3-Clause License.

using CoreJ2K.j2k.image;

namespace CoreJ2K.Util
{
    public abstract class ImageCreator<TBase> : IImageCreator
    {
        public System.Type ImageType => typeof(TBase);

        public abstract IImage Create(int width, int height, int numComponents, byte[] bytes);

        public abstract BlkImgDataSrc ToPortableImageSource(object imageObject);
    }
}
