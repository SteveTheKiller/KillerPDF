using System;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace KillerPDF.Controls
{
    /// <summary>The drawn marks of a document's check boxes and radio buttons, by field name and page,
    /// so that every control of a name shows the one state the save writes, on whichever page it sits.</summary>
    internal sealed class RepeatedFieldMarks
    {
        private readonly System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<(int Page, UIElement Mark)>> _checks = [];
        private readonly System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<(int Page, UIElement Dot, string OnValue)>> _radios = [];
        private readonly System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<(int Page, TextBox Box, double Scale)>> _texts = [];

        /// <summary>Forgets a page's marks because its overlay is being built again.</summary>
        internal void BeginPage(int pageIndex)
        {
            foreach (var marks in _checks.Values) marks.RemoveAll(item => item.Page == pageIndex);
            foreach (var dots in _radios.Values) dots.RemoveAll(item => item.Page == pageIndex);
            foreach (var boxes in _texts.Values) boxes.RemoveAll(item => item.Page == pageIndex);
        }

        /// <summary>Adds a box of a text field. Its scale turns points into the box's own pixels,
        /// since the boxes of one name can sit on pages drawn at different sizes.</summary>
        internal void AddText(int pageIndex, string fieldName, TextBox box, double scale = 1)
        {
            if (!_texts.TryGetValue(fieldName, out var boxes)) _texts[fieldName] = boxes = [];
            boxes.Add((pageIndex, box, scale > 0 ? scale : 1));
        }

        /// <summary>Shows a typed value in every other box of the field, across pages.</summary>
        internal void ShowText(string fieldName, string value, TextBox typedIn)
        {
            if (!_texts.TryGetValue(fieldName, out var boxes)) return;
            foreach ((_, TextBox box, _) in boxes)
                if (!ReferenceEquals(box, typedIn)) box.Text = value;
        }

        /// <summary>Shows a font size set in one box in every other box of the field.</summary>
        internal void ShowFontSize(string fieldName, double points, TextBox setIn)
        {
            if (!_texts.TryGetValue(fieldName, out var boxes)) return;
            foreach ((_, TextBox box, double scale) in boxes)
                if (!ReferenceEquals(box, setIn)) box.FontSize = points * scale;
        }

        internal void AddCheck(int pageIndex, string fieldName, UIElement mark)
        {
            if (!_checks.TryGetValue(fieldName, out var marks)) _checks[fieldName] = marks = [];
            marks.Add((pageIndex, mark));
        }

        internal void ShowCheck(string fieldName, bool on)
        {
            if (!_checks.TryGetValue(fieldName, out var marks)) return;
            foreach ((_, UIElement mark) in marks)
                mark.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        }

        internal void AddRadio(int pageIndex, string groupName, UIElement dot, string onValue)
        {
            if (!_radios.TryGetValue(groupName, out var dots)) _radios[groupName] = dots = [];
            dots.Add((pageIndex, dot, onValue));
        }

        internal void SelectRadio(string groupName, string onValue)
        {
            if (!_radios.TryGetValue(groupName, out var dots)) return;
            foreach ((_, UIElement dot, string value) in dots)
                dot.Visibility = value == onValue ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    /// <summary>
    /// Hosts a drawn PDF checkbox or radio widget (WPF's own indicators do not scale to the field)
    /// and gives it what the drawing alone lacks: Tab focus, Space/Enter to toggle, arrow keys
    /// within a radio group, and a CheckBox or RadioButton automation peer (BF-U002).
    /// </summary>
    internal sealed class FormToggleSurface : ContentControl
    {
        internal enum ToggleKind { CheckBox, Radio }

        private readonly Func<bool> _isOn;
        private readonly Action? _activate;

        internal FormToggleSurface(UIElement visual, ToggleKind kind, Func<bool> isOn, Action? activate, string? group = null)
        {
            Kind = kind;
            Group = group;
            _isOn = isOn;
            _activate = activate;
            Content = visual;
            Focusable = true;
            IsTabStop = true;
            Background = Brushes.Transparent;
            Cursor = activate is null ? Cursors.Arrow : Cursors.Hand;
        }

        internal ToggleKind Kind { get; }
        internal string? Group { get; }
        internal bool IsOn => _isOn();
        internal bool IsReadOnly => _activate is null;

        /// <summary>Toggles a checkbox or selects a radio, as a click does.</summary>
        internal void Activate()
        {
            if (_activate is null) return;
            bool before = IsOn;
            _activate();
            if (UIElementAutomationPeer.FromElement(this) is Peer peer) peer.Changed(before, IsOn);
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonDown(e);
            if (e.Handled) return;
            Focus();
            Activate();
            e.Handled = true;
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.Handled) return;
            if (e.Key is Key.Space or Key.Enter)
            {
                Activate();
                e.Handled = true;
            }
            else if (Kind == ToggleKind.Radio && e.Key is Key.Up or Key.Left or Key.Down or Key.Right)
            {
                // Arrow keys move through the group and select, as a radio group does.
                if (Parent is not Panel panel) return;
                FormToggleSurface[] group = [.. panel.Children.OfType<FormToggleSurface>()
                    .Where(item => item.Kind == ToggleKind.Radio && item.Group == Group)
                    .OrderBy(item => Canvas.GetTop(item)).ThenBy(item => Canvas.GetLeft(item))];
                int index = Array.IndexOf(group, this);
                if (index < 0 || group.Length < 2) return;
                int step = e.Key is Key.Up or Key.Left ? -1 : 1;
                FormToggleSurface next = group[(index + step + group.Length) % group.Length];
                next.Focus();
                next.Activate();
                e.Handled = true;
            }
        }

        protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this);

        private sealed class Peer(FormToggleSurface owner) : FrameworkElementAutomationPeer(owner),
            IToggleProvider, ISelectionItemProvider
        {
            private FormToggleSurface Surface => (FormToggleSurface)Owner;

            protected override AutomationControlType GetAutomationControlTypeCore() =>
                Surface.Kind == ToggleKind.CheckBox ? AutomationControlType.CheckBox : AutomationControlType.RadioButton;

            protected override string GetClassNameCore() =>
                Surface.Kind == ToggleKind.CheckBox ? "CheckBox" : "RadioButton";

            protected override bool IsKeyboardFocusableCore() => true;

            public override object? GetPattern(PatternInterface patternInterface) =>
                (patternInterface, Surface.Kind) switch
                {
                    (PatternInterface.Toggle, ToggleKind.CheckBox) => this,
                    (PatternInterface.SelectionItem, ToggleKind.Radio) => this,
                    _ => base.GetPattern(patternInterface)
                };

            internal void Changed(bool before, bool after)
            {
                if (before == after) return;
                if (Surface.Kind == ToggleKind.CheckBox)
                    RaisePropertyChangedEvent(TogglePatternIdentifiers.ToggleStateProperty,
                        before ? ToggleState.On : ToggleState.Off, after ? ToggleState.On : ToggleState.Off);
                else
                    RaisePropertyChangedEvent(SelectionItemPatternIdentifiers.IsSelectedProperty, before, after);
            }

            public ToggleState ToggleState => Surface.IsOn ? ToggleState.On : ToggleState.Off;

            public void Toggle()
            {
                if (Surface.IsReadOnly) throw new ElementNotEnabledException();
                Surface.Activate();
            }

            public bool IsSelected => Surface.IsOn;
            public IRawElementProviderSimple? SelectionContainer => null;

            public void Select()
            {
                if (Surface.IsReadOnly) throw new ElementNotEnabledException();
                if (!Surface.IsOn) Surface.Activate();
            }

            public void AddToSelection() => throw new InvalidOperationException();
            public void RemoveFromSelection() => throw new InvalidOperationException();
        }
    }
}
