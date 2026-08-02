using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media.Animation;

// ============================================================
// THEMED WINDOWS SYSTEM MENU (from KillerUI/Shell/SystemMenu.cs)
//
// WindowChrome turns the title bar into a real native caption, which is what buys free snap,
// drag and double-click-maximize - but it also means Windows answers a caption right-click
// (and Alt+Space) with its own stock white HMENU. That menu is drawn by Win32, not WPF, so no
// brush, style or theme in the app can reach it. The ONLY way to theme it is to suppress the
// two messages that raise it and show our own.
//
// So: swallow them and pop a normal WPF ContextMenu, which picks up the app's implicit
// ContextMenu / MenuItem / Separator styles for free. Each item posts back the exact
// WM_SYSCOMMAND the native menu would have sent, so behaviour is identical - including Move and
// Size, which hand off to Windows' own modal drag loops.
//
// KillerPDF adaptations: HTCAPTION already lives in Shell/WindowChrome.cs (not redeclared
// here), the menu comes from MakeThemedMenu() so its TextOptions match the app's other
// code-built menus, and the kit's Anim.FadeIn is inlined (KillerPDF carries no Anim class).
// ============================================================
namespace KillerPDF
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
            if (msg == WM_NCRBUTTONUP && wParam.ToInt32() == HTCAPTION)
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
            int v = lParam.ToInt32();
            return new Point((short)(v & 0xFFFF), (short)((v >> 16) & 0xFFFF));
        }

        private void ShowSystemMenu(Point? screenPoint)
        {
            _sysMenu ??= BuildSystemMenu();

            bool maximized = WindowState == WindowState.Maximized;
            // Windows greys out what does not apply: you cannot Restore a normal window, cannot
            // Maximize an already-maximized one, and cannot Move or Size while maximized.
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
            }

            // KillerPDF keeps its implicit ContextMenu/MenuItem styles in MainWindow.xaml's
            // WINDOW resources (not App.xaml), and an Absolute-placement menu has no inheritance
            // context to find them through - it rendered stock white. PlacementTarget supplies
            // that context; with PlacementMode.Absolute it does not affect position.
            _sysMenu.PlacementTarget = this;
            _sysMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Absolute;
            if (screenPoint is { } p)
            {
                // Screen pixels -> DIP, so the menu lands under the cursor at any DPI.
                var src = PresentationSource.FromVisual(this);
                if (src?.CompositionTarget is { } ct) p = ct.TransformFromDevice.Transform(p);
                _sysMenu.HorizontalOffset = p.X;
                _sysMenu.VerticalOffset = p.Y;
            }
            else
            {
                // Keyboard route: hang it under the top-left of the window, like Windows does.
                _sysMenu.HorizontalOffset = Left + 8;
                _sysMenu.VerticalOffset = Top + 36;
            }

            _sysMenu.IsOpen = true;
            // Inline of the kit's Anim.FadeIn: 150ms opacity ease-out.
            _sysMenu.BeginAnimation(UIElement.OpacityProperty,
                new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(150)))
                {
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                });
        }

        private ContextMenu BuildSystemMenu()
        {
            var menu = MakeThemedMenu();

            // Every codepoint below was verified present in segmdl2.ttf by RENDERING it, not
            // trusted from documentation - this repo has shipped a missing-glyph box before.
            // E923/E921/E922/E8BB are the same four the title-bar caption buttons draw, so the
            // menu and the caption agree. E7C2 is the four-way move arrow, E740 the diagonal
            // resize arrow.
            MenuItem Add(string key, string fallback, int cmd, int glyph, bool danger = false)
            {
                var mi = new MenuItem { Tag = cmd, Padding = new Thickness(12, 7, 24, 7) };
                mi.SetResourceReference(HeaderedItemsControl.HeaderProperty, key);
                // Loc() returns the key itself when a string is missing; fall back to English so a
                // half-translated locale never shows "Str_Sys_Move" in the menu.
                if (Loc(key) == key) mi.Header = fallback;

                var ico = new TextBlock
                {
                    Text = char.ConvertFromUtf32(glyph),
                    FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"),
                    FontSize = 12,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                ico.SetResourceReference(TextBlock.ForegroundProperty,
                                         danger ? "DangerRed" : "MutedTextBrush");
                mi.Icon = ico;

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
            Add("Str_Sys_Close",    "Close",    SC_CLOSE,    0xE8BB, danger: true);
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
