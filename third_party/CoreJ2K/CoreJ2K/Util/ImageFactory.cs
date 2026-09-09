// Copyright (c) 2007-2016 CSJ2K contributors.
// Copyright (c) 2024-2025 Sjofn LLC.
// Licensed under the BSD 3-Clause License.

using System;
using System.Collections.Generic;
using System.Linq;

namespace CoreJ2K.Util
{
    using j2k.image;

    public static class ImageFactory
    {
        #region FIELDS

        // Immutable snapshot, swapped atomically under _lock. Readers (New/ToPortableImageSource)
        // enumerate whatever snapshot they observe without taking the lock, so a Register racing
        // an in-flight decode can never invalidate the reader's enumeration.
        private static volatile IImageCreator[] _creators = Array.Empty<IImageCreator>();
        private static readonly object _lock = new object();

        #endregion

        #region CONSTRUCTORS

        static ImageFactory()
        {
#if !NET8_0_OR_GREATER
            foreach (var creator in J2kSetup.FindCodecInstances<IImageCreator>())
            {
                Register(creator);
            }
#endif
        }

#if NET8_0_OR_GREATER
        private static volatile bool _autoRegistered;
        private static readonly object _autoRegisterLock = new object();

        /// <summary>
        /// On JIT runtimes, scans the application directory for CoreJ2K.*.dll plugin
        /// assemblies just like the .NET Framework path, so apps that never statically
        /// touch a plugin assembly (and therefore never trigger its [ModuleInitializer])
        /// still get its creators registered. On NativeAOT dynamic discovery is
        /// impossible; plugins register via [ModuleInitializer] or an explicit Register.
        /// Deliberately NOT run from the static constructor: loading an assembly there
        /// (type-init lock held → loader lock wanted) can deadlock against a plugin
        /// [ModuleInitializer] on another thread calling Register (loader lock held →
        /// type-init lock wanted). Instead this runs lazily on the first conversion,
        /// outside any type-init lock.
        /// </summary>
        [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2026",
            Justification = "Best-effort fallback; trimmed apps keep plugin [ModuleInitializer] registration and can call Register explicitly.")]
        [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("AOT", "IL3050",
            Justification = "Guarded by RuntimeFeature.IsDynamicCodeSupported.")]
        private static void EnsureAutoRegistered()
        {
            if (_autoRegistered) return;
            lock (_autoRegisterLock)
            {
                if (_autoRegistered) return;
                try
                {
                    if (System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported)
                    {
                        foreach (var creator in J2kSetup.FindCodecInstances<IImageCreator>())
                        {
                            // First-wins: discovery must never replace a creator the app
                            // registered explicitly (or that a module initializer added).
                            RegisterIfAbsent(creator);
                        }
                    }
                }
                finally
                {
                    _autoRegistered = true;
                }
            }
        }

        private static void RegisterIfAbsent(IImageCreator creator)
        {
            lock (_lock)
            {
                foreach (var existing in _creators)
                {
                    if (existing.ImageType == creator.ImageType) return;
                }
                var updated = new List<IImageCreator>(_creators.Length + 1);
                updated.AddRange(_creators);
                updated.Add(creator);
                _creators = updated.ToArray();
            }
        }
#endif

        #endregion

        #region METHODS

        /// <summary>
        /// Registers an image creator. Call this from a <c>[ModuleInitializer]</c> or application startup
        /// on platforms where automatic plugin discovery is unavailable (e.g. NativeAOT).
        /// Registration is idempotent per <see cref="IImageCreator.ImageType"/>: registering a creator
        /// for a type that already has one replaces the previous creator instead of accumulating a
        /// duplicate, so a manual Register on top of a module initializer is harmless.
        /// </summary>
        public static void Register(IImageCreator creator)
        {
            if (creator == null) throw new ArgumentNullException(nameof(creator));
            lock (_lock)
            {
                var updated = new List<IImageCreator>(_creators.Length + 1);
                foreach (var existing in _creators)
                {
                    if (existing.ImageType != creator.ImageType) updated.Add(existing);
                }
                updated.Add(creator);
                _creators = updated.ToArray();
            }
        }

        internal static IImage? New<T>(int width, int height, int numComponents, byte[] bytes)
        {
#if NET8_0_OR_GREATER
            EnsureAutoRegistered();
#endif
            return Resolve(typeof(T))?.Create(width, height, numComponents, bytes);
        }

        internal static BlkImgDataSrc? ToPortableImageSource(object imageObject)
        {
            if (imageObject == null) return null;
#if NET8_0_OR_GREATER
            EnsureAutoRegistered();
#endif
            return Resolve(imageObject.GetType())?.ToPortableImageSource(imageObject);
        }

        /// <summary>
        /// Describes the currently registered creators, for use in diagnostic messages when
        /// resolution fails (e.g. "SKBitmapImageCreator(SKBitmap), AvaloniaImageCreator(WriteableBitmap)").
        /// </summary>
        internal static string DescribeRegistered()
        {
            var creators = _creators;
            return creators.Length == 0
                ? "<none>"
                : string.Join(", ", creators.Select(c => $"{c.GetType().Name}({c.ImageType.Name})"));
        }

        private static IImageCreator? Resolve(Type target)
        {
            var creators = _creators;
            IImageCreator? assignable = null;
            foreach (var creator in creators)
            {
                if (creator.ImageType == target) return creator;
                if (assignable == null && creator.ImageType.IsAssignableFrom(target)) assignable = creator;
            }
            return assignable;
        }

        #endregion
    }
}
