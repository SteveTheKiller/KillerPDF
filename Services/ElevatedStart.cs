using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security;

namespace KillerPDF.Services
{
    /// <summary>
    /// The one way into an elevated mode (install, silent install, update helper, machine
    /// uninstall, any runas relaunch) and the one way to start PowerShell from one.
    ///
    /// A process running as the user can set variables in HKCU\Environment that make the .NET
    /// runtime load its code: startup hooks, a profiler, another runtime root or extra deps. An
    /// elevated copy started after that reads them and runs the code as administrator through a
    /// UAC prompt that honestly names the signed publisher. The host cannot fully stop this from
    /// managed code, because the runtime acts on those variables before Main. Each app therefore
    /// also sets StartupHookSupport=false in its csproj, which is what actually closes startup
    /// hooks; the refusal here is the check that the other variables are absent, made before
    /// elevation is requested and again on the elevated side before anything is installed.
    /// </summary>
    internal static class ElevatedStart
    {
        // Exact names, then prefixes that cover the runtime's per-architecture and versioned forms
        // (DOTNET_ROOT_X64, COR_PROFILER_PATH_64, CORECLR_PROFILER_PATH_ARM64 and so on).
        private static readonly string[] Names =
        {
            "DOTNET_STARTUP_HOOKS", "DOTNET_ADDITIONAL_DEPS", "DOTNET_SHARED_STORE", "DOTNET_ADDITIONAL_PROBING_PATHS",
            "COR_ENABLE_PROFILING", "CORECLR_ENABLE_PROFILING",
        };
        private static readonly string[] Prefixes =
        {
            "DOTNET_ROOT", "COR_PROFILER", "CORECLR_PROFILER",
        };

        /// <summary>The runtime injection variables set to a non-empty value in
        /// <paramref name="environment"/>, sorted by name. An empty value does nothing to the runtime.</summary>
        internal static string[] InjectedVariables(IDictionary environment)
        {
            var found = new List<string>();
            foreach (DictionaryEntry entry in environment)
            {
                if (entry.Key is not string name || string.IsNullOrEmpty(entry.Value as string)) continue;
                if (Names.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase))
                    || Prefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                    found.Add(name);
            }
            found.Sort(StringComparer.OrdinalIgnoreCase);
            return found.ToArray();
        }

        /// <summary>Throws <see cref="SecurityException"/> when this process's environment carries a
        /// runtime injection variable. Call it before every runas start, since an elevated child built
        /// by ShellExecute inherits this environment and cannot be handed a clean one.</summary>
        internal static void RefuseInjectedEnvironment()
        {
            string[] found = InjectedVariables(Environment.GetEnvironmentVariables());
            if (found.Length != 0)
                throw new SecurityException("An administrator operation was refused because the environment sets "
                    + string.Join(", ", found) + ", which can load code into it. Remove "
                    + (found.Length == 1 ? "it" : "them") + " from your user environment variables and try again.");
        }

        /// <summary>
        /// Call first in every elevated mode, before anything is read or installed: refuses an
        /// injection environment and limits later native DLL loads without a full path to System32,
        /// so a DLL planted beside a user-writable copy of the exe is never loaded as administrator.
        /// Only elevated modes call it; a normal run keeps loading the app's own native DLLs.
        /// </summary>
        internal static void Enter()
        {
            RefuseInjectedEnvironment();
            if (!SetDefaultDllDirectories(0x00000800 /* LOAD_LIBRARY_SEARCH_SYSTEM32 */))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        /// <summary>Windows PowerShell, by full path, running <paramref name="script"/> through
        /// <see cref="ImmutablePowerShell"/> (no profile, modules only from $PSHOME\Modules), from
        /// System32, in an environment built from Windows APIs and fixed values instead of the user's.</summary>
        internal static ProcessStartInfo PowerShell(string script)
        {
            string system = Environment.GetFolderPath(Environment.SpecialFolder.System);
            var start = new ProcessStartInfo(Path.Combine(system, @"WindowsPowerShell\v1.0\powershell.exe"),
                ImmutablePowerShell.Arguments(script))
            {
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = system,
            };
            CleanEnvironment(start);
            return start;
        }

        /// <summary>Machine and account facts a script may read, each taken from a Windows API or a
        /// fixed value, never from this process's environment, which HKCU\Environment can override
        /// (a user-set windir is a known elevation trick). The account folders still belong to the
        /// signed-in user, who can redirect them; nothing here runs code from them.</summary>
        private static IEnumerable<(string name, string value)> SystemValues()
        {
            static string Folder(Environment.SpecialFolder folder) =>
                Environment.GetFolderPath(folder, Environment.SpecialFolderOption.DoNotVerify);
            string windows = Folder(Environment.SpecialFolder.Windows);
            string system = Folder(Environment.SpecialFolder.System);
            string profile = Folder(Environment.SpecialFolder.UserProfile);
            string programData = Folder(Environment.SpecialFolder.CommonApplicationData);
            yield return ("SystemRoot", windows);
            yield return ("windir", windows);
            yield return ("SystemDrive", Path.GetPathRoot(windows)!.TrimEnd('\\'));
            yield return ("ComSpec", Path.Combine(system, "cmd.exe"));
            yield return ("PATHEXT", ".COM;.EXE;.BAT;.CMD;.VBS;.VBE;.JS;.JSE;.WSF;.WSH;.MSC");
            yield return ("OS", "Windows_NT");
            yield return ("NUMBER_OF_PROCESSORS", Environment.ProcessorCount.ToString(System.Globalization.CultureInfo.InvariantCulture));
            yield return ("PROCESSOR_ARCHITECTURE", RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.X86 => "x86", Architecture.Arm64 => "ARM64", _ => "AMD64",
            });
            yield return ("ProgramData", programData);
            yield return ("ALLUSERSPROFILE", programData);
            yield return ("ProgramFiles", Folder(Environment.SpecialFolder.ProgramFiles));
            yield return ("ProgramFiles(x86)", Folder(Environment.SpecialFolder.ProgramFilesX86));
            yield return ("ProgramW6432", Folder(Environment.SpecialFolder.ProgramFiles));
            yield return ("CommonProgramFiles", Folder(Environment.SpecialFolder.CommonProgramFiles));
            yield return ("CommonProgramFiles(x86)", Folder(Environment.SpecialFolder.CommonProgramFilesX86));
            yield return ("CommonProgramW6432", Folder(Environment.SpecialFolder.CommonProgramFiles));
            yield return ("COMPUTERNAME", Environment.MachineName);
            yield return ("USERNAME", Environment.UserName);
            yield return ("USERDOMAIN", Environment.UserDomainName);
            yield return ("USERPROFILE", profile);
            yield return ("HOMEDRIVE", Path.GetPathRoot(profile)?.TrimEnd('\\') ?? string.Empty);
            yield return ("HOMEPATH", profile.Length > 2 ? profile.Substring(2) : string.Empty);
            yield return ("APPDATA", Folder(Environment.SpecialFolder.ApplicationData));
            yield return ("LOCALAPPDATA", Folder(Environment.SpecialFolder.LocalApplicationData));
        }

        /// <summary>Replaces the environment of <paramref name="start"/> (which must not use
        /// ShellExecute) with System32 on the path, PowerShell's own module folder, the Windows temp
        /// folder (the update guard makes a private folder inside it) and <see cref="SystemValues"/>.</summary>
        internal static void CleanEnvironment(ProcessStartInfo start)
        {
            if (start.UseShellExecute) throw new InvalidOperationException("A clean environment needs UseShellExecute = false.");
            var values = SystemValues().Where(pair => !string.IsNullOrEmpty(pair.value)).ToList();
            string windows = values.First(pair => pair.name == "SystemRoot").value;
            string system = Environment.GetFolderPath(Environment.SpecialFolder.System, Environment.SpecialFolderOption.DoNotVerify);
            start.Environment.Clear();
            foreach (var (name, value) in values) start.Environment[name] = value;
            start.Environment["Path"] = string.Join(";", system, windows, Path.Combine(system, "Wbem"),
                Path.Combine(system, @"WindowsPowerShell\v1.0"));
            start.Environment["PSModulePath"] = Path.Combine(system, @"WindowsPowerShell\v1.0\Modules");
            start.Environment["TEMP"] = start.Environment["TMP"] = Path.Combine(windows, "Temp");
        }

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetDefaultDllDirectories(uint directoryFlags);
    }
}
