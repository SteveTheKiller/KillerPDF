// Copyright (c) 2007-2016 CSJ2K contributors.
// Copyright (c) 2025, Sjofn LLC.
// Licensed under the BSD 3-Clause License.

using System.IO;

namespace CoreJ2K
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics.CodeAnalysis;
    using System.Linq;
    using System.Reflection;

    using Util;

    /// <summary>
    /// Setup helper methods for initializing library.
    /// </summary>
    internal static class J2kSetup
    {
        // Simple cache for discovered codec types keyed by the plugin contract type
        private static readonly object _codecCacheLock = new object();
        private static readonly Dictionary<Type, List<Type>> _codecTypeCache = new Dictionary<Type, List<Type>>();

        /// <summary>
        /// Gets a single instance from the platform assembly implementing the <typeparamref name="T"/> type.
        /// </summary>
        /// <typeparam name="T">(Abstract) type for which implementation is requested.</typeparam>
        /// <returns>The single instance from the platform assembly implementing the <typeparamref name="T"/> type, 
        /// or null if no or more than one implementation is available.</returns>
        /// <remarks>It is implicitly assumed that implementation class has a public, parameter-less constructor.</remarks>
        [RequiresUnreferencedCode("Scans assembly DefinedTypes via reflection. Use direct instantiation on net8+.")]
        [RequiresDynamicCode("Uses Activator.CreateInstance on dynamically discovered types.")]
        internal static T? GetSinglePlatformInstance<T>()
        {
            try
            {
                var assembly = GetCurrentAssembly();

                // Find all concrete types in the current assembly that implement/derive from T
                var types = GetConcreteTypes<T>(assembly).ToList();

                // If there isn't exactly one concrete implementation, return default
                if (types.Count != 1)
                {
                    return default(T);
                }

                var type = types.Single();

                // Ensure the type has a public parameterless constructor
                var ctor = type.GetConstructor(Type.EmptyTypes);
                if (ctor == null)
                {
                    return default(T);
                }

                var instance = (T?)Activator.CreateInstance(type);

                return instance;
            }
            catch (Exception ex)
            {
                try
                {
                    CoreJ2K.j2k.util.FacilityManager.GetMsgLogger().printmsg(CoreJ2K.j2k.util.MsgLogger_Fields.ERROR,
                        $"J2kSetup.GetSinglePlatformInstance<{typeof(T).FullName}> failed: {ex.Message}");
                }
                catch
                {
                    // Swallow any logging failures to avoid throwing from a logger.
                }

                return default(T);
            }
        }

        /// <summary>
        /// Gets the default classified instance from the platform assembly implementing the <typeparamref name="T"/> type.
        /// </summary>
        /// <typeparam name="T">(Abstract) type for which implementation is requested.</typeparam>
        /// <returns>The single instance from the platform assembly implementing the <typeparamref name="T"/> type that is classified as default, 
        /// or null if no or more than one default classified implementations are available.</returns>
        /// <remarks>It is implicitly assumed that all implementation classes has a public, parameter-less constructor.</remarks>
        [RequiresUnreferencedCode("Scans assembly DefinedTypes via reflection. Use direct instantiation on net8+.")]
        [RequiresDynamicCode("Uses Activator.CreateInstance on dynamically discovered types.")]
        internal static T? GetDefaultPlatformInstance<T>() where T : IDefaultable
        {
            try
            {
                var assembly = GetCurrentAssembly();
                var types = GetConcreteTypes<T>(assembly);

                return GetDefaultOrSingleInstance<T>(types);
            }
            catch (Exception ex)
            {
                try
                {
                    CoreJ2K.j2k.util.FacilityManager.GetMsgLogger().printmsg(CoreJ2K.j2k.util.MsgLogger_Fields.ERROR,
                        $"J2kSetup.GetDefaultPlatformInstance<{typeof(T).FullName}> failed: {ex.Message}");
                }
                catch
                {
                }

                return default(T);
            }
        }


        [RequiresUnreferencedCode("Scans disk for CoreJ2K.*.dll and reflects types. Use ImageFactory.Register on net8+.")]
        [RequiresDynamicCode("Loads assemblies and uses Activator.CreateInstance on discovered types.")]
        internal static IEnumerable<Type> FindCodecs<T>() where T : IImageCreator
        {
            var contractType = typeof(T);

            // Return cached results if present
            lock (_codecCacheLock)
            {
                if (_codecTypeCache.TryGetValue(contractType, out var cached))
                {
                    return cached;
                }
            }

            try
            {
                var currentAssemblyDir = AppContext.BaseDirectory;
                if (string.IsNullOrEmpty(currentAssemblyDir))
                    return Enumerable.Empty<Type>();

                var dlls = Directory.GetFiles(currentAssemblyDir, "CoreJ2K.*.dll", SearchOption.TopDirectoryOnly);

                var result = new List<Type>();

                foreach (var dll in dlls)
                {
                    Assembly? asm = null;
                    try
                    {
#if NET8_0_OR_GREATER
                        // Load into the default context so plugin types unify with the
                        // app's own references; LoadFile would isolate them in a separate
                        // AssemblyLoadContext and Register creators for foreign type ids.
                        asm = System.Runtime.Loader.AssemblyLoadContext.Default.LoadFromAssemblyPath(dll);
#else
                        asm = Assembly.LoadFile(dll);
#endif
                    }
                    catch (Exception loadEx)
                    {
                        try
                        {
                            CoreJ2K.j2k.util.FacilityManager.GetMsgLogger().printmsg(CoreJ2K.j2k.util.MsgLogger_Fields.INFO,
                                $"Skipping assembly '{dll}' because it failed to load: {loadEx.Message}");
                        }
                        catch
                        {
                        }
                        continue;
                    }

                    Type?[] types;
                    try
                    {
                        types = asm.GetTypes();
                    }
                    catch (ReflectionTypeLoadException rtle)
                    {
                        types = rtle.Types.Where(t => t != null).ToArray()!;
                    }
                    catch (Exception ex)
                    {
                        try
                        {
                            CoreJ2K.j2k.util.FacilityManager.GetMsgLogger().printmsg(CoreJ2K.j2k.util.MsgLogger_Fields.INFO,
                                $"Skipping types from assembly '{dll}' because GetTypes failed: {ex.Message}");
                        }
                        catch
                        {
                        }

                        continue;
                    }

                    foreach (var t in types)
                    {
                        try
                        {
                            if (t == null) continue;

                            if ((t.IsSubclassOf(typeof(T)) || typeof(T).IsAssignableFrom(t)) && !t.IsAbstract)
                            {
                                result.Add(t);
                            }
                        }
                        catch
                        {
                            // Ignore type inspection failures for individual types
                        }
                    }
                }

                // Cache the discovered types for this contract
                lock (_codecCacheLock)
                {
                    _codecTypeCache[contractType] = result.ToList();
                }

                return result;
            }
            catch (Exception ex)
            {
                try
                {
                    CoreJ2K.j2k.util.FacilityManager.GetMsgLogger().printmsg(CoreJ2K.j2k.util.MsgLogger_Fields.ERROR,
                        $"J2kSetup.FindCodecs<{typeof(T).FullName}> failed: {ex.Message}");
                }
                catch
                {
                }

                return Enumerable.Empty<Type>();
            }
        }

        /// <summary>
        /// Find and instantiate codec instances for the given plugin contract T. Instances are cached to avoid repeated disk scans and activations.
        /// </summary>
        [RequiresUnreferencedCode("Scans disk for CoreJ2K.*.dll and reflects types. Use ImageFactory.Register on net8+.")]
        [RequiresDynamicCode("Loads assemblies and uses Activator.CreateInstance on discovered types.")]
        internal static IEnumerable<T> FindCodecInstances<T>() where T : IImageCreator
        {
            // Discover types first (may be cached)
            var types = FindCodecs<T>()?.ToList() ?? new List<Type>();

            foreach (var t in types)
            {
                if (t == null) continue;

                // Ensure public parameterless ctor exists
                if (t.GetConstructor(Type.EmptyTypes) == null) continue;

                object? obj = null;
                try
                {
                    obj = Activator.CreateInstance(t);
                }
                catch (Exception ex)
                {
                    try
                    {
                        CoreJ2K.j2k.util.FacilityManager.GetMsgLogger().printmsg(CoreJ2K.j2k.util.MsgLogger_Fields.INFO,
                            $"Failed to create codec instance of type '{t.FullName}': {ex.Message}");
                    }
                    catch
                    {
                    }
                }

                if (obj is T typed)
                {
                    yield return typed;
                }
            }
        }

        private static Assembly GetCurrentAssembly()
        {
            return typeof(J2kSetup).Assembly;
        }

        [RequiresUnreferencedCode("Scans assembly DefinedTypes via reflection.")]
        private static IEnumerable<Type> GetConcreteTypes<T>(Assembly assembly)
        {
            return assembly.DefinedTypes.Where(
                t =>
                    {
                        try
                        {
                            return (t.IsSubclassOf(typeof(T)) || typeof(T).IsAssignableFrom(t))
                                   && !t.IsAbstract;
                        }
                        catch
                        {
                            return false;
                        }
                    }).Select(t => t.AsType());
        }

        [RequiresUnreferencedCode("Uses Activator.CreateInstance on dynamically discovered types.")]
        [RequiresDynamicCode("Uses Activator.CreateInstance on dynamically discovered types.")]
        private static T? GetDefaultOrSingleInstance<T>(IEnumerable<Type> types) where T : IDefaultable
        {
            var instances = types.Select(
                t =>
                    {
                        try
                        {
                            return (T?)Activator.CreateInstance(t);
                        }
                        catch
                        {
                            return default(T);
                        }
                    }).ToList();

            return instances.Count > 1 ? instances.Single(instance => instance.IsDefault) : instances.SingleOrDefault();
        }
    }
}
