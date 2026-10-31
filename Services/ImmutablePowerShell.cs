using System;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace KillerPDF.Services
{
    internal static class ImmutablePowerShell
    {
        internal static string Arguments(string script)
        {
            using var buffer = new MemoryStream();
            byte[] bytes = Encoding.UTF8.GetBytes(script);
            using (var gzip = new GZipStream(buffer, CompressionLevel.Optimal, leaveOpen: true))
                gzip.Write(bytes, 0, bytes.Length);
            // First, before any cmdlet can autoload: modules come only from Windows PowerShell's own
            // folder, which only administrators can write. The inherited PSModulePath starts with the
            // user's Documents\WindowsPowerShell\Modules, where any program running as the user could
            // plant a module named like New-Object and have it run inside an elevated helper.
            // An assignment and a .NET call cannot be shadowed by a module.
            string bootstrap = "$env:PSModulePath=[IO.Path]::Combine($PSHOME,'Modules');"
                + "$stream=[IO.MemoryStream]::new([Convert]::FromBase64String('"
                + Convert.ToBase64String(buffer.ToArray())
                + "'));$reader=[IO.StreamReader]::new([IO.Compression.GZipStream]::new($stream,[IO.Compression.CompressionMode]::Decompress));"
                + "try{&([scriptblock]::Create($reader.ReadToEnd()))}finally{$reader.Dispose();$stream.Dispose()}";
            // Plain text in double quotes rather than -EncodedCommand, whose UTF-16 and second Base64
            // nearly triple the length of a command line that has a hard limit. The bootstrap holds
            // no double quote or backslash, and the script itself is safely inside the Base64.
            string arguments = "-NoLogo -NoProfile -NonInteractive -Command \"" + bootstrap + "\"";
            if (arguments.Length > 30000) throw new IOException("The immutable helper exceeds the Windows command limit.");
            return arguments;
        }
    }
}
