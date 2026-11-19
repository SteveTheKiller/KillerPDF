using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using KillerFamily.Accessibility;

// ============================================================
// THEMED WINDOWS SYSTEM MENU
//
// WindowChrome turns the title bar into a real native caption, which is what buys free snap,
// drag and double-click-maximize - but it also means Windows answers a caption right-click
// (and Alt+Space) with its own stock white HMENU. That menu is drawn by Win32, not WPF, so no
// brush, style or theme in the app can reach it. The ONLY way to theme it is to suppress the
// two messages that raise it and show our own.
//
// So: swallow them and pop a normal WPF ContextMenu, which picks up the app's implicit
// ContextMenu / MenuItem / Separator styles for free. Each item posts back the exact
// WM_SYSCOMMAND the native menu would have sent, so behavior is identical - including Move and
// Size, which hand off to Windows' own modal drag loops.
//
// ---- Wiring it up -------------------------------------------------------------------------
//   1. Drop this file in as a MainWindow partial and fix the namespace.
//   2. The window needs an HwndSource hook. If Chrome.cs already has a WndProc, add this as the
//      FIRST thing it does:
//
//          if (TryHandleSystemMenu(msg, wParam, lParam)) { handled = true; return IntPtr.Zero; }
//
//   3. Chrome.cs must define `private const int HTCAPTION = 2;` (most already do).
//   4. Add the six Str_Sys_* keys to en-US.xaml at minimum. Missing keys fall back to English
//      rather than rendering the key name, so a half-translated locale still reads correctly.
//
// TRAP (KillerPDF, 2026-07-31): an Absolute-placement ContextMenu with no PlacementTarget has
// NO inheritance context, so it only sees APP-level implicit styles. An app that keeps its
// ContextMenu/MenuItem styles in the window's resources (KillerPDF does) gets the stock white
// menu. ShowSystemMenu below therefore sets PlacementTarget = this - harmless with
// PlacementMode.Absolute (it supplies the resource context without affecting position). Do not
// remove it when porting.
//
// Originally written for KillerScan; KillerShell's older Minimize/Maximize/Close-only copy in
// Chrome.cs was replaced with this file on 2026-10-23, when Close gained Windows' Alt+F4 hint and
// the default item its bold. Ported into
// the kit 2026-07-30 because three of five apps were still showing the stock white menu.
//
// ---- App hooks (optional partial methods; leave them out to keep the kit menu) -----------
//   CustomizeSystemMenuItem(item, icon, command, danger): runs once per item after the kit
//      builds it. An app that styles menu icons through its own MenuIcon style clears the
//      icon's local font, size and alignment and sets that style here, and may clear the item's
//      Padding to use its implicit MenuItem padding.
//   UseSystemMenuAccessKeys(ref use): set true to show and handle Windows' access letters
//      (R, M, S, N, X and C, with Close labeled "Alt+F4 / C") while the menu is open.
//   CustomizeSystemMenu(menu): runs once on the new menu before any item is added, for an app
//      that builds its code-made menus with its own style or text options.
//   AddSystemMenuItems(menu): runs once after the separator above Close, for the app's own
//      rows; an app that adds any ends them with its own Separator.
//   SystemMenuOpening(): runs each time the menu opens, before the kit enables its items, so
//      the app's own rows can show their current state.
// ============================================================
namespace KillerPDF.Shell
{
    public partial class MainWindow
    {
        private const int WM_NCRBUTTONUP = 0x00A5;
        private const int WM_SYSCOMMAND  = 0x0112;

        private const int SC_SIZE     = 0xF000;
        private const int SC_MOVE     = 0xF010;
        private const int SC_MINIMIZE = 0xF020;
        private const int SC_MAXIMIZE = 0xF030;
        private const int SC_CLOSE    = 0xF060;
        private const int SC_KEYMENU  = 0xF100;
        private const int SC_RESTORE  = 0xF120;

        private ContextMenu? _sysMenu;

        /// <summary>
        /// Call from WndProc. Returns true when the message was ours and the native menu should
        /// be suppressed.
        /// </summary>
        private bool TryHandleSystemMenu(int msg, IntPtr wParam, IntPtr lParam)
        {
            // Right-click on the caption.
            if (msg == WM_NCRBUTTONUP && wParam.ToInt64() == HTCAPTION)
            {
                ShowSystemMenu(ScreenPointFromLParam(lParam));
                return true;
            }

            // Alt+Space. SC_KEYMENU with a space is the keyboard route to the same menu; masking
            // the low nibble is required because Windows packs state into it.
            if (msg == WM_SYSCOMMAND && (wParam.ToInt64() & 0xFFF0) == SC_KEYMENU && lParam.ToInt64() == ' ')
            {
                ShowSystemMenu(null);
                return true;
            }

            return false;
        }

        /// <summary>lParam of a non-client mouse message packs screen coords as two shorts.</summary>
        private static Point ScreenPointFromLParam(IntPtr lParam)
        {
            int v = unchecked((int)lParam.ToInt64());   // ToInt32 throws on a 64-bit value
            return new Point((short)(v & 0xFFFF), (short)((v >> 16) & 0xFFFF));
        }

        private void ShowSystemMenu(Point? screenPoint)
        {
            _sysMenu ??= BuildSystemMenu();
            SystemMenuOpening();

            bool maximized = WindowState == WindowState.Maximized;
            // Windows grays out what does not apply: you cannot Restore a normal window, cannot
            // Maximize an already-maximized one, and cannot Move or Size while maximized. It also
            // bolds the default item: Restore when maximized, Close otherwise.
            int defaultCommand = maximized ? SC_RESTORE : SC_CLOSE;
            foreach (object o in _sysMenu.Items)
            {
                if (o is not MenuItem mi || mi.Tag is not int cmd) continue;
                mi.IsEnabled = cmd switch
                {
                    SC_RESTORE  => maximized,
                    SC_MAXIMIZE => !maximized,
                    SC_MOVE or SC_SIZE => !maximized,
                    _ => true,
                };
                mi.FontWeight = cmd == defaultCommand ? FontWeights.Bold : FontWeights.Normal;
            }

            // See the TRAP note in the header: without a PlacementTarget an Absolute-placement
            // menu has no inheritance context and misses window-level implicit styles.
            _sysMenu.PlacementTarget = this;
            _sysMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Absolute;
            Point at;
            if (screenPoint is { } p)
            {
                // Screen pixels -> DIP, so the menu lands under the cursor at any DPI.
                var src = PresentationSource.FromVisual(this);
                if (src?.CompositionTarget is { } ct) p = ct.TransformFromDevice.Transform(p);
                at = p;
            }
            else
            {
                // Keyboard route: hang it under the top-left of the window, like Windows does.
                // While maximized Left/Top keep the RESTORE position, which put the menu where the
                // restored window would sit, possibly on another monitor, so the real screen
                // position is used there. A normal window keeps Left/Top, the same point.
                if (maximized)
                {
                    Point corner = PointToScreen(new Point(8, 36));
                    if (PresentationSource.FromVisual(this)?.CompositionTarget is { } target)
                        corner = target.TransformFromDevice.Transform(corner);
                    at = corner;
                }
                else
                {
                    at = new Point(Left + 8, Top + 36);
                }
            }

            // The point is where the visible card starts, so its shadow halo (the MenuRoot margin,
            // read the way FlyoutPlacement reads it) goes before it.
            _sysMenu.ApplyTemplate();
            Thickness halo = _sysMenu.Template?.FindName("MenuRoot", _sysMenu) is FrameworkElement card ? card.Margin : default;
            Point origin = SystemMenuOrigin(at, halo);
            _sysMenu.HorizontalOffset = origin.X;
            _sysMenu.VerticalOffset = origin.Y;

            _sysMenu.IsOpen = true;
            Controls.Anim.FadeIn(_sysMenu);
        }

        /// <summary>The popup origin that puts the visible card, not its transparent halo, at
        /// <paramref name="cardCorner"/>. Both are in DIPs.</summary>
        internal static Point SystemMenuOrigin(Point cardCorner, Thickness halo)
            => new(cardCorner.X - halo.Left, cardCorner.Y - halo.Top);

        partial void CustomizeSystemMenuItem(MenuItem item, Glyph icon, int command, bool danger);
        partial void UseSystemMenuAccessKeys(ref bool use);
        partial void CustomizeSystemMenu(ContextMenu menu);
        partial void AddSystemMenuItems(ContextMenu menu);
        partial void SystemMenuOpening();

        private ContextMenu BuildSystemMenu()
        {
            var menu = new ContextMenu();
            CustomizeSystemMenu(menu);
            bool accessKeys = false;
            UseSystemMenuAccessKeys(ref accessKeys);

            // Every codepoint below was verified present in segmdl2.ttf by RENDERING it, not
            // trusted from documentation - this repo has shipped a missing-glyph box before.
            // E923/E921/E922/E8BB are the same four the title-bar caption buttons draw, so the
            // menu and the caption agree. E7C2 is the four-way move arrow, E740 the diagonal
            // resize arrow.
            MenuItem Add(string key, string fallback, int cmd, int glyph, string gesture = "", bool danger = false)
            {
                var mi = new MenuItem { Tag = cmd, Padding = new Thickness(12, 7, 24, 7), InputGestureText = gesture };
                mi.SetResourceReference(HeaderedItemsControl.HeaderProperty, key);
                // Loc() returns the key itself when a string is missing; fall back to English so a
                // half-translated locale never shows "Str_Sys_Move" in the menu.
                if (Loc(key) == key) mi.Header = fallback;

                var ico = new Glyph
                {
                    Text = char.ConvertFromUtf32(glyph),
                    FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"),
                    FontSize = 12,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                ico.SetResourceReference(TextBlock.ForegroundProperty,
                                         danger ? "MenuDangerBrush" : "MutedTextBrush");
                mi.Icon = ico;

                if (accessKeys)
                {
                    string letter = cmd switch
                    {
                        SC_RESTORE => "R", SC_MOVE => "M", SC_SIZE => "S",
                        SC_MINIMIZE => "N", SC_MAXIMIZE => "X", _ => "C",
                    };
                    if (cmd == SC_CLOSE) mi.InputGestureText = "Alt+F4 / C";
                    else if (gesture.Length == 0) mi.InputGestureText = letter;
                    menu.KeyDown += (_, e) =>
                    {
                        if (Keyboard.Modifiers != ModifierKeys.None || e.Key.ToString() != letter || !mi.IsEnabled) return;
                        menu.IsOpen = false;
                        SendSysCommand(cmd);
                        e.Handled = true;
                    };
                }

                CustomizeSystemMenuItem(mi, ico, cmd, danger);
                mi.Click += (_, _) => SendSysCommand(cmd);
                menu.Items.Add(mi);
                return mi;
            }

            Add("Str_Sys_Restore",  "Restore",  SC_RESTORE,  0xE923);
            Add("Str_Sys_Move",     "Move",     SC_MOVE,     0xE7C2);
            Add("Str_Sys_Size",     "Size",     SC_SIZE,     0xE740);
            Add("Str_Sys_Minimize", "Minimize", SC_MINIMIZE, 0xE921);
            Add("Str_Sys_Maximize", "Maximize", SC_MAXIMIZE, 0xE922);
            menu.Items.Add(new Separator());
            AddSystemMenuItems(menu);
            Add("Str_Sys_Close",    "Close",    SC_CLOSE,    0xE8BB, gesture: "Alt+F4", danger: true);
            return menu;
        }

        private void SendSysCommand(int cmd)
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            // Post rather than Send: the menu is still closing, and SC_MOVE / SC_SIZE start a
            // modal loop that must not run inside the click handler.
            PostMessage(hwnd, WM_SYSCOMMAND, new IntPtr(cmd), IntPtr.Zero);
        }

        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr hWnd, int Msg, IntPtr wParam, IntPtr lParam);
    }
}
