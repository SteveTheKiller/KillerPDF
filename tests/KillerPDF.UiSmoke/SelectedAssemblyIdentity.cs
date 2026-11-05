using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;

internal static class SelectedAssemblyIdentity
{
    internal static string HashLoaded(Assembly assembly, string appPath)
    {
        byte[] selected = ReadSelected(appPath, assembly.GetName().Name!);
        using var stream = new MemoryStream(selected, writable: false);
        using var image = new PEReader(stream);
        MetadataReader metadata = image.GetMetadataReader();
        Guid expected = metadata.GetGuid(metadata.GetModuleDefinition().Mvid);
        if (assembly.ManifestModule.ModuleVersionId != expected)
            throw new InvalidOperationException("The loaded assembly does not match the selected candidate module.");
        string hash = Convert.ToHexString(SHA256.HashData(selected));
        if (!string.IsNullOrEmpty(assembly.Location)
            && Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly.Location))) != hash)
            throw new InvalidOperationException("The loaded assembly file differs from the selected candidate.");
        return hash;
    }

    internal static byte[] ReadSelected(string appPath, string assemblyName)
    {
        using var stream = File.OpenRead(appPath);
        using var image = new PEReader(stream);
        MetadataReader metadata = image.GetMetadataReader();
        string suffix = assemblyName + ".dll";
        foreach (ManifestResourceHandle handle in metadata.ManifestResources)
        {
            ManifestResource resource = metadata.GetManifestResource(handle);
            if (!resource.Implementation.IsNil) continue;
            string name = metadata.GetString(resource.Name);
            bool compressed = name.EndsWith(suffix + ".compressed", StringComparison.OrdinalIgnoreCase);
            if (!compressed && !name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) continue;
            int resources = image.PEHeaders.CorHeader?.ResourcesDirectory.RelativeVirtualAddress
                ?? throw new InvalidOperationException("Managed resources are absent.");
            BlobReader blob = image.GetSectionData(resources).GetReader();
            blob.Offset = checked((int)resource.Offset);
            int length = blob.ReadInt32();
            if (length <= 0 || length > 128 * 1024 * 1024 || length > blob.RemainingBytes)
                throw new InvalidOperationException("Invalid embedded assembly size.");
            byte[] bytes = blob.ReadBytes(length);
            if (!compressed) return bytes;
            using var packed = new MemoryStream(bytes, writable: false);
            using var decoder = new DeflateStream(packed, CompressionMode.Decompress);
            using var unpacked = new MemoryStream();
            byte[] buffer = new byte[81920];
            int count;
            while ((count = decoder.Read(buffer)) != 0)
            {
                if (unpacked.Length + count > 128 * 1024 * 1024)
                    throw new InvalidOperationException("Embedded assembly exceeds the supported size.");
                unpacked.Write(buffer, 0, count);
            }
            return unpacked.ToArray();
        }
        return File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(appPath)!, suffix));
    }
}
