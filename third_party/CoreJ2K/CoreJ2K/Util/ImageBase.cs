// Copyright (c) 2007-2016 CSJ2K contributors.
// Licensed under the BSD 3-Clause License.

namespace CoreJ2K.Util
{
    using System;
    using System.Reflection;


    public abstract class ImageBase<TBase> : IImage
    {
        #region CONSTRUCTORS

        protected ImageBase(int width, int height, int numComponents, byte[] bytes)
        {
            Width = width;
            Height = height;
            NumComponents = numComponents;
            Bytes = bytes;
        }

        #endregion

        #region PROPERTIES

        protected int Width { get; }

        protected int Height { get; }

        protected int NumComponents { get; }

        protected byte[] Bytes { get; }

        #endregion

        #region METHODS

        public virtual T As<T>()
        {
            if (!typeof(TBase).GetTypeInfo().IsAssignableFrom(typeof(T).GetTypeInfo()))
            {
                throw new InvalidCastException(
                    $"Cannot cast to '{typeof(T).Name}'; type must be assignable from '{typeof(TBase).Name}'");
            }

            return (T)GetImageObject();
        }

        protected abstract object GetImageObject();

        #endregion
    }
}
