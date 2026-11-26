using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KillerFamily.Controls;
using KillerPDF.Services;

namespace KillerPDF.Shell
{
    // The right-click card for a picture, both one that is part of the page and one placed on it. The header says it
    // is a picture, its size in pixels and its page. Both kinds get the same card (Steve 2026-11-19): copy, cut, save
    // and delete; turn, flip, replace, color correct and reset its shape; copy the text in it. The first edit of a
    // picture that is part of the page lifts it off the page into a placed picture, in the same Undo step.
    public partial class MainWindow
    {
        private CardTile SavePictureTile(ImageAnnotation picture) => new("\uE74E", Loc("Str_Ctx_SavePicture"), null, () => SavePicture(picture), false, true);

        // placed: the picture is an ImageAnnotation on the page; otherwise it is the page's own picture under the
        // viewer's source selection.
        private void AddPictureCard(ImageAnnotation picture, bool placed)
        {
            if (TryFindResource("CardMenu") is Style card) _ctxMenu.Style = card;
            bool canRemove = placed || ActiveViewer.CanDeleteSelectedSourceImageExt;
            // The nav row every object card shares with the page card.
            AddNavRow(_ctxMenu, picture.PageIndex);
            _ctxMenu.Items.Add(CardSlot(WithHeaderDelete(PictureCardHeader(picture), canRemove), new Thickness(4, 2, 4, 6)));

            // Family card grid (KillerUI CardBuilder.TileGroups): three across, four full rows by default, most used
            // first: Copy, Cut and Save; the three turns; the two flips and Replace; Color Correction, Copy Text and
            // Reset Size. Paste, Raise and Lower start in the Hidden drawer, and Delete sits in the header. A picture
            // that is part of the page can be edited only when it can leave the page (canRemove).
            bool canEdit = placed || canRemove;
            CardTile[] clipboard =
            [
                new("\uE8C8", Loc("Str_Ctx_Copy"), "Ctrl:C", CopySelectedAnnotations, false, true),
                new("\uE8C6", Loc("Str_Ctx_Cut"), placed ? null : "Ctrl:X", placed ? CutSelectedAnnotations : CutSelectedSourceImage, false, canRemove),
                new("\uE77F", Loc("Str_Ctx_Paste"), "Ctrl:V", () => PasteAnnotations(picture.PageIndex), false, MarkupToPaste()),
                SavePictureTile(picture),
            ];
            CardTile[] turns =
            [
                new("\uE7AD", Loc("Str_Ctx_RotateCCWShort"), null, () => EditPicture(picture, placed, p => PictureTurn.Turn(p, 270)), true, canEdit),
                new("\uE895", Loc("Str_Ctx_UpsideDown"), null, () => EditPicture(picture, placed, p => PictureTurn.Turn(p, 180)), false, canEdit),
                new("\uE7AD", Loc("Str_Ctx_RotateCWShort"), null, () => EditPicture(picture, placed, p => PictureTurn.Turn(p, 90)), false, canEdit),
            ];
            CardTile[] changes =
            [
                new("\uE8AB", Loc("Str_Ctx_FlipHorizontal"), null, () => FlipPicture(picture, placed, horizontal: true), false, canEdit),
                new("\uE8CB", Loc("Str_Ctx_FlipVertical"), null, () => FlipPicture(picture, placed, horizontal: false), false, canEdit),
                new("\uE8E5", Loc("Str_Ctx_ReplacePicture"), null, () => ReplacePicture(picture, placed), false, canEdit),
            ];
            CardTile[] tools =
            [
                new("\uE790", Loc("Str_ColorCorrection"), null, () => CorrectPictureColor(picture, placed), false, canEdit),
                new("\uEE6F", Loc("Str_Ctx_CopyText"), null, () => CopyPictureText(picture), false, true),
                new("\uE799", Loc("Str_Ctx_ResetSize"), null, () => EditPicture(picture, placed, ResetPictureShape), false, canEdit && PictureOutOfShape(picture)),
            ];
            // Raise and Lower only reorder placed pictures among the marks; a picture that is part of the page already
            // sits beneath every mark, and it leaves the page only for an edit.
            bool canRaise = false, canLower = false;
            if (placed && _annotations.TryGetValue(picture.PageIndex, out var pageList) && pageList.IndexOf(picture) is >= 0 and int idx)
            {
                canRaise = OverlapNeighbor(pageList, idx, picture, +1) >= 0;
                canLower = OverlapNeighbor(pageList, idx, picture, -1) >= 0;
            }
            CardTile[] layers =
            [
                new("\uE70E", Loc("Str_Ctx_Raise"), null, () => MoveAnnotationLayer(picture, +1), false, canRaise),
                new("\uE70D", Loc("Str_Ctx_Lower"), null, () => MoveAnnotationLayer(picture, -1), false, canLower),
            ];
            // A spare in the Hidden drawer: Select All, for picking up everything on the page from here.
            CardTile[] spares = [Spare("selectall", new("\uE8B3", Loc("Str_Ctx_SelectAll"), "Ctrl:A", SelectAllInView, false, true))];
            var strip = EditableTileGroups(_ctxMenu, "Picture", ["\uE8C8", "\uE8C6"], ["\uE77F", "\uE70E", "\uE70D"],
                clipboard, turns, changes, tools, layers, spares);
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
                Text = "\uEB9F", FontSize = 18, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center,
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

        // One edit of a picture as one Undo step. A placed picture changes in place; a picture that is part of the page
        // is lifted off it first, and the edit lands on the lifted copy. Returns the picture that now carries the edit.
        private ImageAnnotation? EditPicture(ImageAnnotation picture, bool placed, Action<ImageAnnotation> edit)
        {
            if (!placed)
            {
                if (LiftPicture(edit) is not { } lifted) return null;
                RefreshPicture(lifted);
                return lifted;
            }
            ActiveViewer.PushPageSnapshotUndoExt(picture.PageIndex);
            edit(picture);
            picture.CachedBitmap = null;
            RefreshPicture(picture);
            return picture;
        }

        // Lifts the selected picture off the page into a placed picture: the saved page loses it and the markup layer
        // gains it at the bottom, so every mark that covered it still does. The edit is made on the copy before the
        // reload, so the document history holds one step that puts the page back as it was.
        private ImageAnnotation? LiftPicture(Action<ImageAnnotation> edit)
        {
            if (ActiveViewer.SelectedSourceImageRemovalExt is not { } source
                || CloneAnnotation(source.Image) is not ImageAnnotation lifted) return null;
            edit(lifted);
            lifted.CachedBitmap = null;
            int page = lifted.PageIndex;
            try
            {
                SaveTempAndReload(keepAnnotations: true, preserveZoom: true, selectedPageAfterReload: page,
                    finalizeSavedFile: source.Remove,
                    remapAnnotations: annotations =>
                    {
                        if (!annotations.TryGetValue(page, out var list)) annotations[page] = list = [];
                        list.Insert(0, lifted);
                    });
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                KillerDialog.Show(this, string.Format(Loc("Str_Pages_Failed"), ex.Message), "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Error);
                return null;
            }
            return _annotations.TryGetValue(page, out var placed) && placed.Contains(lifted) ? lifted : null;
        }

        // Flips the picture as it looks on the page.
        private void FlipPicture(ImageAnnotation picture, bool placed, bool horizontal)
        {
            bool acrossPixels = PictureShape.FlipsAcrossPixels(horizontal, picture.Rotation);
            if (RecodePicture(picture, bitmap => ApplyFlip(bitmap, acrossPixels, !acrossPixels)) is not { } data) return;
            EditPicture(picture, placed, p => p.ImageData = data);
        }

        // True when the picture is drawn out of its own proportions, stretched or squashed.
        private static bool PictureOutOfShape(ImageAnnotation picture) =>
            PicturePixels(picture) is { } pixels && PictureShape.OutOfShape(picture.SourceWidth, picture.SourceHeight, pixels.Width, pixels.Height);

        // Reset Size keeps the picture's spot and width and gives it back its own proportions.
        private static void ResetPictureShape(ImageAnnotation picture)
        {
            if (PicturePixels(picture) is not { Width: > 0, Height: > 0 } pixels) return;
            picture.SourceHeight = PictureShape.HeightFor(picture.SourceWidth, pixels.Width, pixels.Height);
        }

        private void CopyPictureText(ImageAnnotation picture)
        {
            BitmapSource upright;
            try { upright = Turned(PictureBitmap(picture), picture.Rotation); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                KillerDialog.Show(this, Loc("Str_Err_OcrFailed") + "\n" + ex.Message, "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            Ocr.OcrPicture(upright);
        }

        // Color Correction for one picture: the page window's Color and Levels sections on the picture alone. Each Apply
        // is one Undo step; the first one lifts a picture that is part of the page, and later ones change the lifted copy.
        private void CorrectPictureColor(ImageAnnotation picture, bool placed)
        {
            ImageAnnotation current = picture;
            bool isPlaced = placed;
            TransformWindow.PagePreview? Preview()
            {
                try
                {
                    var shown = Turned(Fitted(PictureBitmap(current), 1100), current.Rotation);
                    return new TransformWindow.PagePreview(shown, current.SourceWidth * current.Scale,
                        current.SourceHeight * current.Scale, current.PageIndex + 1);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    KillerDialog.Show(this, Loc("Str_Err_LoadImageFailed") + "\n" + ex.Message, "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Error);
                    return null;
                }
            }
            if (Preview() is not { } first) return;
            var window = new TransformWindow(this, 1, [0], first, _ => Preview(), (settings, _) =>
            {
                string? data = RecodePicture(current, bitmap => ColorCorrection.Apply(
                    TransformWindow.ApplyLevels(bitmap, settings.LevelBlack, settings.LevelWhite, settings.LevelGamma),
                    settings.ColorAdjustments));
                if (data is null || EditPicture(current, isPlaced, p => p.ImageData = data) is not { } edited)
                    return Task.FromResult(false);
                current = edited;
                isPlaced = true;
                return Task.FromResult(true);
            }, TransformWindowMode.PictureColorCorrection);
            window.ShowDialog();
        }

        // The picture's pixels as stored, before its turn: a picture lifted from the page is drawn at its own size.
        private static BitmapSource PictureBitmap(ImageAnnotation picture)
        {
            byte[] data = Convert.FromBase64String(picture.ImageData);
            BitmapSource bitmap = data.AsSpan().StartsWith("%PDF-"u8)
                ? RenderPdfPicture(data)
                : BitmapFrame.Create(new MemoryStream(data), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            if (bitmap.CanFreeze && !bitmap.IsFrozen) bitmap.Freeze();
            return bitmap;
        }

        private static BitmapSource RenderPdfPicture(byte[] pdf)
        {
            var asset = PdfAnnotationImageSource.ReadPdfAsset(pdf);
            return PdfAnnotationImageSource.RenderPdf(pdf, asset.Width, asset.Height);
        }

        private static BitmapSource Turned(BitmapSource bitmap, int rotation)
        {
            if (rotation % 360 == 0) return bitmap;
            var turned = new TransformedBitmap(bitmap, new RotateTransform(rotation));
            turned.Freeze();
            return turned;
        }

        private static BitmapSource Fitted(BitmapSource bitmap, int longestSide)
        {
            double scale = (double)longestSide / Math.Max(bitmap.PixelWidth, bitmap.PixelHeight);
            if (scale >= 1) return bitmap;
            var fitted = new TransformedBitmap(bitmap, new ScaleTransform(scale, scale));
            fitted.Freeze();
            return fitted;
        }

        // New pixels for the picture, as base 64: a JPEG stays a JPEG, and everything else is written as a PNG.
        // Null, after saying so, when the picture cannot be read or written.
        private string? RecodePicture(ImageAnnotation picture, Func<BitmapSource, BitmapSource> change)
        {
            try
            {
                byte[] data = Convert.FromBase64String(picture.ImageData);
                BitmapSource changed = change(PictureBitmap(picture));
                if (data is [0xFF, 0xD8, ..]) return Convert.ToBase64String(BitmapHelpers.EncodeJpeg(changed, 92, grayscale: false));
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(changed));
                using var stream = new MemoryStream();
                encoder.Save(stream);
                return Convert.ToBase64String(stream.ToArray());
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                KillerDialog.Show(this, Loc("Str_Err_LoadImageFailed") + "\n" + ex.Message, "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Error);
                return null;
            }
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
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(RenderPdfPicture(pdf)));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            return stream.ToArray();
        }

        // Swaps a picture's file, keeping its spot and width; the height follows the new picture's shape.
        private void ReplacePicture(ImageAnnotation picture, bool placed)
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
                string data = Convert.ToBase64String(bytes);
                EditPicture(picture, placed, p =>
                {
                    double width = p.SourceWidth * p.Scale;
                    p.ImageData = data;
                    p.Rotation = 0;
                    p.SourceWidth = frame.PixelWidth;
                    p.SourceHeight = frame.PixelHeight;
                    p.Scale = width / frame.PixelWidth;
                });
            }
            catch (Exception ex)
            {
                KillerDialog.Show(this, Loc("Str_Err_LoadImageFailed") + "\n" + ex.Message, "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
