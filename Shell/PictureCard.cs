using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using KillerFamily.Controls;
using KillerPDF.Services;

namespace KillerPDF.Shell
{
    // The right-click card for a picture, both one that is part of the page and one placed on it. The header says it
    // is a picture, its size in pixels and its page; quick buttons copy, cut and delete it, and a placed picture also
    // raises, lowers and turns left, right and upside down. Save picture writes it to a file; a placed picture can be
    // replaced by another file in the same spot.
    public partial class MainWindow
    {
        // placed: the picture is an ImageAnnotation on the page; otherwise it is the page's own picture under the
        // viewer's source selection.
        private void AddPictureCard(ImageAnnotation picture, bool placed)
        {
            if (TryFindResource("CardMenu") is Style card) _ctxMenu.Style = card;
            bool canRemove = placed || ActiveViewer.CanDeleteSelectedSourceImageExt;
            _ctxMenu.Items.Add(CardSlot(WithHeaderDelete(PictureCardHeader(picture), canRemove), new Thickness(4, 2, 4, 6)));

            // Family card grid (KillerUI CardBuilder.TileGroups): three across, one group per row, most used first.
            // Delete sits in the header.
            var clipboard = new List<CardTile>
            {
                new("\uE8C8", Loc("Str_Ctx_Copy"), "Ctrl:C", CopySelectedAnnotations, false, true),
                new("\uE8C6", Loc("Str_Ctx_Cut"), placed ? null : "Ctrl:X", placed ? CutSelectedAnnotations : CutSelectedSourceImage, false, canRemove),
            };
            var turns = new List<CardTile>();
            var file = new List<CardTile> { new("\uE74E", Loc("Str_Ctx_SavePicture"), null, () => SavePicture(picture), false, true) };
            var layers = new List<CardTile>();
            if (placed)
            {
                clipboard.Add(new("\uE77F", Loc("Str_Ctx_Paste"), "Ctrl:V", () => PasteAnnotations(picture.PageIndex), false, MarkupToPaste()));
                turns.Add(new("\uE7AD", Loc("Str_Ctx_RotateCCWShort"), null, () => TurnPicture(picture, 270), true, true));
                turns.Add(new("\uE895", Loc("Str_Ctx_UpsideDown"), null, () => TurnPicture(picture, 180), false, true));
                turns.Add(new("\uE7AD", Loc("Str_Ctx_RotateCWShort"), null, () => TurnPicture(picture, 90), false, true));
                file.Add(new("\uE8AB", Loc("Str_Ctx_ReplacePicture"), null, () => ReplacePicture(picture), false, true));
                _annotations.TryGetValue(picture.PageIndex, out var pageList);
                int idx = pageList?.IndexOf(picture) ?? -1;
                bool canRaise = pageList is not null && idx >= 0 && OverlapNeighbor(pageList, idx, picture, +1) >= 0;
                bool canLower = pageList is not null && idx >= 0 && OverlapNeighbor(pageList, idx, picture, -1) >= 0;
                layers.Add(new("\uE70E", Loc("Str_Ctx_Raise"), null, () => MoveAnnotationLayer(picture, +1), false, canRaise));
                layers.Add(new("\uE70D", Loc("Str_Ctx_Lower"), null, () => MoveAnnotationLayer(picture, -1), false, canLower));
            }
            var strip = EditableTileGroups(_ctxMenu, "Picture", ["\uE8C8", "\uE8C6"], ["\uE77F"], clipboard, turns, file, layers);
            _ctxMenu.Items.Add(CardSlot(strip, new Thickness(0)));
            FocusFirstActionFromKeyboard(_ctxMenu, strip);
        }

        private FrameworkElement PictureCardHeader(ImageAnnotation picture)
        {
            var title = new TextBlock { Text = Loc("Str_Card_Picture"), FontSize = 14, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
            title.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            string meta = string.Format(Loc("Str_PageLabel"), picture.PageIndex + 1);
            if (PicturePixels(picture) is { } size) meta = $"{size.Width} × {size.Height} px · " + meta;
            var sub = new TextBlock { Text = meta, FontSize = 11, Margin = new Thickness(0, 1, 0, 0) };
            sub.SetResourceReference(TextBlock.ForegroundProperty, "DimTextBrush");
            var glyph = new TextBlock
            {
                Text = "", FontSize = 18, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center,
                FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets")
            };
            glyph.SetResourceReference(TextBlock.ForegroundProperty, "PrimaryBrush");
            var text = new StackPanel();
            text.Children.Add(title);
            text.Children.Add(sub);
            var header = new DockPanel();
            DockPanel.SetDock(glyph, Dock.Left);
            header.Children.Add(glyph);
            header.Children.Add(text);
            System.Windows.Automation.AutomationProperties.SetName(header, title.Text + ", " + meta);
            return header;
        }

        // The picture's own pixel size, as drawn, or null when its bytes cannot be read.
        private static (int Width, int Height)? PicturePixels(ImageAnnotation picture)
        {
            try
            {
                byte[] data = Convert.FromBase64String(picture.ImageData);
                int w, h;
                if (data.AsSpan().StartsWith("%PDF-"u8))
                {
                    var asset = PdfAnnotationImageSource.ReadPdfAsset(data);
                    (w, h) = ((int)asset.Width, (int)asset.Height);
                }
                else
                {
                    var frame = BitmapFrame.Create(new MemoryStream(data), BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
                    (w, h) = (frame.PixelWidth, frame.PixelHeight);
                }
                return picture.Rotation % 180 == 0 ? (w, h) : (h, w);
            }
            catch (Exception) { return null; }
        }

        private void CutSelectedAnnotations()
        {
            CopySelectedAnnotations();
            DeleteSelected();
        }

        private void TurnPicture(ImageAnnotation picture, int degrees)
        {
            ActiveViewer.PushPageSnapshotUndoExt(picture.PageIndex);
            PictureTurn.Turn(picture, degrees);
            RefreshPicture(picture);
        }

        private void RefreshPicture(ImageAnnotation picture)
        {
            MarkDirty();
            RenderAllAnnotations(picture.PageIndex);
            ClearSelection();
            SelectAnnotation(picture, new Rect(picture.Position.X, picture.Position.Y,
                picture.SourceWidth * picture.Scale, picture.SourceHeight * picture.Scale));
        }

        private void SavePicture(ImageAnnotation picture)
        {
            try
            {
                var (bytes, extension) = PictureFile.Bytes(picture.ImageData, EncodePdfPictureAsPng);
                var picker = new Controls.FileDialog(Controls.FileDialogMode.Save)
                {
                    Title = Loc("Str_Ctx_SavePicture"),
                    Filter = Loc("Str_Filter_Images") + "|*" + extension,
                    DefaultExt = extension.TrimStart('.'),
                    FileName = (_currentFile is { Length: > 0 } name ? Path.GetFileNameWithoutExtension(name) : "picture")
                        + "-p" + (picture.PageIndex + 1) + extension
                };
                if (picker.ShowDialog(this) != true || string.IsNullOrEmpty(picker.FileName)) return;
                File.WriteAllBytes(picker.FileName, bytes);
                SetStatus(Path.GetFileName(picker.FileName));
            }
            catch (Exception ex)
            {
                KillerDialog.Show(this, Loc("Str_Err_SavePictureFailed") + "\n" + ex.Message, "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // A picture lifted from the page is its own one-page PDF: drawn at the image's own size, written as a PNG.
        private static byte[] EncodePdfPictureAsPng(byte[] pdf)
        {
            var asset = PdfAnnotationImageSource.ReadPdfAsset(pdf);
            var bitmap = PdfAnnotationImageSource.RenderPdf(pdf, asset.Width, asset.Height);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            return stream.ToArray();
        }

        // Swaps a placed picture's file, keeping its spot and width; the height follows the new picture's shape.
        private void ReplacePicture(ImageAnnotation picture)
        {
            var picker = new Controls.FileDialog(Controls.FileDialogMode.Open)
            {
                Title = Loc("Str_Ctx_ReplacePicture"),
                Filter = Loc("Str_Filter_Images") + "|*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.tif;*.tiff"
            };
            if (picker.ShowDialog(this) != true || picker.FileNames.Length == 0) return;
            try
            {
                byte[] bytes = File.ReadAllBytes(picker.FileNames[0]);
                var frame = BitmapFrame.Create(new MemoryStream(bytes), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                if (frame.PixelWidth <= 0 || frame.PixelHeight <= 0) throw new InvalidDataException(Loc("Str_Err_LoadImageFailed"));
                ActiveViewer.PushPageSnapshotUndoExt(picture.PageIndex);
                double width = picture.SourceWidth * picture.Scale;
                picture.ImageData = Convert.ToBase64String(bytes);
                picture.Rotation = 0;
                picture.SourceWidth = frame.PixelWidth;
                picture.SourceHeight = frame.PixelHeight;
                picture.Scale = width / frame.PixelWidth;
                picture.CachedBitmap = null;
                RefreshPicture(picture);
            }
            catch (Exception ex)
            {
                KillerDialog.Show(this, Loc("Str_Err_LoadImageFailed") + "\n" + ex.Message, "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
