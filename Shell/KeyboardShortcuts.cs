using System.Diagnostics;
using KillerPDF.Controls;
using KillerPDF.Shell;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Microsoft.Win32;
using KillerPDF.Services;

namespace KillerPDF.Shell
{
    public partial class MainWindow
    {
        // ============================================================
        // Keyboard shortcuts
        // ============================================================

        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            if (_pageBoxActive && e.Key is not (Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift))
            {
                EndPageBoxSelection(cancel: true);
                if (e.Key == Key.Escape) { e.Handled = true; return; }
            }
            if (App.UpdateRestartPrepared)
            {
                e.Handled = true;
                return;
            }
            base.OnPreviewKeyDown(e);

            if (_syntheticShortcut is null && HandleSignaturePickerKey(e)) return;

            // The title bar search box keeps its own navigation keys while it has focus.
            if (_syntheticShortcut is null && HandleToolSearchKey(e)) return;

            if (_syntheticShortcut is null && e.OriginalSource is Controls.DocumentSurface reading
                && reading.IsKeyboardFocused && Controls.DocumentSurface.IsReadingNavigation(e.Key, Keyboard.Modifiers))
                return;

            // Form controls keep their activation and radio navigation keys before shortcut remapping.
            if (_syntheticShortcut is null && Keyboard.Modifiers == ModifierKeys.None
                && e.Key is Key.Space or Key.Enter or Key.Left or Key.Right or Key.Up or Key.Down)
            {
                for (DependencyObject? source = e.OriginalSource as DependencyObject; source is not null;
                    source = source is Visual ? VisualTreeHelper.GetParent(source) : (source as FrameworkContentElement)?.Parent)
                {
                    if (source is Controls.FormToggleSurface toggle
                        && (e.Key is Key.Space or Key.Enter || toggle.Kind == Controls.FormToggleSurface.ToggleKind.Radio))
                        return;
                }
            }

            if (_syntheticShortcut is null && ShortcutTogglePolicy.IsToggle(e.Key, Keyboard.Modifiers, e.SystemKey))
            {
                if (!e.IsRepeat) SetKeyboardShortcutsEnabled(!KeyboardShortcutsEnabled);
                e.Handled = true;
                return;
            }
            // A single click on a text form field only selects it (#307), so the keys that follow reach this
            // window. A plain character moves the keyboard into the field and is typed there, and the key is
            // left unhandled so it arrives as text. Chords such as Ctrl+S and every other control keep going.
            if (_syntheticShortcut is null && (ReferenceEquals(e.OriginalSource, this) || e.OriginalSource is ScrollViewer) && _doc is not null
                && ShortcutsOverlay.Visibility != Visibility.Visible && AboutOverlay.Visibility != Visibility.Visible
                && ActiveViewer.CanTypeIntoSelectedFormFieldExt
                && FormFieldTypeAhead.StartsEditing(e.Key, Keyboard.Modifiers,
                    Services.KeyLayout.CharFor(e.Key, (Keyboard.Modifiers & ModifierKeys.Shift) != 0))
                && ActiveViewer.BeginTypedFormFieldEditExt())
                return;
            if (_syntheticShortcut is null && !KeyboardShortcutsEnabled)
            {
                e.Handled = ShortcutTogglePolicy.SuppressWhenDisabled(
                    e.Key, e.OriginalSource is TextBox or PasswordBox, e.SystemKey);
                return;
            }

            // Keyboard view of the shortcuts overlay: holding Ctrl / Shift / Alt previews that layer.
            if (_syntheticShortcut is null) KbSyncLayerFromModifiers();

            // #190: a custom shortcut stands in for the key its command shipped on. Everything
            // below matches the translated key; a focused text box still gets the keys as typed.
            Key rawKey = e.Key, rawSystemKey = e.SystemKey;
            ModifierKeys rawMods = Keyboard.Modifiers;
            // A command run from the title bar search (ToolSearch.cs) arrives as its built-in key,
            // which is what every branch below matches, so it skips the custom-key translation.
            if (_syntheticShortcut is { } synthetic) (rawKey, rawMods, rawSystemKey) = synthetic;
            var (key, mods, systemKey, swallow) = _syntheticShortcut is null
                ? TranslateShortcut(rawKey, rawMods, rawSystemKey)
                : (rawKey, rawMods, rawSystemKey, false);

            if (_comparisonActive && key == Key.Escape && mods == ModifierKeys.None)
            {
                EndComparison(closeSplit: true);
                e.Handled = true;
                return;
            }

            // Bold / italic / underline while a text annotation is being edited. This has to come
            // BEFORE the early return below, which hands every other key to the edit box: that
            // return is exactly why Ctrl+B never reached the text tool and collapsed the sidebar
            // instead. The two meanings of Ctrl+B cannot collide, because the sidebar binding is
            // only reachable when no edit box has focus and this one only when it does.
            if (!swallow && _activeTextBox is not null && _activeTextBox.IsFocused &&
                mods == ModifierKeys.Control &&
                key is Key.B or Key.I or Key.U)
            {
                switch (key)
                {
                    case Key.B: _textBold      = !_textBold;      break;
                    case Key.I: _textItalic    = !_textItalic;    break;
                    case Key.U: _textUnderline = !_textUnderline; break;
                }
                ApplyTextStyleToSelection();   // TextSettingsBar.cs, same pair the toggle buttons call
                ShowTextSettings();
                e.Handled = true;
                return;
            }

            // Ordinary typing and standard text-editing gestures stay in an editable TextBox.
            // Window commands such as Ctrl+S, Ctrl+F, and Ctrl+P must continue through the app
            // shortcut chain even while a form field or typewriter box owns keyboard focus.
            bool editableTextFocused =
                e.OriginalSource is TextBox tbSrc && !tbSrc.IsReadOnly
                || e.OriginalSource is PasswordBox
                || _activeTextBox is not null && _activeTextBox.IsFocused;
            if (editableTextFocused && key == Key.Escape
                && mods == ModifierKeys.None)
            {
                // Escape abandons a page edit. Losing focus alone would commit it.
                if (_activeTextBox is { IsFocused: true }) ActiveViewer.CancelActiveTextEditExt();
                Keyboard.ClearFocus();
                Focus();
                e.Handled = true;
                return;
            }
            if (editableTextFocused
                && EditableTextShortcutPolicy.KeepInTextBox(
                    rawKey, rawMods, rawSystemKey))
                return;
            // The key a command moved away from does nothing now.
            if (swallow)
            {
                e.Handled = true;
                return;
            }

            if (mods == (ModifierKeys.Alt | ModifierKeys.Shift)
                && (key == Key.System ? systemKey : key) is Key.Up or Key.Down)
            {
                if (!e.IsRepeat) SetTabPlacement((key == Key.System ? systemKey : key) == Key.Down);
                e.Handled = true;
                return;
            }

            if (TryRunExpandedShortcut(key == Key.System ? systemKey : key, mods, e.IsRepeat))
            {
                e.Handled = true;
                return;
            }

            if (key == Key.C && mods == ModifierKeys.Control)
            {
                // An annotation selection copies the annotation(s); otherwise copy page text.
                if (_selectedAnnotation is not null || _selectedSet.Count > 0) CopySelectedAnnotations();
                else CopySelectedText();
                e.Handled = true;
            }
            else if (key == Key.V && mods == ModifierKeys.Control)
            {
                PasteInView(PageList.SelectedIndex);
                e.Handled = true;
            }
            else if (key == Key.A && mods == ModifierKeys.Control)
            {
                if (PageList.IsKeyboardFocusWithin)
                    PageList.SelectAll();
                else
                    SelectAllInView();
                e.Handled = true;
            }
            else if (key == Key.F && mods == ModifierKeys.Control)
            {
                ToggleSearchBar();
                e.Handled = true;
            }
            else if (key == Key.K && mods == ModifierKeys.Control)
            {
                ToggleToolSearch();   // ToolSearch.cs: the title bar's tool and command search
                e.Handled = true;
            }
            else if (key == Key.H && mods == ModifierKeys.Control)
            {
                ShowReplaceBar();
                e.Handled = true;
            }
            else if (key == Key.F3 && (mods == ModifierKeys.None || mods == ModifierKeys.Shift))
            {
                // F3 / Shift+F3 - next / previous search match, the Find Next convention (Acrobat and
                // Sumatra do the same). With the search bar closed, F3 opens it like Ctrl+F.
                if (_searchBar is null || _searchBar.Visibility != Visibility.Visible)
                    ToggleSearchBar();
                else if (Search.HasResults)
                {
                    if (mods == ModifierKeys.Shift) SearchPrevResult();
                    else SearchNextResult();
                }
                e.Handled = true;
            }
            else if (key == Key.Escape && ActiveViewer.CancelMeasurePlacement())
            {
                // Measure tool: abandon the measurement (or calibration line) being placed.
                e.Handled = true;
            }
            else if (key == Key.Back && ActiveViewer.MeasurePlacementActive)
            {
                ActiveViewer.RemoveLastMeasurePoint();
                e.Handled = true;
            }
            else if (key == Key.Enter && ActiveViewer.MeasurePlacementActive)
            {
                // Finish a perimeter or area from the keyboard.
                ActiveViewer.FinishMeasurePlacement();
                e.Handled = true;
            }
            else if (key == Key.Escape && _shapePolyPoints.Count > 0)
            {
                // Shapes tool (#127 Phase 3): abandon the in-progress polygon.
                CancelShapePolygon();
                e.Handled = true;
            }
            else if (key == Key.Back && _shapePolyPoints.Count > 0)
            {
                // Remove the last placed polygon vertex.
                ShapePolyBackspace();
                e.Handled = true;
            }
            else if (key == Key.Enter && _shapePolyPoints.Count >= 3)
            {
                // Close the polygon from the keyboard.
                CommitShapePolygon();
                e.Handled = true;
            }
            else if (key == Key.Enter && _currentTool == EditTool.Crop && _cropConfirmBar is not null)
            {
                ApplyCrop([PageList.SelectedIndex]);
                e.Handled = true;
            }
            else if (key == Key.Escape && _currentTool == EditTool.Crop && _cropConfirmBar is not null)
            {
                HideCropConfirmBar();
                e.Handled = true;
            }
            else if (key == Key.Escape && ShortcutsOverlay.Visibility == Visibility.Visible)
            {
                FadeOverlayOut(ShortcutsOverlay);
                e.Handled = true;
            }
            else if (key == Key.Escape && AboutOverlay.Visibility == Visibility.Visible)
            {
                CloseAboutOverlay();
                e.Handled = true;
            }
            else if (key == Key.Escape && _searchBar is not null && _searchBar.Visibility == Visibility.Visible)
            {
                CloseSearchBar();
                e.Handled = true;
            }
            else if (key == Key.Escape && _busyCts is not null)
            {
                // A cancellable long operation (OCR, repair) is running behind the busy overlay - offer to
                // cancel it.
                OperationHold? hold = _busyHold;
                hold?.Hold();
                try
                {
                    if (KillerDialog.Show(this, string.Format(Loc("Str_Dlg_CancelBusy"), _busyOpLabel), "KillerPDF",
                            MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                        _busyCts?.Cancel();
                }
                finally { if (ReferenceEquals(hold, _busyHold)) hold?.Release(); }
                e.Handled = true;
            }
            // #153: matched by the character the key TYPES, not its position. On a German layout
            // "?" is Shift+ss, so the US-positional OemQuestion check never fired (and the exact
            // modifier equality failed too, since typing "?" holds Shift). The VK test stays as a
            // fast path for layouts where it already worked.
            else if (Services.KeyLayout.IsCtrlChar(key, '?')
                     || (key == Key.OemQuestion && mods == ModifierKeys.Control))
            {
                if (ShortcutsOverlay.Visibility == Visibility.Visible) FadeOverlayOut(ShortcutsOverlay);
                else ShowShortcutsOverlayExclusive();
                e.Handled = true;
            }
            else if (key == Key.F1)
            {
                // Toggle the shortcuts overlay (conventional Help key, alongside Ctrl+?).
                if (ShortcutsOverlay.Visibility == Visibility.Visible) FadeOverlayOut(ShortcutsOverlay);
                else ShowShortcutsOverlayExclusive();
                e.Handled = true;
            }
            else if (key == Key.F12)
            {
                // Toggle the About dialog. (Moved off F2, which now belongs to rename-bookmark in the
                // outline panel, #133 - Windows convention. Document Info moved to F4 / Ctrl+D below.)
                if (AboutOverlay.Visibility == Visibility.Visible) CloseAboutOverlay();
                else ShowAboutOverlay();
                e.Handled = true;
            }
            else if (key == Key.F4 && mods == ModifierKeys.None)
            {
                // Document Info - the advertised single-key shortcut (F1 help, F4 info, F5-F8 views,
                // F11 full screen, F12 about). Looked it up on the shortcuts overlay and pressed it?
                // The cheat sheet gets out of the way first.
                if (ShortcutsOverlay.Visibility == Visibility.Visible) FadeOverlayOut(ShortcutsOverlay);
                OpenDocumentInfo();
                e.Handled = true;
            }
            else if (key == Key.D && mods == ModifierKeys.Control)
            {
                // Compatibility alias: Ctrl+D is Document Properties in Acrobat, Foxit, and SumatraPDF.
                if (ShortcutsOverlay.Visibility == Visibility.Visible) FadeOverlayOut(ShortcutsOverlay);
                OpenDocumentInfo();
                e.Handled = true;
            }
            else if (key == Key.F5) { if (_doc is not null) SetViewMode(ViewMode.Continuous); e.Handled = true; }
            else if (key == Key.F6) { if (_doc is not null) SetViewMode(ViewMode.Single);     e.Handled = true; }
            else if (key == Key.F7) { if (_doc is not null) SetViewMode(ViewMode.TwoPage);    e.Handled = true; }
            else if (key == Key.F8) { if (_doc is not null) SetViewMode(ViewMode.Grid);       e.Handled = true; }
            // F10 = split pane. Matched as Key.System + SystemKey == Key.F10, NOT e.Key == Key.F10:
            // F10 is a system key in WPF (it activates the menu bar), so it never arrives as
            // Key.F10 and a plain check silently never fires. The Shift+F10 branch further down has
            // the same shape; F11 below does not need it, because F11 is not a system key.
            // Shift is excluded here because Shift+F10 is the context menu - without that guard the
            // two would both fire off one press.
            else if (key == Key.System && systemKey == Key.F10
                     && !mods.HasFlag(ModifierKeys.Shift))
            {
                ToggleSplit();
                e.Handled = true;
            }
            // #215: reading view - toolbar off. Alt+M arrives as a system key (Alt+letter), same
            // shape as the F10 branches above.
            else if (key == Key.System && systemKey == Key.M && mods == ModifierKeys.Alt)
            { ToggleToolbarHidden(); e.Handled = true; }
            else if (key == Key.F11) { ToggleFullScreen(); e.Handled = true; }
            else if (key == Key.Escape && _fullScreen) { ToggleFullScreen(); e.Handled = true; }
            else if (key == Key.F4 && mods == ModifierKeys.Shift && _doc is not null)
            {
                ShowCurrentFileSize();
                e.Handled = true;
            }
            // PgDn / PgUp navigate to the next / previous page - they never reorder pages (that's the
            // toolbar Move Up/Down buttons). Handled at the window level with e.Handled so it behaves the
            // same whether the page canvas or a sidebar thumbnail has focus; without this, a focused
            // PageList (ListBox) would page its own selection instead. The TextBox guard at the top of this
            // handler already exempts typing in a form field / typewriter box.
            else if (key == Key.PageDown && mods == ModifierKeys.None)
            {
                NavigatePageStep(1);   // one page; one SPREAD in Two-Page mode (#120)
                e.Handled = true;
            }
            else if (key == Key.PageUp && mods == ModifierKeys.None)
            {
                NavigatePageStep(-1);
                e.Handled = true;
            }
            else if (key == Key.P && mods == ModifierKeys.Control)
            {
                Print_Click(this, e);
                e.Handled = true;
            }
            else if (key == Key.Delete && PageList.IsKeyboardFocusWithin && PageList.SelectedItems.Count > 0)
            {
                Delete_Click(this, e);
                e.Handled = true;
            }
            else if (key == Key.Delete && Viewer.HasSelectedFormFieldExt)
            {
                Viewer.DeleteSelectedFormFieldExt();
                e.Handled = true;
            }
            else if (key == Key.Delete && (_selectedAnnotation is not null || _selectedSet.Count > 0))
            {
                DeleteSelected();
                e.Handled = true;
            }
            else if (key == Key.R &&
                     (mods == ModifierKeys.Control ||
                      mods == (ModifierKeys.Control | ModifierKeys.Shift)))
            {
                if (!e.IsRepeat)
                    RotatePages_Click(mods == ModifierKeys.Control ? 90 : -90);
                e.Handled = true;
            }
            else if (key == Key.Z && mods == ModifierKeys.Control)
            {
                if (!e.IsRepeat) Undo_Click(this, e);   // ignore key auto-repeat so one press = one undo
                e.Handled = true;
            }
            else if (key == Key.S && mods == (ModifierKeys.Control | ModifierKeys.Shift))
            {
                SaveAs_Click(this, e);
                e.Handled = true;
            }
            else if (key == Key.S && mods == ModifierKeys.Control)
            {
                SaveInPlace();
                e.Handled = true;
            }
            else if (key == Key.W && mods == ModifierKeys.Control)
            {
                CloseTab(_active);
                e.Handled = true;
            }
            else if (key == Key.W && mods == (ModifierKeys.Control | ModifierKeys.Shift))
            {
                CloseOtherTabs(_active);
                e.Handled = true;
            }
            else if (key == Key.Q && mods == (ModifierKeys.Control | ModifierKeys.Shift))
            {
                CloseAllTabs();
                e.Handled = true;
            }
            else if (key == Key.Q && mods == ModifierKeys.Control)
            {
                // The family quit: the same command QuitShortcut binds, so a moved key still quits.
                KillerFamily.Shell.QuitShortcut.Command.Execute(null, this);
                e.Handled = true;
            }
            else if (key == Key.Tab && mods == (ModifierKeys.Control | ModifierKeys.Shift))
            {
                CycleTab(-1);
                e.Handled = true;
            }
            else if (key == Key.Tab && mods == ModifierKeys.Control)
            {
                CycleTab(1);
                e.Handled = true;
            }
            else if (key == Key.O && mods == ModifierKeys.Control)
            {
                Open_Click(this, e);
                e.Handled = true;
            }
            else if (key == Key.O && mods == (ModifierKeys.Control | ModifierKeys.Shift))
            {
                OcrSelectedPagesToClipboard();
                e.Handled = true;
            }
            else if (key == Key.I && mods == (ModifierKeys.Control | ModifierKeys.Shift))
            {
                BeginOcrRegion();
                e.Handled = true;
            }
            // 1.9 commands. Each matches its menu gesture text and its ShortcutTable row; key repeat
            // is ignored so holding the chord opens one dialog, not a queue of them.
            else if (key == Key.T && mods == (ModifierKeys.Control | ModifierKeys.Shift))
            {
                if (!e.IsRepeat) ConvertScanToText();
                e.Handled = true;
            }
            else if (key == Key.E && mods == (ModifierKeys.Control | ModifierKeys.Shift))
            {
                if (!e.IsRepeat) ExportDocument_Click(this, e);
                e.Handled = true;
            }
            else if (key == Key.P && mods == (ModifierKeys.Control | ModifierKeys.Shift))
            {
                if (!e.IsRepeat) OpenProtectDialog();
                e.Handled = true;
            }
            else if (key == Key.D && mods == (ModifierKeys.Control | ModifierKeys.Shift))
            {
                if (!e.IsRepeat) OpenDocumentToolsMenu();   // DocumentTools.cs; the next letter picks the tool
                e.Handled = true;
            }
            else if (key == Key.System && systemKey == Key.G && mods == (ModifierKeys.Alt | ModifierKeys.Shift))
            {
                if (!e.IsRepeat) ToggleFilmGrain();
                e.Handled = true;
            }
            else if (key == Key.N && mods == (ModifierKeys.Control | ModifierKeys.Shift))
            {
                if (!e.IsRepeat) MoveActiveTabToNewWindow();
                e.Handled = true;
            }
            else if (key == Key.B && mods == ModifierKeys.None && _doc is not null
                     && _viewMode == ViewMode.TwoPage
                     && ShortcutsOverlay.Visibility != Visibility.Visible
                     && AboutOverlay.Visibility != Visibility.Visible)
            {
                // #193: bare B = book layout toggle, only while Two-Page is active (single-key
                // house style; the sidebar is F9). Same guards as the bare-key switches.
                ToggleBookMode();
                e.Handled = true;
            }
            else if (key == Key.N && mods == ModifierKeys.None && _doc is not null
                     && ShortcutsOverlay.Visibility != Visibility.Visible
                     && AboutOverlay.Visibility != Visibility.Visible)
            {
                // Bare N = invert document colors (night mode), #135. Moved off Ctrl+I in 1.6.6 so
                // the conventional italic chord is free while editing text; single-key house style.
                // Same guards as the bare-key tool switches below (doc open, no overlay, not typing).
                ToggleDocInvert(!ActiveViewer.DocInvert);   // per pane: flips the focused pane only
                e.Handled = true;
            }
            else if (key == Key.N && mods == ModifierKeys.Shift && _doc is not null
                     && ShortcutsOverlay.Visibility != Visibility.Visible
                     && AboutOverlay.Visibility != Visibility.Visible)
            {
                // Shift+N pairs with bare N: toggles whether night mode inverts pictures too
                // (the moon's right-click option). Same guards as N.
                ToggleInvertImages(!BitmapHelpers.DocInvertImages);
                e.Handled = true;
            }
            else if (key == Key.O && mods == ModifierKeys.Shift && _doc is not null
                     && ShortcutsOverlay.Visibility != Visibility.Visible
                     && AboutOverlay.Visibility != Visibility.Visible)
            {
                // Shift+O pairs with the Shapes key O: steps to the next shape type.
                CycleShapeKind();
                e.Handled = true;
            }
            else if (key == Key.M && mods == ModifierKeys.Shift && _doc is not null
                     && ShortcutsOverlay.Visibility != Visibility.Visible
                     && AboutOverlay.Visibility != Visibility.Visible)
            {
                // Shift+M pairs with the Measure key M: steps to the next measurement type.
                CycleMeasureMode();
                e.Handled = true;
            }
            // App-wide accessibility size (AppScale.cs), distinct from the Ctrl+wheel page zoom:
            // Ctrl+Shift +/- steps the whole-app size, Ctrl+Shift+0 resets it. Works with no
            // mouse; the wheel-over-logo gesture drives the same scale.
            else if ((key == Key.OemPlus || key == Key.Add) && mods == (ModifierKeys.Control | ModifierKeys.Shift))
            {
                ApplyAppScale(_appScale + AppScaleStep, persist: true);
                e.Handled = true;
            }
            else if ((key == Key.OemMinus || key == Key.Subtract) && mods == (ModifierKeys.Control | ModifierKeys.Shift))
            {
                ApplyAppScale(_appScale - AppScaleStep, persist: true);
                e.Handled = true;
            }
            else if ((key == Key.D0 || key == Key.NumPad0) && mods == (ModifierKeys.Control | ModifierKeys.Shift))
            {
                ApplyAppScale(1.0, persist: true);
                e.Handled = true;
            }
            // Toolbar appearance, mirroring the bar's right-click menu top to bottom: Ctrl+Shift+1/2
            // pick the icon size, Ctrl+Shift+3..6 pick where the text goes. Number row only.
            else if (key is >= Key.D1 and <= Key.D6 && mods == (ModifierKeys.Control | ModifierKeys.Shift))
            {
                switch (key)
                {
                    case Key.D1: SetIconSize(IconSize.Small);   break;
                    case Key.D2: SetIconSize(IconSize.Large);   break;
                    case Key.D3: SetLabelMode(LabelMode.None);  break;
                    case Key.D4: SetLabelMode(LabelMode.Beside);break;
                    case Key.D5: SetLabelMode(LabelMode.Under); break;
                    case Key.D6: SetLabelMode(LabelMode.Only);  break;
                }
                e.Handled = true;
            }
            else if (key == Key.F9 && mods == ModifierKeys.Shift && !_fullScreen)
            {
                ToggleSidebarSide();   // the next side, pairing with F9's collapse toggle
                e.Handled = true;
            }
            else if (key == Key.N && mods == ModifierKeys.Control)
            {
                NewDocument();
                e.Handled = true;
            }
            else if (key == Key.F9 && mods == ModifierKeys.None && !_fullScreen)
            {
                // Full screen hides the sidebar and zeroes its column; toggling it there opened a
                // blank column and left the layout inconsistent on the way back.
                // The sidebar gets the single key, house style. F9 used to jog the view mode, which
                // was the one redundant F-key: F5-F8 already jump straight to all four modes and the
                // wheel over the view still cycles them. There is deliberately no Ctrl+B alias:
                // Ctrl+B is bold, which is what users pressing it were expecting all along.
                SidebarToggle_Click(this, e);
                e.Handled = true;
            }
            else if (key == Key.Home && mods == ModifierKeys.None && _doc is not null)
            {
                // First / last page (the Acrobat / Sumatra convention).
                RecordNavJump();
                PageList.SelectedIndex = 0;
                e.Handled = true;
            }
            else if (key == Key.End && mods == ModifierKeys.None && _doc is not null)
            {
                RecordNavJump();
                PageList.SelectedIndex = _doc.PageCount - 1;
                e.Handled = true;
            }
            else if (key == Key.D1 && mods == ModifierKeys.Control && _doc is not null)
            {
                _fitMode = FitMode.None;
                SetTrueZoom(1.0);    // actual size (Acrobat Ctrl+1); Ctrl+0 stays the 100% reset
                e.Handled = true;
            }
            else if (key == Key.D2 && mods == ModifierKeys.Control && _doc is not null)
            {
                App.SetSetting("DefaultFitMode", FitMode.Width.ToString());
                FitToWidth();        // (Acrobat Ctrl+2)
                e.Handled = true;
            }
            else if (key == Key.D3 && mods == ModifierKeys.Control && _doc is not null)
            {
                App.SetSetting("DefaultFitMode", FitMode.Page.ToString());
                FitToPage();
                e.Handled = true;
            }
            else if (key == Key.Y && mods == ModifierKeys.Control)
            {
                if (!e.IsRepeat) Redo_Click(this, e);
                e.Handled = true;
            }
            else if (key == Key.Z && mods == (ModifierKeys.Control | ModifierKeys.Shift))
            {
                if (!e.IsRepeat) Redo_Click(this, e);   // Ctrl+Shift+Z, the editor-style redo
                e.Handled = true;
            }
            else if (key == Key.System && systemKey == Key.Left && mods == ModifierKeys.Alt)
            {
                NavHistoryGo(back: true);    // retrace bookmark / link / jump-box jumps
                e.Handled = true;
            }
            else if (key == Key.System && systemKey == Key.Right && mods == ModifierKeys.Alt)
            {
                NavHistoryGo(back: false);
                e.Handled = true;
            }
            else if ((key == Key.Apps
                      || (key == Key.System && systemKey == Key.F10 && mods.HasFlag(ModifierKeys.Shift)))
                     && _doc is not null)
            {
                // Windows accessibility convention: the Menu key / Shift+F10 opens the context menu
                // at the current selection without the mouse.
                OpenContextMenuAtSelection();
                e.Handled = true;
            }
            else if (mods == ModifierKeys.None && key is Key.Left or Key.Right or Key.Up or Key.Down
                     && NudgeSelection(key switch
                     {
                         Key.Left  => new Vector(-1, 0),
                         Key.Right => new Vector(1, 0),
                         Key.Up    => new Vector(0, -1),
                         _         => new Vector(0, 1)
                     }, createUndo: !e.IsRepeat))
            {
                e.Handled = true;
            }
            // Bare-key tool switches. Only when a document is open, no modifier is held, and no
            // overlay is up (and not while typing - guarded at the top of this handler).
            else if (mods == ModifierKeys.None && _doc is not null
                     && ShortcutsOverlay.Visibility != Visibility.Visible
                     && AboutOverlay.Visibility != Visibility.Visible
                     && TryToolShortcut(key))
            {
                e.Handled = true;
            }
            // Left/Right move one page - one two-page SPREAD in Two-Page mode (#120), so a press
            // always changes what's on screen instead of stepping through both pages of a spread.
            else if (key == Key.Left && mods == ModifierKeys.None)
            {
                if (NavigatePageStep(-1)) e.Handled = true;
            }
            else if (key == Key.Right && mods == ModifierKeys.None)
            {
                if (NavigatePageStep(1)) e.Handled = true;
            }
            // Up/Down scroll the view like the mouse wheel instead of jumping a page; at the top/
            // bottom edge (or when the page fits the viewport, where there's nothing to scroll)
            // they flip to the previous/next page, so at fit-to-page zoom this behaves exactly
            // like the old page navigation. Left/Right and PgUp/PgDn stay hard page jumps.
            // Handled at the window level so a focused sidebar thumbnail doesn't move its own
            // selection instead (same reasoning as PgUp/PgDn above).
            else if ((key == Key.Up || key == Key.Down) && mods == ModifierKeys.None)
            {
                if (_doc is not null)
                {
                    ScrollOrFlipByKey(up: key == Key.Up);
                    e.Handled = true;
                }
            }
            // #153: "the key that types + or =", whatever position that is on this layout. The
            // Ctrl+Shift app-scale chords above are tested FIRST, so ignoring Shift here cannot
            // swallow them - which is why this ordering matters and must not be rearranged.
            else if (Services.KeyLayout.IsCtrlChar(key, '+', '=')
                     || ((key == Key.OemPlus || key == Key.Add) && mods == ModifierKeys.Control))
            {
                if (_viewMode == ViewMode.Grid) GridZoomStep(false); else SetZoom(_zoomLevel + ZoomStep);
                e.Handled = true;
            }
            else if (Services.KeyLayout.IsCtrlChar(key, '-')
                     || ((key == Key.OemMinus || key == Key.Subtract) && mods == ModifierKeys.Control))
            {
                if (_viewMode == ViewMode.Grid) GridZoomStep(true); else SetZoom(_zoomLevel - ZoomStep);
                e.Handled = true;
            }
            else if (key == Key.D0 && mods == ModifierKeys.Control)
            {
                SetTrueZoom(1.0);
                e.Handled = true;
            }
            else if (key == Key.Escape && _doc is not null && _currentTool != EditTool.Select)
            {
                // 1.6.6 (#127 Phase 3): Esc with nothing left to cancel drops back to the Select
                // tool, Acrobat-style. With Select already active it does nothing, as in other
                // Windows apps.
                SetTool(EditTool.Select);
                e.Handled = true;
            }
            else if (key == Key.Space && !_spaceHeld)
            {
                _spaceHeld = true;
                PagePreviewPanel.Cursor = DragCursors.Open;
                e.Handled = true;
            }
        }

        // Full-window overlays are mutually exclusive: opening the shortcuts overlay dismisses
        // About instead of stacking - ShowAboutOverlay does the converse.
        private void ShowShortcutsOverlayExclusive()
        {
            if (AboutOverlay.Visibility == Visibility.Visible) FadeOverlayOut(AboutOverlay);
            ApplyPersistedShortcutView();
            FadePanelIn(ShortcutsOverlay);
        }

        // ── Jump history (Alt+Left / Alt+Right / mouse back-forward buttons) ─────────────────────
        // Page-granular: recorded at the long-jump sites (bookmark click, internal link, the page
        // jump box, Home/End) so a reader thrown 30 pages by a bookmark can retrace the hop.

        /// <summary>Records the CURRENT page onto the back stack. Call BEFORE performing a jump.</summary>
        private void RecordNavJump()
        {
            if (_doc is null) return;
            int cur = Math.Max(0, PageList.SelectedIndex);
            if (_navBack.Count > 0 && _navBack.Peek() == cur) { _navForward.Clear(); return; }
            _navBack.Push(cur);
            _navForward.Clear();   // a fresh jump invalidates the forward chain, like a browser
        }

        private void NavHistoryGo(bool back)
        {
            if (_doc is null) return;
            var from = back ? _navBack : _navForward;
            var to   = back ? _navForward : _navBack;
            if (from.Count == 0) return;
            int cur = Math.Max(0, PageList.SelectedIndex);
            int target = from.Pop();
            to.Push(cur);
            if (target >= 0 && target < _doc.PageCount)
                PageList.SelectedIndex = target;
        }

        // Mouse back / forward buttons (XButton1 / XButton2) retrace the same history, like a
        // browser. Registered on the window in the MainWindow constructor.
        private void NavHistory_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.XButton1)      { NavHistoryGo(back: true);  e.Handled = true; }
            else if (e.ChangedButton == MouseButton.XButton2) { NavHistoryGo(back: false); e.Handled = true; }
        }

        /// <summary>Keyboard access to the right-click menu (Menu key / Shift+F10): the selected
        /// annotation's menu at its bounds, or the page-level menu centered on the current page's
        /// canvas. Placement is set to Center just for this open and restored on close, so the
        /// mouse path keeps its open-at-cursor behavior.</summary>
        private void OpenContextMenuAtSelection()
        {
            if (_doc is null || _annotationCanvas.ContextMenu is not ContextMenu cm) return;
            int pg = Math.Max(0, PageList.SelectedIndex);
            Point pt;
            if (_selectedAnnotation is not null)
            {
                pg = _selectedAnnotation.PageIndex;
                var b = AnnotBounds(_selectedAnnotation);
                pt = new Point(b.X + b.Width / 2, b.Y + b.Height / 2);
            }
            else
                pt = _renderDims.TryGetValue(pg, out var rd)
                    ? new Point(rd.w / 2.0, rd.h / 2.0)
                    : new Point(0, 0);
            var canvas = VisibleCanvasForPage(pg) ?? CanvasForPage(pg);
            if (canvas is null) return;
            _activeCanvas = canvas;
            PopulateContextMenu(pt, pg);
            cm.PlacementTarget = canvas;
            var prevPlacement = cm.Placement;
            cm.Placement = System.Windows.Controls.Primitives.PlacementMode.Center;
            void Restore(object? s, RoutedEventArgs a) { cm.Placement = prevPlacement; cm.Closed -= Restore; }
            cm.Closed += Restore;
            cm.IsOpen = true;
        }

        // One Up/Down arrow press scrolls this many DIP; key auto-repeat makes holding the key a
        // smooth continuous scroll. Kept smaller than a wheel notch (144 DIP, see WheelScrollFactor)
        // for fine reading control.
        private const double ArrowScrollStep = 48.0;

        // Up/Down arrow behavior, mirroring PagePreview_PreviewMouseWheel exactly: Grid and
        // Continuous are one scroll over the whole document, so the keys always scroll; Single/
        // Two-Page scroll within the page and flip to the previous/next page at the edges.
        private void ScrollOrFlipByKey(bool up)
        {
            double step = up ? -ArrowScrollStep : ArrowScrollStep;
            if (_viewMode == ViewMode.Grid || _viewMode == ViewMode.Continuous)
            {
                PagePreviewPanel.ScrollToVerticalOffset(PagePreviewPanel.VerticalOffset + step);
                return;
            }
            // NavigatePageByWheel treats positive delta as "previous page" (wheel-up), so reuse
            // the same convention here.
            if (PagePreviewPanel.ScrollableHeight <= 0)
            {
                NavigatePageByWheel(up ? 120 : -120);
                return;
            }
            bool atTop    = PagePreviewPanel.VerticalOffset <= 0;
            bool atBottom = PagePreviewPanel.VerticalOffset >= PagePreviewPanel.ScrollableHeight - 1;
            if ((atTop && up) || (atBottom && !up))
            {
                NavigatePageByWheel(up ? 120 : -120);
                return;
            }
            PagePreviewPanel.ScrollToVerticalOffset(PagePreviewPanel.VerticalOffset + step);
        }

        protected override void OnPreviewKeyUp(KeyEventArgs e)
        {
            base.OnPreviewKeyUp(e);
            KbSyncLayerFromModifiers();   // releasing a modifier drops the keyboard view back a layer
            if (e.Key == Key.Space && _spaceHeld)
            {
                _spaceHeld = false;
                if (!ActiveViewer.IsPanning)
                    PagePreviewPanel.Cursor = Cursors.Arrow;
                e.Handled = true;
            }
        }

        // Maps a bare key to an editing tool. Returns false for any other key so the caller's
        // shortcut chain continues. Mirrors the toolbar tool buttons exactly - Signature routes
        // through its button handler so the signature picker opens (a bare SetTool would only arm
        // the tool without showing the menu).
        private bool TryToolShortcut(Key key)
        {
            if (_doc is null) return false;
            switch (key)
            {
                // One letter per tool, so a key keeps its tool however the toolbar grows. Select is
                // V (the Photoshop / Illustrator / Figma convention) and Shapes is O; Line also keeps
                // U. Signature, Transform, Stamp, Color Correction, Measure and Redact go through
                // their toolbar handlers so their pickers open.
                case Key.V: SetTool(EditTool.Select); return true;
                case Key.F: SetTool(EditTool.FormField); return true;
                case Key.T: SetTool(EditTool.Text); return true;
                case Key.H: SetTool(EditTool.Highlight); return true;
                case Key.L: case Key.U: SetTool(EditTool.Line); return true;
                case Key.O: SetTool(EditTool.Shape); return true;
                case Key.D: SetTool(EditTool.Draw); return true;
                case Key.I: SetTool(EditTool.Image); return true;
                case Key.G: ToolSignature_Click(this, new RoutedEventArgs()); return true;
                case Key.C: SetTool(EditTool.Crop); return true;
                case Key.R: ToolRotate_Click(this, new RoutedEventArgs()); return true;
                case Key.S: ToolStamp_Click(this, new RoutedEventArgs()); return true;
                case Key.E: ToolColorCorrection_Click(this, new RoutedEventArgs()); return true;
                case Key.M: ToolMeasure_Click(this, new RoutedEventArgs()); return true;
                case Key.X: ToolRedact_Click(this, new RoutedEventArgs()); return true;
                default: return false;
            }
        }

        // Ctrl+A and the page menu's Select All. Prefer selecting all annotations (shows where
        // everything is, makes stacked annotations editable); fall back to selecting page text when
        // there are none on screen.
        private void SelectAllInView()
        {
            if (!SelectAllAnnotations()) SelectAllText();
        }

        // Ctrl+V and the page menu's Paste. Copied annotations take priority over whatever image or
        // text the Windows clipboard holds.
        private void PasteInView(int pageIndex)
        {
            if (_annotationClipboard.Count > 0) PasteAnnotations(pageIndex);
            else PasteFromClipboard();
        }

        // Whether Paste has anything to place. The clipboard is checked when a menu opens, and
        // another program holding it open is treated as nothing to paste.
        private bool CanPasteInView()
        {
            if (_annotationClipboard.Count > 0) return true;
            try { return Clipboard.ContainsImage() || Clipboard.ContainsText(); }
            catch (System.Runtime.InteropServices.ExternalException) { return false; }
        }

        // Alt+Shift+G: Film Grain on or off, kept in step with the theme menu's check box.
        private void ToggleFilmGrain() => _themePicker.ToggleFilmGrain();

        // Ctrl+Shift+N: the keyboard twin of dragging the active tab out of the window.
        private void MoveActiveTabToNewWindow()
        {
            if (_doc is null || _active is null) { SetStatus(Loc("Str_Msg_OpenFirst")); return; }
            TearOutTab(ActiveViewer, _active);
        }

        // Opens the online help / how-to page in the user's default browser.
        private void OnlineHelp_Click(object sender, RoutedEventArgs e)
        {
            try { Process.Start(new ProcessStartInfo("https://killerpdf.net/help.html#shortcuts") { UseShellExecute = true }); }
            catch { }
        }
    }
}
