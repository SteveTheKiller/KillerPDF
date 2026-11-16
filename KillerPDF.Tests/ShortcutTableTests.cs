using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using KillerPDF;
using Xunit;

namespace KillerPDF.Tests
{
    /// <summary>
    /// Invariants of the one shortcut table (Services/ShortcutTable.cs).
    ///
    /// The table was unified in 1.7.5 from two hand maintained copies that had quietly drifted:
    /// Alt+M existed in the list and not on the keyboard map, Home and End were captioned
    /// differently in each view, and Ctrl+B was documented as bold in one section of the list and
    /// as the sidebar in another while the code did neither consistently. Unifying removes the
    /// opportunity for that; these tests are what keep it removed.
    ///
    /// No WPF here on purpose. The table is plain data, which is why the csproj links the single
    /// file rather than referencing the app, the same arrangement OcrCatalogTests uses.
    /// </summary>
    public class ShortcutTableTests
    {
        private static readonly string StringsDir =
            Path.Combine(RepoRoot(), "Strings");

        private static string RepoRoot([System.Runtime.CompilerServices.CallerFilePath] string source = "")
        {
            // Source-based lookup also supports isolated build outputs outside the checkout.
            var dir = new DirectoryInfo(Path.GetDirectoryName(source)!);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Strings", "en-US.xaml")))
                dir = dir.Parent;
            Assert.True(dir != null, "could not locate the repo root from the test source");
            return dir!.FullName;
        }

        private static HashSet<string> EnglishKeys()
        {
            var doc = XDocument.Load(Path.Combine(StringsDir, "en-US.xaml"));
            XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
            return [.. doc.Root!.Elements()
                      .Select(e => (string?)e.Attribute(x + "Key"))
                      .Where(k => k != null)
                      .Select(k => k!)];
        }

        /// <summary>
        /// THE one that matters. Two bindings claiming the same key on the same layer means the
        /// keyboard map silently shows whichever was declared last, which is exactly how Ctrl+B
        /// managed to mean two different things without anyone noticing.
        /// </summary>
        [Fact]
        public void NoKeyIsClaimedTwiceOnTheSameLayer()
        {
            var duplicates = ShortcutTable.AllCapClaims()
                                          .GroupBy(c => c)
                                          .Where(g => g.Count() > 1)
                                          .Select(g => g.Key)
                                          .ToList();

            Assert.True(duplicates.Count == 0,
                "these keys are claimed by more than one binding: " + string.Join(", ", duplicates));
        }

        [Theory]
        [InlineData("Str_Lbl_DigitalSig")]
        [InlineData("Str_TableExport_Title")]
        [InlineData("Str_Pages_FixOrientation")]
        [InlineData("Str_Pages_Straighten")]
        [InlineData("Str_Pages_DivideSpreads")]
        [InlineData("Str_Ctx_AddBlankPage")]
        [InlineData("Str_Pages_Reverse")]
        [InlineData("Str_Measure_Distance")]
        [InlineData("Str_Measure_Perimeter")]
        [InlineData("Str_Measure_Area")]
        [InlineData("Str_Measure_Angle")]
        public void ExpandedActionsHaveKeyboardBindings(string label)
            => Assert.Contains(ShortcutTable.KsAll, binding => binding.LabelKey == label && binding.Caps.Length > 0);

        /// <summary>Every cap the table lights has to exist on the drawn board, or the binding is
        /// invisible: the map only renders ids present in KbRows. Alt+M was missing the other way
        /// round, absent from the table while the list advertised it.</summary>
        [Fact]
        public void EveryCapExistsOnTheDrawnKeyboard()
        {
            // KbRows lives in the WPF half, so the ids are mirrored here deliberately: this test is
            // the thing that fails if the two ever disagree, which is the point.
            var board = new HashSet<string>
            {
                "Esc","F1","F2","F3","F4","F5","F6","F7","F8","F9","F10","F11","F12",
                "Grave","D1","D2","D3","D4","D5","D6","D7","D8","D9","D0","Minus","Equals","Back",
                "Ins","Home","PgUp",
                "Tab","Q","W","E","R","T","Y","U","I","O","P","LBr","RBr","Bslash","Del","End","PgDn",
                "Caps","A","S","D","F","G","H","J","K","L","Semi","Quote","Enter",
                "Shift","Z","X","C","V","B","N","M","Comma","Period","Slash","RShift","Up",
                "Ctrl","Win","Alt","Space","RAlt","Menu","RCtrl","Left","Down","Right",
            };

            var missing = ShortcutTable.KsAll
                .SelectMany(b => b.Caps)
                .Select(c => c.Id)
                .Distinct()
                .Where(id => !board.Contains(id))
                .ToList();

            Assert.True(missing.Count == 0,
                "these cap ids are not on the drawn keyboard: " + string.Join(", ", missing));
        }

        [Fact]
        public void EveryLabelKeyExistsInEnglish()
        {
            var english = EnglishKeys();
            var missing = ShortcutTable.KsAll
                .SelectMany(b => new[] { b.LabelKey }.Concat(b.Caps.Select(c => c.LabelKey)))
                .Where(k => k.Length > 0)
                .Distinct()
                .Where(k => !english.Contains(k))
                .ToList();

            Assert.True(missing.Count == 0,
                "these resource keys are referenced but not defined in en-US.xaml: " + string.Join(", ", missing));
        }

        [Fact]
        public void EveryBindingBelongsToADeclaredSection()
        {
            var groups = ShortcutTable.KsGroups.Select(g => g.Cat).ToHashSet();
            var orphans = ShortcutTable.KsAll.Select(b => b.Cat).Distinct()
                                             .Where(c => !groups.Contains(c)).ToList();

            Assert.True(orphans.Count == 0,
                "these categories have bindings but no section: " + string.Join(", ", orphans));
        }

        /// <summary>The keyboard map's hover caption names a key's section. It once kept its own
        /// copy of the titles, missed Document tools and called those keys OCR.</summary>
        [Fact]
        public void EveryBindingsSectionTitleIsItsOwnSection()
        {
            foreach (var (cat, titleKey, _) in ShortcutTable.KsGroups)
                Assert.Equal(titleKey, ShortcutTable.SectionTitleKey(cat));
            Assert.Equal("Str_KS_DocumentTools", ShortcutTable.SectionTitleKey("Docs"));
        }

        /// <summary>A section with no bindings would render as a heading over nothing.</summary>
        [Fact]
        public void EverySectionHasAtLeastOneBinding()
        {
            var used = ShortcutTable.KsAll.Select(b => b.Cat).ToHashSet();
            var empty = ShortcutTable.KsGroups.Select(g => g.Cat).Where(c => !used.Contains(c)).ToList();

            Assert.True(empty.Count == 0,
                "these sections would render empty: " + string.Join(", ", empty));
        }

        /// <summary>The layer prefix is the only thing separating Shift+F3 from F3. A typo in the
        /// prefix silently lands the cap on the base layer, so the parse gets its own check.</summary>
        // Layer passed by name: KbLayer is internal, and an InlineData parameter has to be at least
        // as accessible as the public test method.
        [Theory]
        [InlineData("F3", "Base", "F3")]
        [InlineData("Ctrl:O", "Ctrl", "O")]
        [InlineData("CtrlShift:D1", "CtrlShift", "D1")]
        [InlineData("Shift:F9", "Shift", "F9")]
        [InlineData("Alt:M", "Alt", "M")]
        [InlineData("AltShift:G", "AltShift", "G")]
        public void CapParsesItsLayerPrefix(string id, string layerName, string bare)
        {
            var cap = ShortcutTable.Cap(id);
            Assert.Equal(layerName, cap.Layer.ToString());
            Assert.Equal(bare, cap.Id);
        }

        /// <summary>A cap without its own label inherits the row's; one with a label keeps it. That
        /// is what lets "Home / End" stay one row while Home and End caption separately.</summary>
        [Fact]
        public void CapLabelOverridesTheRowAndOtherwiseInherits()
        {
            var map = ShortcutTable.BuildMap();

            Assert.Equal("Str_Kb_FirstPage", map[KbLayer.Base]["Home"].Label);
            Assert.Equal("Str_Kb_LastPage",  map[KbLayer.Base]["End"].Label);
            Assert.Equal("Str_KS_Open",      map[KbLayer.Ctrl]["O"].Label);   // inherited
        }

        /// <summary>The three bindings 1.7.5 changed, pinned so a later edit cannot quietly undo
        /// them: the sidebar is F9 and Shift+F9, and Ctrl+B is bold rather than the sidebar.</summary>
        [Fact]
        public void TheSidebarIsOnF9AndCtrlBIsBold()
        {
            var map = ShortcutTable.BuildMap();

            Assert.Equal("Str_KS_ToggleSidebar", map[KbLayer.Base]["F9"].Label);
            Assert.Equal("Str_KS_SidebarSide",   map[KbLayer.Shift]["F9"].Label);
            Assert.False(map[KbLayer.Ctrl].ContainsKey("F9"));
            Assert.Equal("Str_Lbl_Bold",         map[KbLayer.Ctrl]["B"].Label);
            Assert.False(map[KbLayer.Ctrl].ContainsKey("B") &&
                         map[KbLayer.Ctrl]["B"].Label == "Str_KS_ToggleSidebar",
                         "Ctrl+B must not be the sidebar again");
        }

        /// <summary>Alt+M was in the list and missing from the map for as long as it existed.</summary>
        [Fact]
        public void AltMIsOnTheMap()
        {
            Assert.Equal("Str_Toolbar_Hide", ShortcutTable.BuildMap()[KbLayer.Alt]["M"].Label);
        }

        [Fact]
        public void FormFieldAndStampShortcutsAreOnTheMap()
        {
            var map = ShortcutTable.BuildMap()[KbLayer.Base];

            Assert.Equal("Str_KS_FormField", map["F"].Label);
            Assert.Equal("Str_Ctx_StampPages", map["S"].Label);
        }

        /// <summary>Tools answer to letters only. The number row used to mirror toolbar positions,
        /// which renumbered whenever a tool was added.</summary>
        [Fact]
        public void ToolsUseLettersOnly()
        {
            var digits = ShortcutTable.KsAll
                .Where(b => b.Cat == "Tools")
                .SelectMany(b => b.Caps)
                .Where(c => c.Layer == KbLayer.Base && c.Id.Length == 2 && c.Id[0] == 'D' && char.IsDigit(c.Id[1]))
                .Select(c => c.Id)
                .ToList();
            Assert.True(digits.Count == 0, "tool rows still claim number keys: " + string.Join(", ", digits));

            var map = ShortcutTable.BuildMap();
            Assert.Equal("Str_Lbl_Shape", map[KbLayer.Base]["O"].Label);
            Assert.Equal("Str_KS_NextShape", map[KbLayer.Shift]["O"].Label);
        }

        [Fact]
        public void NewToolsAndCommandsAreOnTheMap()
        {
            var map = ShortcutTable.BuildMap();

            Assert.Equal("Str_ColorCorrection", map[KbLayer.Base]["E"].Label);
            Assert.Equal("Str_Lbl_Measure", map[KbLayer.Base]["M"].Label);
            Assert.Equal("Str_Lbl_Redact", map[KbLayer.Base]["X"].Label);
            Assert.Equal("Str_Menu_ExportDocument", map[KbLayer.CtrlShift]["E"].Label);
            Assert.Equal("Str_Protect_Name", map[KbLayer.CtrlShift]["P"].Label);
            Assert.Equal("Str_Ocr_ScanToText", map[KbLayer.CtrlShift]["T"].Label);
            Assert.Equal("Str_Theme_FilmGrain", map[KbLayer.AltShift]["G"].Label);
            Assert.Equal("Str_Ctx_MoveToNewWindow", map[KbLayer.CtrlShift]["N"].Label);
            Assert.Equal("Str_DocumentTools", map[KbLayer.CtrlShift]["D"].Label);
        }

        /// <summary>One key per document tool keeps the menu and shortcut rows unambiguous.</summary>
        [Fact]
        public void DocumentToolKeysAreUniqueWithRows()
        {
            var letters = ShortcutTable.DocumentToolKeys.Select(t => t.Key).ToList();
            Assert.Equal(letters.Count, letters.Distinct().Count());
            Assert.All(letters, c => Assert.True(c is >= 'A' and <= 'Z' or '0', $"{c} is not an allowed document-tool key"));
            // One row per tool. A tool with a direct key lights it on the map; the rest are chord-only rows.
            var toolLabels = ShortcutTable.DocumentToolKeys.Select(t => t.LabelKey).ToHashSet();
            Assert.Equal(ShortcutTable.DocumentToolKeys.Length,
                ShortcutTable.KsAll.Count(b => b.Cat == "Docs" && toolLabels.Contains(b.LabelKey)));
            Assert.Equal(ShortcutTable.DocumentToolKeys.Length - ShortcutTable.DocumentToolDirectKeys.Length,
                ShortcutTable.KsAll.Count(b => b.Cat == "Docs" && b.Caps.Length == 0));
            Assert.Equal("Ctrl+Shift+D, P", ShortcutTable.DocumentToolGesture("Str_DocumentTools_Preflight"));
            Assert.Equal("Ctrl+Shift+D, 0", ShortcutTable.DocumentToolGesture("Str_CM_Title"));
        }

        /// <summary>Macros and Guided tasks are used often enough to have their own keys as well as their
        /// panel letters, so the map, the overlay and the menus show Ctrl+Shift+M and Ctrl+Shift+X.</summary>
        [Fact]
        public void MacrosAndGuidedTasksHaveDirectKeys()
        {
            var map = ShortcutTable.BuildMap();
            Assert.Equal("Str_DocumentTools_Macros", map[KbLayer.CtrlShift]["M"].Label);
            Assert.Equal("Str_Guided_Title", map[KbLayer.CtrlShift]["X"].Label);
            Assert.Equal("Str_DocumentTools_Macros", ShortcutTable.DocumentToolForDirectKey("M"));
            Assert.Equal("Str_Guided_Title", ShortcutTable.DocumentToolForDirectKey("X"));
            Assert.Null(ShortcutTable.DocumentToolForDirectKey("P"));
            var macros = Assert.Single(ShortcutTable.KsAll, b => b.LabelKey == "Str_DocumentTools_Macros");
            Assert.Equal("%ctrl%+%shift%+M / %ctrl%+%shift%+D, M", macros.Keys);
            // The panel letter still works, so both keys are the same tool.
            Assert.Equal('M', ShortcutTable.DocumentToolKey("Str_DocumentTools_Macros"));
            Assert.Equal('X', ShortcutTable.DocumentToolKey("Str_Guided_Title"));
        }

        /// <summary>Every tool in the Document tools panel takes its letter from the table, so the
        /// gesture text shown in the menu, the letter handler and the overlay agree.</summary>
        [Fact]
        public void EveryDocumentToolMenuItemUsesItsTableLetter()
        {
            string source = File.ReadAllText(Path.Combine(RepoRoot(), "Shell", "DocumentTools.cs"));
            foreach (var (_, labelKey) in ShortcutTable.DocumentToolKeys)
                Assert.Contains($"DocumentToolItem(\"{labelKey}\"", source, StringComparison.Ordinal);
        }

        /// <summary>Gesture-only rows (Ctrl+Scroll, Shift+Click, Middle drag) have
        /// no keycap by design, and so do the document tool letters, which are the second key of
        /// the Ctrl+Shift+D chord rather than a modifier layer. Anything else with no caps is likely
        /// an oversight, so the count is pinned rather than left to drift.</summary>
        [Fact]
        public void OnlyMouseGesturesHaveNoCaps()
        {
            // Asserted by label key, not by the Keys text: the key column is tokenized, so pinning
            // its literal spelling here would just break every time a token is renamed.
            var documentTools = ShortcutTable.DocumentToolKeys.Select(t => t.LabelKey).ToHashSet();
            var capless = ShortcutTable.KsAll.Where(b => b.Caps.Length == 0)
                                             .Select(b => b.LabelKey)
                                             .Where(k => !documentTools.Contains(k)).ToList();

            Assert.Equal(
                new[] { "Str_KS_ZoomCursor", "Str_KS_PanView",
                        "Str_KS_AppSize", "Str_KS_MultiSelect", "Str_Replace_All" }
                    .OrderBy(s => s, StringComparer.Ordinal).ToArray(),
                capless.OrderBy(s => s, StringComparer.Ordinal).ToArray());
        }

        /// <summary>
        /// Every token in the key column must be one the renderer knows, or it prints raw to the
        /// user as "%shft%". The zoom pair is substituted from the keyboard layout rather than the
        /// locale (Services/KeyLayout.cs), so it is known-good without a resource key.
        /// </summary>
        [Fact]
        public void EveryKeyTokenIsResolvable()
        {
            var known = ShortcutTable.KeyTokens.Select(t => t.Token)
                                     .Concat(["%zin%", "%zout%"])
                                     .ToHashSet();

            var unknown = ShortcutTable.KsAll
                .SelectMany(b => Regex.Matches(b.Keys, "%[a-z]+%").Cast<Match>().Select(m => m.Value))
                .Distinct()
                .Where(t => !known.Contains(t))
                .ToList();

            Assert.True(unknown.Count == 0,
                "these tokens have no resource key and would render raw: " + string.Join(", ", unknown));
        }

        [Fact]
        public void EveryKeyTokenResourceExistsInEnglish()
        {
            var english = EnglishKeys();
            var missing = ShortcutTable.KeyTokens.Select(t => t.Key)
                                       .Where(k => !english.Contains(k)).ToList();

            Assert.True(missing.Count == 0,
                "key-name resources missing from en-US.xaml: " + string.Join(", ", missing));
        }

        /// <summary>The point of #230: no bare English key name is left sitting in the table. Only
        /// letters, digits, F-numbers, arrows and the chord punctuation may appear literally.</summary>
        [Fact]
        public void NoUntranslatedKeyNamesRemainInTheTable()
        {
            var offenders = ShortcutTable.KsAll
                .Where(b => Regex.IsMatch(Regex.Replace(b.Keys, "%[a-z]+%", ""),
                                          @"\b(Ctrl|Alt|Shift|Delete|Enter|Escape|Esc|Menu|Home|End|PgUp|PgDn|Tab|Scroll|Click|Wheel|Space|Middle|drag|or)\b"))
                .Select(b => b.Keys)
                .ToList();

            Assert.True(offenders.Count == 0,
                "these rows still carry hardcoded English key names: " + string.Join(" | ", offenders));
        }

        [Fact]
        public void ListHeadingsUseTheKeyboardMapCategoryBrushes()
        {
            string source = File.ReadAllText(Path.Combine(RepoRoot(), "Shell", "ShortcutsOverlay.cs"));

            Assert.Contains(
                "header.SetResourceReference(TextBlock.ForegroundProperty, \"KsCat\" + section.Cat);",
                source,
                StringComparison.Ordinal);
        }

        // Every consumer resolves "KsCat" + group with SetResourceReference, so a group without a
        // brush gets the default foreground on its header and a null brush on its key caps. The
        // themes declare seven brushes for nine groups; AppTheme aliases the rest at load time
        // (BF-S008: Tools and Ocr had no alias). The alias table is read off AppTheme.cs itself.
        [Fact]
        public void EveryKsGroupHasABrushInEveryTheme()
        {
            XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
            var aliases = Regex.Matches(File.ReadAllText(Path.Combine(RepoRoot(), "Services", "AppTheme.cs")),
                    @"if \(!d\.Contains\(""(KsCat\w+)""\) && d\.Contains\(""(KsCat\w+)""\)\) d\[""\1""\] = d\[""\2""\];")
                .ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value, StringComparer.Ordinal);

            string[] themes = Directory.GetFiles(Path.Combine(RepoRoot(), "Themes"), "*.xaml");
            Assert.Equal(14, themes.Length);
            var missing = new List<string>();
            foreach (string theme in themes)
            {
                var keys = XDocument.Load(theme).Descendants()
                    .Select(element => (string?)element.Attribute(x + "Key"))
                    .Where(key => key is not null)
                    .ToHashSet(StringComparer.Ordinal);
                foreach (var (cat, _, _) in ShortcutTable.KsGroups)
                {
                    string key = "KsCat" + cat;
                    bool resolves = keys.Contains(key)
                        || (aliases.TryGetValue(key, out string? fallback) && keys.Contains(fallback));
                    if (!resolves) missing.Add($"{Path.GetFileName(theme)}: {key}");
                }
            }
            Assert.True(missing.Count == 0, "shortcut groups without a brush: " + string.Join(" | ", missing));
        }
    }
}
