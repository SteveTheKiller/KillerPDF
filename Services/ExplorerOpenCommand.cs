using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace KillerPDF.Services;

/// <summary>The files Explorer asked KillerPDF to open, and whether Shift was down when it did.</summary>
internal sealed record ExplorerOpenRequest(IReadOnlyList<string> Paths, bool NewWindow);

/// <summary>
/// #513: KillerPDF's open verb in File Explorer. Explorer starts KillerPDF as a COM server for this class and
/// hands it the whole selection with the Shift state of the key press, so Shift+Enter opens the files in a new
/// window however quickly the key is released. A command-line verb gets one launch per file and no key state.
/// </summary>
internal static unsafe partial class ExplorerOpenServer
{
    /// <summary>The class Explorer creates for KillerPDF's open verb, registered by FileAssociationRegistry.</summary>
    internal static readonly Guid ClassId = new("CF1944D6-20B1-4AB0-B64C-C03F6AF2CD4C");

    internal static string ClassIdText => ClassId.ToString("B").ToUpperInvariant();

    /// <summary>The switch in the class's LocalServer32 command line. COM adds -Embedding after it.</summary>
    internal const string Switch = "--explorer-open";

    /// <summary>How long a server launch waits for Explorer's call before it gives up and exits.</summary>
    internal static readonly TimeSpan CallWait = TimeSpan.FromSeconds(30);

    internal static readonly StrategyBasedComWrappers Wrappers = new();

    internal static bool IsServerLaunch(IEnumerable<string> args) =>
        args.Any(arg => string.Equals(arg, Switch, StringComparison.OrdinalIgnoreCase));

    /// <summary>The class factory Explorer reaches through COM; <paramref name="onExecute"/> gets each request.</summary>
    internal static object CreateClassFactory(Action<ExplorerOpenRequest> onExecute) => new ClassFactory(onExecute);

    /// <summary>
    /// Serves one Explorer call and returns its request, or null when none came within <see cref="CallWait"/>.
    /// The class object lives in the multithreaded apartment, so Explorer's calls run on COM's own threads and
    /// nothing has to pump messages while this waits.
    /// </summary>
    internal static ExplorerOpenRequest? Serve()
    {
        ExplorerOpenRequest? received = null;
        var executed = new ManualResetEventSlim();
        object factory = CreateClassFactory(request =>
        {
            Interlocked.CompareExchange(ref received, request, null);
            executed.Set();
        });
        nint unknown = Wrappers.GetOrCreateComInterfaceForObject(factory, CreateComInterfaceFlags.None);
        // Keeps the apartment alive after the server thread ends, so Execute's reply still reaches Explorer.
        CoIncrementMTAUsage(out _);
        var server = new Thread(() => Register(unknown, executed)) { IsBackground = true, Name = "KillerPDF-ExplorerOpen" };
        server.SetApartmentState(ApartmentState.MTA);
        server.Start();
        server.Join();
        Marshal.Release(unknown);
        return Volatile.Read(ref received);
    }

    private static void Register(nint factory, ManualResetEventSlim executed)
    {
        const uint ClsctxLocalServer = 0x4, RegclsSingleUse = 0x0;
        Guid classId = ClassId;
        uint cookie;
        if (CoRegisterClassObject(&classId, factory, ClsctxLocalServer, RegclsSingleUse, &cookie) < 0) return;
        executed.Wait(CallWait);
        CoRevokeClassObject(cookie);
    }

    [GeneratedComClass]
    private sealed partial class ClassFactory(Action<ExplorerOpenRequest> onExecute) : IClassFactory
    {
        public int CreateInstance(nint outer, Guid* iid, nint* instance)
        {
            const int ClassENoAggregation = unchecked((int)0x80040110);
            *instance = 0;
            if (outer != 0) return ClassENoAggregation;
            nint unknown = Wrappers.GetOrCreateComInterfaceForObject(new OpenCommand(onExecute), CreateComInterfaceFlags.None);
            int hr = Marshal.QueryInterface(unknown, in *iid, out *instance);
            Marshal.Release(unknown);
            return hr;
        }

        public int LockServer(int keepLoaded) => 0;
    }

    [GeneratedComClass]
    private sealed partial class OpenCommand(Action<ExplorerOpenRequest> onExecute) : IExecuteCommand, IObjectWithSelection
    {
        private const uint MkShift = 0x0004;
        private IReadOnlyList<string> _paths = [];
        private uint _keyState;

        // The items are read here, while Explorer's array is certainly valid, and never kept.
        public int SetSelection(nint array)
        {
            _paths = array == 0 ? [] : ShellItems.Paths(array);
            return 0;
        }

        public int GetSelection(Guid* iid, nint* result)
        {
            const int ENoInterface = unchecked((int)0x80004002);
            *result = 0;
            return ENoInterface;
        }

        public int SetKeyState(uint keyState)
        {
            _keyState = keyState;
            return 0;
        }

        public int SetParameters(nint parameters) => 0;
        public int SetPosition(long point) => 0;
        public int SetShowWindow(int show) => 0;
        public int SetNoShowUI(int noShowUi) => 0;
        public int SetDirectory(nint directory) => 0;

        public int Execute()
        {
            onExecute(new ExplorerOpenRequest(_paths, (_keyState & MkShift) != 0));
            return 0;
        }
    }

    [LibraryImport("ole32.dll")]
    private static partial int CoRegisterClassObject(Guid* classId, nint unknown, uint context, uint flags, uint* cookie);

    [LibraryImport("ole32.dll")]
    private static partial int CoRevokeClassObject(uint cookie);

    [LibraryImport("ole32.dll")]
    private static partial int CoIncrementMTAUsage(out nint cookie);
}

/// <summary>Reads the paths of a shell item array Explorer passed, through the shell's own vtables.</summary>
internal static unsafe class ShellItems
{
    private const uint SigdnFileSystemPath = 0x80058000, SigdnParentRelativeParsing = 0x80018001;
    private static readonly Guid BindStream = new("1CEBB3AB-7C10-499A-A417-92CA16C4CB83");
    private static readonly Guid IidStream = new("0000000C-0000-0000-C000-000000000046");

    internal static IReadOnlyList<string> Paths(nint array)
    {
        uint count;
        // IShellItemArray::GetCount is slot 7, GetItemAt is slot 8.
        if (((delegate* unmanaged[Stdcall]<nint, uint*, int>)Slot(array, 7))(array, &count) < 0) return [];
        var paths = new List<string>((int)count);
        for (uint i = 0; i < count; i++)
        {
            nint item;
            if (((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)Slot(array, 8))(array, i, &item) < 0) continue;
            try
            {
                if ((DisplayName(item, SigdnFileSystemPath) ?? CopyOut(item)) is { } path) paths.Add(path);
            }
            finally { Marshal.Release(item); }
        }
        return paths;
    }

    private static nint Slot(nint instance, int slot) => (*(nint**)instance)[slot];

    private static string? DisplayName(nint item, uint form)
    {
        nint name;
        // IShellItem::GetDisplayName is slot 5.
        if (((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)Slot(item, 5))(item, form, &name) < 0 || name == 0) return null;
        try { return Marshal.PtrToStringUni(name); }
        finally { Marshal.FreeCoTaskMem(name); }
    }

    // An item with no path on disk, such as a PDF inside a zip folder, is copied out under its own name, the way
    // Explorer copies one out for a command-line verb.
    private static string? CopyOut(nint item)
    {
        Guid bind = BindStream, iid = IidStream;
        nint stream;
        // IShellItem::BindToHandler is slot 3.
        if (((delegate* unmanaged[Stdcall]<nint, nint, Guid*, Guid*, nint*, int>)Slot(item, 3))(item, 0, &bind, &iid, &stream) < 0
            || stream == 0)
            return null;
        string folder = Path.Combine(Path.GetTempPath(), "KillerPDF-Explorer", Guid.NewGuid().ToString("N"));
        try
        {
            string name = SafeName(DisplayName(item, SigdnParentRelativeParsing));
            Directory.CreateDirectory(folder);
            string target = Path.Combine(folder, name);
            using (var output = File.Create(target))
            {
                byte[] buffer = new byte[81920];
                fixed (byte* bytes = buffer)
                {
                    while (true)
                    {
                        uint read;
                        // ISequentialStream::Read is slot 3; S_FALSE or a short read marks the end.
                        int hr = ((delegate* unmanaged[Stdcall]<nint, byte*, uint, uint*, int>)Slot(stream, 3))(stream, bytes, (uint)buffer.Length, &read);
                        if (hr < 0) throw new IOException("The item could not be read.", hr);
                        if (read == 0) break;
                        output.Write(buffer, 0, (int)read);
                    }
                }
            }
            return target;
        }
        catch (IOException) { TryDelete(folder); return null; }
        catch (UnauthorizedAccessException) { TryDelete(folder); return null; }
        finally { Marshal.Release(stream); }
    }

    private static string SafeName(string? name)
    {
        name = Path.GetFileName(name ?? "");
        foreach (char invalid in Path.GetInvalidFileNameChars()) name = name.Replace(invalid, '_');
        name = name.Trim().TrimEnd('.');
        return name.Length == 0 ? "document.pdf" : name;
    }

    private static void TryDelete(string folder)
    {
        try { Directory.Delete(folder, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

[GeneratedComInterface]
[Guid("00000001-0000-0000-C000-000000000046")]
internal unsafe partial interface IClassFactory
{
    [PreserveSig] int CreateInstance(nint outer, Guid* iid, nint* instance);
    [PreserveSig] int LockServer(int keepLoaded);
}

[GeneratedComInterface]
[Guid("7F9185B0-CB92-43C5-80A9-92277A4F7B54")]
internal partial interface IExecuteCommand
{
    [PreserveSig] int SetKeyState(uint keyState);
    [PreserveSig] int SetParameters(nint parameters);
    /// <summary>POINT is two 32-bit ints, passed by value as one 64-bit argument on x64 and ARM64.</summary>
    [PreserveSig] int SetPosition(long point);
    [PreserveSig] int SetShowWindow(int show);
    [PreserveSig] int SetNoShowUI(int noShowUi);
    [PreserveSig] int SetDirectory(nint directory);
    [PreserveSig] int Execute();
}

[GeneratedComInterface]
[Guid("1C9CD5BB-98E9-4491-A60F-31AACC72B83C")]
internal unsafe partial interface IObjectWithSelection
{
    [PreserveSig] int SetSelection(nint array);
    [PreserveSig] int GetSelection(Guid* iid, nint* result);
}
