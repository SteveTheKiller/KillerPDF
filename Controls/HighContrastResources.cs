#nullable enable
using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Controls;

namespace KillerFamily.Accessibility
{
    /// <summary>Windows contrast colors override the app palette without saving a theme change.</summary>
    public static class HighContrastResources
    {
        public static readonly DependencyProperty IsActiveProperty = DependencyProperty.RegisterAttached(
            "IsActive", typeof(bool), typeof(HighContrastResources), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));
        public static bool GetIsActive(DependencyObject element) => (bool)element.GetValue(IsActiveProperty);
        public static void SetIsActive(DependencyObject element, bool value) => element.SetValue(IsActiveProperty, value);
        private static readonly Dictionary<Style, Style> CaptionStyles = new();
        private static readonly HashSet<Style> PreparedStyles = new();
        private static bool _initialized;
        private static int _refreshPending;
        private static ResourceDictionary? _overlay;
        private static ResourceDictionary? _owner;
        private static readonly Dictionary<string, object?> Originals = new();
        internal static event Action? SystemColorsRefreshed;

        internal static void Initialize()
        {
            if (!_initialized)
            {
                _initialized = true;
                SystemParameters.StaticPropertyChanged += OnSystemColorsChanged;
            }
            Refresh();
        }

        internal static void PrepareCaption(Control control)
        {
            var original = control.Style;
            if (original == null || PreparedStyles.Contains(original)) return;
            var app = Application.Current;
            if (app == null) return;
            object? caption = app.TryFindResource("ChromeButton");
            bool isCaption = ReferenceEquals(original, caption)
                || ReferenceEquals(original, app.TryFindResource("ChromeCloseButton"));
            if (!isCaption) return;
            if (!CaptionStyles.TryGetValue(original, out var prepared))
            {
                prepared = new Style(original.TargetType, original);
                var hoverProperties = new List<DependencyProperty> { UIElement.IsMouseOverProperty, Button.IsPressedProperty };
                var snap = typeof(HighContrastResources).Assembly.GetType("KillerFamily.Windowing.SnapLayouts");
                foreach (string name in new[] { "IsHoveredProperty", "IsPressedProperty" })
                    if (snap?.GetField(name)?.GetValue(null) is DependencyProperty property) hoverProperties.Add(property);
                foreach (var hover in hoverProperties)
                {
                    var trigger = new MultiTrigger();
                    trigger.Conditions.Add(new Condition(IsActiveProperty, true));
                    trigger.Conditions.Add(new Condition(hover, true));
                    trigger.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension(SystemColors.HighlightTextBrushKey)));
                    prepared.Triggers.Add(trigger);
                }
                CaptionStyles.Add(original, prepared);
                PreparedStyles.Add(prepared);
            }
            control.Style = prepared;
        }

        private static void OnSystemColorsChanged(object? sender, PropertyChangedEventArgs args)
        {
            if (!string.IsNullOrEmpty(args.PropertyName) && args.PropertyName != nameof(SystemParameters.HighContrast)) return;
            RefreshSystemColors();
        }

        internal static void RefreshSystemColors()
        {
            var app = Application.Current;
            if (app == null || app.Dispatcher.HasShutdownStarted) return;
            if (System.Threading.Interlocked.Exchange(ref _refreshPending, 1) != 0) return;
            app.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(() =>
            {
                System.Threading.Interlocked.Exchange(ref _refreshPending, 0);
                Restore();
                Refresh();
                SystemColorsRefreshed?.Invoke();
            }));
        }

        private static void Refresh()
        {
            if (Application.Current == null) return;
            Refresh(SystemParameters.HighContrast, SystemColors.WindowBrush, SystemColors.WindowTextBrush,
                SystemColors.HighlightBrush, SystemColors.HighlightTextBrush);
        }

        /// <summary>The same refresh with the high contrast state and system brushes passed in, so
        /// tests can drive it without changing the Windows setting.</summary>
        internal static void Refresh(bool highContrast, Brush windowBrush, Brush windowTextBrush,
            Brush highlightBrush, Brush highlightTextBrush)
        {
            var app = Application.Current;
            if (app == null) return;
            if (_owner != app.Resources)
            {
                Restore();
                _owner = app.Resources;
            }
            if (!highContrast)
            {
                Restore();
                return;
            }
            if (_overlay != null)
            {
                Publish();
                return;
            }
            var d = new ResourceDictionary();
            void Brushes(Brush brush, params string[] keys)
            {
                foreach (string key in keys) d[key] = brush;
            }
            Brushes(windowBrush, "BackgroundBrush", "SurfaceBrush", "PaneBrush", "ContentPaneBrush",
                "ScanContentPaneBrush", "TableHeaderBrush", "TitleBarBrush", "FooterBackgroundBrush",
                "OverlayWindowBrush", "MenuBackgroundBrush", "BgFlyout", "SidebarPaneBrush", "FileDialogPaneBrush",
                "ComboFieldBrush", "ComboFieldHoverBrush", "ComboPopupBrush", "ComboButtonBrush",
                "ComboButtonHoverBrush", "DropdownArrowBrush", "DialogTitleBarBrush", "RowAltBrush",
                "RowHoverBrush", "SurfaceHoverBrush", "OutlinePressedBrush");
            Brushes(windowBrush, "AboutPanelBrush", "EditorPaneBrush", "InputFieldBrush", "KeyCapBrush",
                "CalendarEvenColumnBrush", "CalendarOddColumnBrush", "CalendarEvenHeaderBrush", "CalendarOddHeaderBrush",
                "CalendarOutsideMonthBrush", "CalendarWeekendBrush", "CalendarWeekendHeaderBrush", "CalendarDayHoverBrush",
                "CalendarWeekendHoverBrush", "GraphCanvasBrush", "ListPaneBrush", "OutlineFaceBrush", "TextFieldBrush",
                "SearchFieldBrush", "ScrollTrackBrush", "RadioWellBrush", "ChipFaceBrush", "ChipHoverBrush", "ChipBrush",
                "ConsoleSurfaceBrush", "ReportCardBrush", "WatchLogBrush", "TableRowBrush", "TopologyNodeBrush",
                "TopologyNodeHoverBrush", "EditorBackgroundBrush", "TerminalBackgroundBrush", "ToolContentBrush",
                "ToolTreeBrush", "SolidBackgroundBrush", "TabInactiveBrush", "TabSeamPatchBrush", "SidebarRailBrush",
                "ComparisonBarBrush", "ComparisonBarHoverBrush", "AnnotationBarBrush", "KeyboardKeyBrush", "TabActiveBrush",
                "BgCanvas", "BgRecentPanel", "BgDragHandle",
                "FooterZoomSurfaceBrush", "FooterZoomClosedBrush", "SortButtonBrush", "MonitorCellBrush", "MonitorHoverBrush");
            Brushes(windowTextBrush, "TextBrush", "MutedTextBrush", "DimTextBrush", "ChromeTextBrush",
                "MenuTextBrush", "TextFooter", "FilenameBrush", "CaptionTextBrush", "DialogCaptionTextBrush",
                "OverlayCloseBrush", "CaptionGlyphBrush", "ComboTextBrush", "OutlineRestBrush", "OutlineBtnBrush", "RadioAccent",
                "RadioHoverFgBrush", "ScrollThumbBrush", "ScrollThumbHoverBrush", "AccentLogo", "HeaderLineBrush",
                "CardBorderBrush", "PaneBorderBrush", "AppBorderBrush", "OverlayEdgeBrush", "OverlayRuleBrush",
                "InputBorderBrush", "InputHoverBrush",
                "MenuBorderBrush", "MenuSeparatorBrush", "ButtonEdgeBrush", "WindowEdgeBrush", "DialogFrameBrush",
                "SurfaceButtonEdgeBrush", "KsCatFile", "KsCatTools", "KsCatEdit", "KsCatNav", "KsCatView",
                "KsCatSearch", "KsCatHelp", "KsCatOcr", "KsCatAppt", "KsCatTabs");
            Brushes(windowTextBrush, "CaptionCloseBrush", "AboutCloseFg", "OutlineTextBrush", "TitleMetaBrush",
                "DatabaseActiveTextBrush", "DatabaseMetadataBrush", "InputEdgeBrush", "PaneEdgeBrush", "BarEdgeBrush",
                "BarSepBrush", "ChipEdgeBrush", "AboutPanelEdgeBrush", "ConsoleTextBrush",
                "ConsoleBorderBrush", "ReportMetricBrush", "TreeLineBrush", "TopologyNodeTextBrush", "TopologyNodeBorderBrush",
                "TopologyNodeHoverTextBrush", "TerminalForegroundBrush", "MonitorTextBrush", "MonitorMutedBrush",
                "ComparisonBarForegroundBrush", "AnnotationBarBorderBrush", "WindowFrameBrush", "FooterAccentBrush", "DropBorder");
            // Menu hover is a selection, so it takes the Highlight pair the way Windows' own menus do.
            Brushes(highlightBrush, "PrimaryBrush", "PrimaryHoverBrush", "PrimaryPressedBrush",
                "PrimaryFillBaseBrush", "PrimaryFillBrush", "SelectionBg", "RowSelectedBrush", "SelectionAccent",
                "OutlineHoverBrush", "OutlineHoverFillBrush", "CaptionHoverBrush", "CaptionCloseHoverBrush",
                "CalendarViewSelectedBrush", "MenuHoverBrush");
            Brushes(highlightBrush, "DatabaseSelectionBrush", "TextSelectionBrush", "TabActiveRingBrush",
                "CheckBoxCheckedBackgroundBrush", "CheckBoxCheckedBrush", "FocusedPaneBrush", "SettingsOpenRowBg");
            Brushes(highlightTextBrush, "OnPrimaryBrush", "SelectionFg", "OutlineHoverTextBrush",
                "ComboHighlightTextBrush", "CaptionHoverTextBrush", "CaptionCloseHoverTextBrush", "MenuHoverTextBrush");
            Brushes(highlightTextBrush, "CaptionCloseHoverFgBrush", "AboutCloseHoverFg", "CaptionPressedTextBrush",
                "DatabaseSelectionTextBrush", "TextSelectionTextBrush", "GridSelectedTextBrush", "CheckMarkBrush",
                "CheckBoxCheckBrush", "OnOutlineBtnBrush");
            Brushes(System.Windows.Media.Brushes.Transparent, "CaptionButtonBrush");
            d["WindowFrameThickness"] = new Thickness(1);
            d["WindowFramePadding"] = new Thickness(1);
            foreach (string key in new[] { "WindowFrameMargin", "FrameOuterLightThickness", "FrameOuterDarkThickness",
                "FrameInnerLightThickness", "FrameInnerDarkThickness" }) d[key] = new Thickness(0);
            foreach (string key in new[] { "GrainOpacity", "PaneShadowOpacity", "BarShadowOpacity",
                "FlyoutShadowOpacity", "ShortcutShadowOpacity", "WordmarkShadowOpacity", "WordmarkEmbossOpacity",
                "WordmarkAccentShadowOpacity", "IconShadowOpacity", "AboutShadowOpacity", "SelectedTileShadowOpacity" }) d[key] = 0.0;
            _overlay = d;
            Publish();
        }

        private static void Publish()
        {
            if (_owner == null || _overlay == null) return;
            foreach (Window window in Application.Current.Windows) SetIsActive(window, true);
            // Locale loaders reserve merged dictionary slots. Local overrides leave those slots
            // and the saved app palette intact, including while a language or theme changes.
            var localKeys = new HashSet<object>();
            foreach (object key in _owner.Keys) localKeys.Add(key);
            foreach (string key in _overlay.Keys)
            {
                if (!Originals.ContainsKey(key))
                    Originals[key] = localKeys.Contains(key) ? _owner[key] : DependencyProperty.UnsetValue;
                _owner[key] = _overlay[key];
            }
        }

        private static void Restore()
        {
            if (Application.Current != null)
                foreach (Window window in Application.Current.Windows) SetIsActive(window, false);
            if (_owner != null && _overlay != null)
                foreach (var entry in Originals)
                    if (ReferenceEquals(_owner[entry.Key], _overlay[entry.Key]))
                    {
                        if (ReferenceEquals(entry.Value, DependencyProperty.UnsetValue)) _owner.Remove(entry.Key);
                        else _owner[entry.Key] = entry.Value;
                    }
            Originals.Clear();
            _overlay = null;
        }
    }
}
