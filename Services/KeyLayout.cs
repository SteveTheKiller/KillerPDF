using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Input;

namespace KillerPDF.Services
{
    // ============================================================
    // Keyboard-layout aware shortcut matching (#153).
    //
    // WPF's Key enum is a VIRTUAL KEY code, which is positional: it says which
    // key was pressed, not what character that key types. Every punctuation
    // shortcut matched by VK is therefore a US-layout assumption. On a German
    // keyboard "?" is Shift+ss and "=" is Shift+0, so Ctrl+? and Ctrl+= never
    // matched - and the exact-equality modifier test failed a second time,
    // because producing those characters holds Shift down as well.
    //
    // So punctuation is matched by the character the keystroke PRODUCES under
    // the active layout, which works on German, AZERTY, Nordic and everything
    // else at once instead of one layout at a time. Letters and F-keys keep
    // using the VK path: they are positional by nature and much cheaper.
    // ============================================================
    internal static class KeyLayout
    {
        [DllImport("user32.dll")] private static extern IntPtr GetKeyboardLayout(uint idThread);
        [DllImport("user32.dll")] private static extern uint MapVirtualKeyEx(uint uCode, uint uMapType, IntPtr dwhkl);
        [DllImport("user32.dll")] private static extern short VkKeyScanEx(char ch, IntPtr dwhkl);
        [DllImport("user32.dll")]
        private static extern int ToUnicodeEx(uint wVirtKey, uint wScanCode, byte[] lpKeyState,
            [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pwszBuff, int cchBuff, uint wFlags, IntPtr dwhkl);

        private const uint MAPVK_VK_TO_VSC = 0;
        private const int VK_SHIFT = 0x10;
        private const int VK_SPACE = 0x20;

        /// <summary>The character <paramref name="key"/> types on the CURRENT layout, or '\0' when
        /// it types nothing (F-keys, arrows, modifiers). Ctrl is deliberately NOT fed to the
        /// translator - with Ctrl down Windows reports control codes rather than characters.</summary>
        internal static char CharFor(Key key, bool shift)
        {
            try
            {
                uint vk = (uint)KeyInterop.VirtualKeyFromKey(key);
                if (vk == 0) return '\0';
                IntPtr hkl = GetKeyboardLayout(0);
                uint sc = MapVirtualKeyEx(vk, MAPVK_VK_TO_VSC, hkl);

                var state = new byte[256];
                if (shift) state[VK_SHIFT] = 0x80;

                var sb = new StringBuilder(8);
                int rc = ToUnicodeEx(vk, sc, state, sb, sb.Capacity, 0, hkl);

                // DEAD KEYS: a negative result means this key is a dead key (accents on many
                // European layouts) and the translator has just swallowed it into its internal
                // state, where it would silently combine with whatever the user types next.
                // Pressing a harmless key through the same call clears it back out. Calling
                // twice is the documented dance; without it, typing an accent right after a
                // shortcut check produces the wrong letter.
                if (rc < 0)
                {
                    var flush = new StringBuilder(8);
                    ToUnicodeEx(VK_SPACE, MapVirtualKeyEx(VK_SPACE, MAPVK_VK_TO_VSC, hkl),
                                new byte[256], flush, flush.Capacity, 0, hkl);
                    return '\0';
                }
                return rc > 0 ? sb[sb.Length - 1] : '\0';
            }
            catch { return '\0'; }   // never let a shortcut check throw
        }

        /// <summary>True when Ctrl (and not Alt) is held and the keystroke types one of
        /// <paramref name="chars"/>. Shift is ignored on purpose: on most layouts the shifted
        /// state is exactly how these characters are produced.</summary>
        internal static bool IsCtrlChar(Key key, params char[] chars)
        {
            var mods = Keyboard.Modifiers;
            if ((mods & ModifierKeys.Control) == 0) return false;
            if ((mods & ModifierKeys.Alt) != 0) return false;   // AltGr combinations are not ours
            char c = CharFor(key, (mods & ModifierKeys.Shift) != 0);
            if (c == '\0') return false;
            foreach (char want in chars) if (c == want) return true;
            return false;
        }

        /// <summary>Can this character be typed on the current layout WITHOUT Shift? Used to label
        /// shortcuts honestly: on a layout where "=" needs Shift, advertising Ctrl+= is a lie.</summary>
        internal static bool TypedUnshifted(char ch)
        {
            try
            {
                short r = VkKeyScanEx(ch, GetKeyboardLayout(0));
                if (r == -1) return false;                  // not typeable at all here
                return ((r >> 8) & 0xFF) == 0;              // high byte 0 = no modifiers needed
            }
            catch { return false; }
        }

        /// <summary>The character to print for "zoom in" on this layout: "+" when it is a plain
        /// keypress, otherwise "=". On US both are unshifted and "=" is the familiar spelling; on
        /// German "+" is the unshifted one and "=" would need Shift.</summary>
        internal static string ZoomInChar() => TypedUnshifted('=') ? "=" : "+";

        internal static string ZoomOutChar() => "-";
    }
}
