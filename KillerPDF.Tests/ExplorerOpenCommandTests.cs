using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

// #513: Explorer creates the open verb's command handler through its class factory, passes the selection as a shell
// item array, sets the key state and calls Execute. These tests make the same calls through the handler's COM
// vtables, with item arrays Windows itself builds, so the interface layouts are checked against the real shell.
public sealed unsafe class ExplorerOpenCommandTests : IDisposable
{
    private const uint MkShift = 0x0004;
    private const uint MkControl = 0x0008;
    private static readonly Guid IidClassFactory = new("00000001-0000-0000-C000-000000000046");
    private static readonly Guid IidExecuteCommand = new("7F9185B0-CB92-43C5-80A9-92277A4F7B54");
    private static readonly Guid IidObjectWithSelection = new("1C9CD5BB-98E9-4491-A60F-31AACC72B83C");

    private readonly string _folder = Directory.CreateTempSubdirectory("kp-explorer-open-").FullName;

    [Fact]
    public void ShiftEnterOpensTheWholeSelectionInANewWindow()
    {
        string[] files = MakeFiles("a.pdf", "b.pdf", "c.pdf");

        ExplorerOpenRequest? request = Invoke(files, MkShift);

        Assert.NotNull(request);
        Assert.True(request.NewWindow);
        Assert.Equal(files, request.Paths);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(MkControl)]
    public void EnterWithoutShiftOpensTheSelectionAsTabs(uint keyState)
    {
        string[] files = MakeFiles("a.pdf", "b.pdf");

        ExplorerOpenRequest? request = Invoke(files, keyState);

        Assert.NotNull(request);
        Assert.False(request.NewWindow);
        Assert.Equal(files, request.Paths);
    }

    // A PDF inside a zip folder has no path on disk, so its bytes are copied out under its own name.
    [Fact]
    public void AFileInsideAZipFolderIsCopiedOut()
    {
        byte[] content = "%PDF-1.7 zipped"u8.ToArray();
        string zip = Path.Combine(_folder, "docs.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        using (var entry = archive.CreateEntry("inside.pdf").Open())
            entry.Write(content);

        ExplorerOpenRequest? request = Invoke([Path.Combine(zip, "inside.pdf")], 0);

        Assert.NotNull(request);
        string copied = Assert.Single(request.Paths);
        Assert.Equal("inside.pdf", Path.GetFileName(copied));
        Assert.Equal(content, File.ReadAllBytes(copied));
        Directory.Delete(Path.GetDirectoryName(copied)!, recursive: true);
    }

    private string[] MakeFiles(params string[] names)
    {
        var paths = names.Select(name => Path.Combine(_folder, name)).ToArray();
        foreach (string path in paths) File.WriteAllText(path, "%PDF-1.7");
        return paths;
    }

    private static ExplorerOpenRequest? Invoke(string[] items, uint keyState)
    {
        ExplorerOpenRequest? received = null;
        object factory = ExplorerOpenServer.CreateClassFactory(request => received = request);
        nint factoryUnknown = ExplorerOpenServer.Wrappers.GetOrCreateComInterfaceForObject(factory, CreateComInterfaceFlags.None);
        nint selection = ShellItemArray(items);
        nint classFactory = 0, command = 0, withSelection = 0;
        try
        {
            classFactory = QueryInterface(factoryUnknown, IidClassFactory);
            Guid iid = IidExecuteCommand;
            // IClassFactory::CreateInstance is slot 3.
            Check(((delegate* unmanaged[Stdcall]<nint, nint, Guid*, nint*, int>)Slot(classFactory, 3))(classFactory, 0, &iid, &command));
            withSelection = QueryInterface(command, IidObjectWithSelection);
            // IObjectWithSelection::SetSelection is slot 3.
            Check(((delegate* unmanaged[Stdcall]<nint, nint, int>)Slot(withSelection, 3))(withSelection, selection));
            // IExecuteCommand: SetKeyState is slot 3, Execute is slot 9.
            Check(((delegate* unmanaged[Stdcall]<nint, uint, int>)Slot(command, 3))(command, keyState));
            Check(((delegate* unmanaged[Stdcall]<nint, int>)Slot(command, 9))(command));
        }
        finally
        {
            foreach (nint pointer in new[] { withSelection, command, classFactory, selection, factoryUnknown })
                if (pointer != 0) Marshal.Release(pointer);
        }
        return received;
    }

    private static nint Slot(nint instance, int slot) => (*(nint**)instance)[slot];

    private static nint QueryInterface(nint unknown, Guid iid)
    {
        Check(Marshal.QueryInterface(unknown, in iid, out nint result));
        return result;
    }

    private static void Check(int hr) => Marshal.ThrowExceptionForHR(hr);

    private static nint ShellItemArray(string[] items)
    {
        var lists = new nint[items.Length];
        try
        {
            for (int i = 0; i < items.Length; i++)
                Check(SHParseDisplayName(items[i], 0, out lists[i], 0, out _));
            fixed (nint* first = lists)
            {
                Check(SHCreateShellItemArrayFromIDLists((uint)lists.Length, first, out nint array));
                return array;
            }
        }
        finally
        {
            foreach (nint list in lists) if (list != 0) Marshal.FreeCoTaskMem(list);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName(string name, nint bindContext, out nint idList, uint sfgaoIn, out uint sfgaoOut);

    [DllImport("shell32.dll")]
    private static extern int SHCreateShellItemArrayFromIDLists(uint count, nint* idLists, out nint array);

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }
}
