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
    /// <summary>The drawn marks of the check boxes on a page, by field name, so that every box of
    /// a name shows the one state the save writes, as a radio group's dots do.</summary>
    internal sealed class RepeatedCheckMarks
    {
        private readonly System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<UIElement>> _marks = [];

        internal void Add(string fieldName, UIElement mark)
        {
            if (!_marks.TryGetValue(fieldName, out var marks)) _marks[fieldName] = marks = [];
            marks.Add(mark);
        }

        internal void Show(string fieldName, bool on)
        {
            if (!_marks.TryGetValue(fieldName, out var marks)) return;
            foreach (UIElement mark in marks)
                mark.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
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
