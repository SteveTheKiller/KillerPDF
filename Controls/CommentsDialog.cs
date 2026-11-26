using System.Windows;
using System.Windows.Controls;
using KillerPdf.Engine.Documents;
using KillerPDF.Services;

namespace KillerPDF.Controls;

// Comments (#288): every review comment in the PDF with its replies. A comment's text can be
// edited, answered with a reply or deleted, and new notes can be added to the current page.
// Nothing is written until Save, and the whole save is one undo step.
internal sealed class CommentsDialog : Window
{
    private sealed class Node
    {
        public PdfCommentInfo? Source;
        public string? Name;
        public int PageIndex;
        public string Contents = "";
        public string Original = "";
        public string? Author;
        public string Subtype = "Text";
        public double? X;
        public double? Y;
        public Node? Parent;
        public readonly List<Node> Replies = [];
    }

    private readonly List<Node> _roots = [];
    private readonly List<PdfCommentInfo> _removed = [];
    private readonly ListBox _list = new();
    private readonly TextBox _text = UiKit.Field();
    private readonly TextBlock _empty;
    private readonly Button _reply;
    private readonly Button _delete;
    private readonly Button _goTo;
    private readonly int _currentPage;
    private readonly string? _author;
    private readonly Action<int> _navigate;
    private bool _refreshing;

    internal bool Saved { get; private set; }

    internal CommentsDialog(Window owner, IReadOnlyList<PdfCommentThread> threads, int currentPage,
        string? author, Action<int> navigate)
    {
        _currentPage = currentPage;
        _author = author;
        _navigate = navigate;
        foreach (PdfCommentThread thread in threads)
            if (Build(thread, null) is Node node) _roots.Add(node);

        Title = "KillerPDF - " + L("Str_DocumentTools_Comments");
        Width = 640;
        Height = 560;
        MinWidth = 480;
        MinHeight = 420;
        DialogFrame.Configure(this, owner, resizable: true);

        var body = new DockPanel { Margin = new Thickness(20, 6, 20, 16), LastChildFill = true };

        var hint = new TextBlock
        {
            Text = L("Str_Comments_Hint"),
            TextWrapping = TextWrapping.Wrap,
            FontFamily = UiKit.UiFont,
            FontSize = 12,
            Margin = new Thickness(0, 0, 0, 10)
        };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
        DockPanel.SetDock(hint, Dock.Top);
        body.Children.Add(hint);

        var cancel = UiKit.Make(L("Str_Btn_Cancel"), accent: false);
        cancel.IsCancel = true;
        cancel.Click += (_, _) => Close();
        var save = UiKit.Make(L("Str_Btn_Save"), accent: true);
        save.Click += (_, _) => { Saved = true; Close(); };
        DockPanel footer = DialogParts.Footer([cancel, save]);
        DockPanel.SetDock(footer, Dock.Bottom);
        body.Children.Add(footer);

        // The list's actions are icons in its heading, named in their tooltips.
        Button add = DialogParts.IconAction("Str_Comments_Add", 0xE710, (_, _) => AddComment());
        _reply = DialogParts.IconAction("Str_Comments_Reply", 0xE97A, (_, _) => AddReply());
        _delete = DialogParts.IconAction("Str_Ctx_BmDelete", 0xE74D, (_, _) => DeleteSelected());
        _goTo = DialogParts.IconAction("Str_Comments_GoTo", 0xE72A,
            (_, _) => { if (Selected is Node node) _navigate(node.PageIndex); });
        DockPanel listHeader = DialogParts.Header(L("Str_DocumentTools_Comments"), add, _reply, _delete, _goTo);
        DockPanel.SetDock(listHeader, Dock.Top);
        body.Children.Add(listHeader);

        _text.AcceptsReturn = true;
        _text.TextWrapping = TextWrapping.Wrap;
        _text.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _text.VerticalContentAlignment = VerticalAlignment.Top;
        System.Windows.Automation.AutomationProperties.SetName(_text, L("Str_Comments_Text"));
        _text.TextChanged += (_, _) =>
        {
            if (_refreshing || Selected is not Node node) return;
            node.Contents = _text.Text;
            if (_list.SelectedItem is ListBoxItem item && item.Content is StackPanel row
                && row.Children.Count > 1 && row.Children[1] is TextBlock preview)
                preview.Text = Preview(node.Contents);
        };
        // The selected comment's text in its own well under the list.
        var editor = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        DockPanel textHeader = DialogParts.Header(L("Str_Comments_Text"));
        DockPanel.SetDock(textHeader, Dock.Top);
        editor.Children.Add(textHeader);
        Grid textWell = DialogParts.Well(_text);
        textWell.Height = 104;
        editor.Children.Add(textWell);
        DockPanel.SetDock(editor, Dock.Bottom);
        body.Children.Add(editor);

        ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
        _list.SelectionChanged += (_, _) => SelectionChanged();
        _empty = DialogParts.EmptyHint("Str_Comments_None");
        body.Children.Add(DialogParts.Well(_list, _empty));

        Content = DialogFrame.Frame(this, owner, Title, Close, body);
        Reload(null);
    }

    private Node? Selected => (_list.SelectedItem as ListBoxItem)?.Tag as Node;

    // Popups hold a copy of their parent's text; they are edited and removed with the parent.
    private static Node? Build(PdfCommentThread thread, Node? parent)
    {
        PdfCommentInfo comment = thread.Comment;
        if (comment.Subtype == "Popup") return null;
        var node = new Node
        {
            Source = comment,
            Name = comment.Name,
            PageIndex = comment.PageIndex,
            Contents = comment.Contents,
            Original = comment.Contents,
            Author = comment.Author,
            Subtype = comment.Subtype,
            X = comment.Bounds?.Left,
            Y = comment.Bounds?.Top,
            Parent = parent
        };
        foreach (PdfCommentThread reply in thread.Replies)
            if (Build(reply, node) is Node child) node.Replies.Add(child);
        return node;
    }

    private void Reload(Node? select)
    {
        _refreshing = true;
        _list.Items.Clear();
        foreach (Node root in _roots) AddRows(root, 0, select);
        _empty.Visibility = _list.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_list.SelectedIndex < 0 && _list.Items.Count > 0) _list.SelectedIndex = 0;
        _refreshing = false;
        SelectionChanged();
    }

    private void AddRows(Node node, int depth, Node? select)
    {
        var header = new TextBlock
        {
            Text = string.Format(L("Str_Comments_Header"), node.PageIndex + 1,
                string.IsNullOrWhiteSpace(node.Author) ? L("Str_Comments_NoAuthor") : node.Author,
                Kind(node.Subtype)),
            FontFamily = UiKit.UiFont,
            FontSize = 11
        };
        header.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
        var preview = new TextBlock
        {
            Text = Preview(node.Contents),
            FontFamily = UiKit.UiFont,
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        var row = new StackPanel();
        row.Children.Add(header);
        row.Children.Add(preview);
        var item = new ListBoxItem
        {
            Content = row,
            Tag = node,
            Padding = new Thickness(8 + depth * 18, 5, 8, 5)
        };
        item.SetResourceReference(Control.ForegroundProperty, "TextBrush");
        _list.Items.Add(item);
        if (ReferenceEquals(node, select)) _list.SelectedItem = item;
        foreach (Node reply in node.Replies) AddRows(reply, depth + 1, select);
    }

    private static string Preview(string text)
    {
        string line = text.ReplaceLineEndings(" ").Trim();
        return line.Length == 0 ? "..." : line;
    }

    private static string Kind(string subtype) => subtype switch
    {
        "Text" => L("Str_Comments_Note"),
        "Highlight" => L("Str_Annot_Highlight"),
        "Underline" => L("Str_Annot_Underline"),
        "StrikeOut" => L("Str_Annot_Strikethrough"),
        "FreeText" => L("Str_Annot_TextBox"),
        "Ink" => L("Str_Annot_Drawing"),
        _ => subtype
    };

    private void SelectionChanged()
    {
        Node? node = Selected;
        _refreshing = true;
        _text.Text = node?.Contents ?? "";
        _refreshing = false;
        _text.IsEnabled = node is not null;
        _delete.IsEnabled = node is not null;
        _goTo.IsEnabled = node is not null;
        // A reply points at its comment by name, so a comment saved without one cannot take replies.
        bool canReply = node is not null && !string.IsNullOrEmpty(node.Name);
        _reply.IsEnabled = canReply;
        // The icon's tooltip is its name, so it goes back to Reply rather than empty.
        _reply.ToolTip = node is not null && !canReply ? L("Str_Comments_NoReply") : L("Str_Comments_Reply");
    }

    internal void AddComment()
    {
        var node = new Node { Name = NewName(), PageIndex = _currentPage, Author = _author };
        _roots.Add(node);
        Reload(node);
        _text.Focus();
    }

    /// <summary>Selects the comment a right-click card was opened on; false when the list does not hold it.</summary>
    internal bool Select(PdfCommentInfo comment)
    {
        foreach (ListBoxItem item in _list.Items)
            if (item.Tag is Node { Source: { } source } && source.PageIndex == comment.PageIndex
                && source.AnnotationIndex == comment.AnnotationIndex)
            {
                _list.SelectedItem = item;
                _list.ScrollIntoView(item);
                _text.Focus();
                return true;
            }
        return false;
    }

    /// <summary>Starts a reply to the selected comment, as the Reply button does.</summary>
    internal void Reply() => AddReply();

    private void AddReply()
    {
        if (Selected is not Node parent || string.IsNullOrEmpty(parent.Name)) return;
        var node = new Node
        {
            Name = NewName(),
            PageIndex = parent.PageIndex,
            Author = _author,
            X = parent.X,
            Y = parent.Y,
            Parent = parent
        };
        parent.Replies.Add(node);
        Reload(node);
        _text.Focus();
    }

    private void DeleteSelected()
    {
        if (Selected is not Node node) return;
        int index = _list.SelectedIndex;
        void Forget(Node gone)
        {
            if (gone.Source is not null) _removed.Add(gone.Source);
            foreach (Node reply in gone.Replies) Forget(reply);
        }
        Forget(node);
        (node.Parent?.Replies ?? _roots).Remove(node);
        Reload(null);
        if (_list.Items.Count > 0) _list.SelectedIndex = Math.Min(index, _list.Items.Count - 1);
    }

    private static string NewName() => "killerpdf-" + Guid.NewGuid().ToString("N");

    /// <summary>What Save changes: new text, removals, then new notes in thread order. A new note
    /// left empty is dropped with its replies.</summary>
    internal IReadOnlyList<CommentEdit> Edits
    {
        get
        {
            var edits = new List<CommentEdit>();
            void Walk(Node node)
            {
                if (node.Source is not null)
                {
                    if (node.Contents != node.Original)
                        edits.Add(new CommentTextEdit(node.Source, node.Contents));
                }
                else
                {
                    if (string.IsNullOrWhiteSpace(node.Contents)) return;
                    edits.Add(new CommentAddition(node.PageIndex, node.Contents, node.Name!,
                        node.Parent?.Name, node.X, node.Y));
                }
                foreach (Node reply in node.Replies) Walk(reply);
            }
            foreach (Node root in _roots) Walk(root);
            edits.AddRange(_removed.Select(comment => new CommentRemoval(comment)));
            return edits;
        }
    }

    private static string L(string key) =>
        Application.Current?.TryFindResource(key) as string ?? key;
}
