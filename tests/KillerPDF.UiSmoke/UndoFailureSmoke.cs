using System.IO;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Windows;
using System.Windows.Controls;
using static DocumentStateSmoke;

internal static class UndoFailureSmoke
{
    internal static int Run(Window owner, string appPath, string folder)
    {
        const BindingFlags members = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
        string source = Path.Combine(folder, "history-storage.pdf");
        File.WriteAllBytes(source, PageDeletionSmoke.Fixture());
        owner.GetType().GetMethod("OpenFromExternal")!.Invoke(owner, [source]);
        var pages = (ListBox)owner.FindName("PageList");
        Wait(() => pages.Items.Count == 5, "history fixture pages");
        pages.SelectedItems.Clear();
        pages.SelectedIndex = 0;
        Wait(() => ((Button)owner.FindName("MovePageDownBtn")).IsEnabled, "page reorder ready");
        Click(owner, "MovePageDownBtn");
        Save(owner);
        if (!ReadText(source).Contains("PAGE_2", StringComparison.Ordinal))
            throw new InvalidOperationException("The document edit did not reorder the fixture.");
        string temp = (string)owner.GetType().Assembly.GetType("KillerPDF.App", true)!
            .GetProperty("TempDir", members)!.GetValue(null)!;
        if (!Path.GetFullPath(temp).StartsWith(Path.GetFullPath(folder) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Undo storage fixture must own its isolated session directory.");
        foreach ((string action, string expected, string retry) in new[]
        {
            ("UndoBtn", "PAGE_1", "undo"), ("RedoBtn", "PAGE_2", "redo")
        })
        {
            byte[] before = File.ReadAllBytes(source);
            var directory = new DirectoryInfo(temp);
            DirectorySecurity security = directory.GetAccessControl(AccessControlSections.Access);
            var deny = new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.CreateFiles,
                InheritanceFlags.None, PropagationFlags.None, AccessControlType.Deny);
            security.AddAccessRule(deny);
            bool refused = false;
            try
            {
                directory.SetAccessControl(security);
                try { Click(owner, action); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { refused = true; }
            }
            finally
            {
                security.RemoveAccessRuleSpecific(deny);
                directory.SetAccessControl(security);
            }
            if (!refused) throw new InvalidOperationException("The " + retry + " storage failure was not reached.");
            if (!before.AsSpan().SequenceEqual(File.ReadAllBytes(source)))
                throw new InvalidOperationException("A failed " + retry + " changed the saved source.");
            Click(owner, action);
            Save(owner);
            if (!ReadText(source).Contains(expected, StringComparison.Ordinal))
                throw new InvalidOperationException("Failed " + retry + " consumed history instead of allowing retry.");
        }
        File.WriteAllText(Path.Combine(folder, "result.txt"), "PASS real document Undo/Redo snapshot storage failure, source preservation and successful retry. App="
            + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(appPath))));
        return 0;
    }
}
