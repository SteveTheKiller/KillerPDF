using System.Runtime.CompilerServices;

// Expose internals to test assemblies
[assembly: InternalsVisibleTo("CoreJ2K.Tests")]
[assembly: InternalsVisibleTo("CoreJ2K.Windows.Tests")]
[assembly: InternalsVisibleTo("CoreJ2K.Skia.Tests")]