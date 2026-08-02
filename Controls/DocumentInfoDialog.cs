using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using PdfSharpCore.Pdf;

namespace KillerPDF
{
    // Read/edit the PDF Document Info dictionary (Title, Author, Subject, Keywords, Creator). Themed via
    // DialogChrome, no preview pane. Producer/dates/structure are shown read-only.
    internal sealed class DocumentInfoDialog : Window
    {
        private readonly PdfDocument _doc;
        private TextBox _title = null!, _author = null!, _subject = null!, _keywords = null!, _creator = null!;

        public bool Saved { get; private set; }

        public DocumentInfoDialog(Window owner, PdfDocument doc, string? filePath)
        {
            _doc = doc;
            Title = "KillerPDF - " + L("Str_DocInfo_Suffix");
            Width = 460;
            SizeToContent = SizeToContent.Height;
            UseLayoutRounding = true;
            DialogChrome.Configure(this, owner);
            BuildUi(filePath);
        }

        private void BuildUi(string? filePath)
        {
            var body = new StackPanel { Margin = new Thickness(20, 6, 20, 16) };

            _title    = AddField(body, L("Str_DocInfo_Title"),    _doc.Info.Title);
            _author   = AddField(body, L("Str_DocInfo_Author"),   _doc.Info.Author);
            _subject  = AddField(body, L("Str_DocInfo_Subject"),  _doc.Info.Subject);
            _keywords = AddField(body, L("Str_DocInfo_Keywords"), _doc.Info.Keywords, wrap: true);
            _creator  = AddField(body, L("Str_DocInfo_Creator"),  _doc.Info.Creator);

            body.Children.Add(new TextBlock
            {
                Text = BuildSummary(filePath),
                FontFamily = UiKit.MonoFont, FontSize = 11,
                Foreground = UiKit.Brush("MutedTextBrush"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 12, 0, 0)
            });

            var cancel = UiKit.Make(L("Str_DocInfo_Cancel"), accent: false);
            cancel.Click += (_, _2) => { DialogResult = false; Close(); };
            cancel.IsCancel = true;   // Esc
            var save = UiKit.Make(L("Str_DocInfo_Save"), accent: true);
            save.Click += (_, _2) => SaveAndClose();
            save.IsDefault = true;    // Enter
            var row = UiKit.ButtonRow(cancel, save);
            row.Margin = new Thickness(0, 16, 0, 0);
            body.Children.Add(row);

            Content = DialogChrome.Frame(this, Owner, "KillerPDF - " + L("Str_DocInfo_Suffix"),
                () => { DialogResult = false; Close(); }, body);

            Loaded += (_, _2) => _title.Focus();
        }

        private static TextBox AddField(StackPanel host, string label, string? value, bool wrap = false)
        {
            host.Children.Add(UiKit.GroupLabel(label));
            var f = UiKit.Field();
            f.Text = value ?? "";
            f.Margin = new Thickness(0, 0, 0, 8);
            // Every field wraps and grows with its content up to a cap, then scrolls - so long titles,
            // subjects, or keyword lists aren't cramped on a single line. Enter is not a newline (each value
            // stays a single metadata string). The `wrap` hint just gives the long-form fields more room.
            f.TextWrapping = TextWrapping.Wrap;
            f.AcceptsReturn = false;
            f.VerticalContentAlignment = VerticalAlignment.Top;
            f.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            f.MaxHeight = wrap ? 110 : 72;   // grow up to ~5 lines (keywords) / ~3 lines (others), then scroll
            host.Children.Add(f);
            return f;
        }

        private string BuildSummary(string? filePath)
        {
            var parts = new List<string>();
            string producer = ""; try { producer = _doc.Info.Producer ?? ""; } catch { }
            if (producer.Length > 0) parts.Add($"Producer: {producer}");
            parts.Add($"{_doc.PageCount} pages");
            parts.Add($"PDF {_doc.Version / 10}.{_doc.Version % 10}");
            try { var d = _doc.Info.CreationDate; if (d != default) parts.Add($"created {d:yyyy-MM-dd HH:mm}"); } catch { }
            try { if (!string.IsNullOrEmpty(filePath) && File.Exists(filePath)) parts.Add($"{new FileInfo(filePath).Length / 1024.0:N0} KB"); } catch { }
            return string.Join("\n", parts);
        }

        private void SaveAndClose()
        {
            _doc.Info.Title    = _title.Text;
            _doc.Info.Author   = _author.Text;
            _doc.Info.Subject  = _subject.Text;
            _doc.Info.Keywords = _keywords.Text;
            _doc.Info.Creator  = _creator.Text;
            Saved = true;
            Close();
        }

        private static string L(string key) => Application.Current?.TryFindResource(key) as string ?? key;
    }
}
