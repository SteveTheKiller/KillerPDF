using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace KillerPDF.Tests;

// No button casts a shadow, lift, glow or blurred copy: not under the face, not behind the caption,
// not behind an icon, at rest, on hover or when pressed. The XAML styles are walked as XML (setters,
// control template tree and triggers) and the buttons built in code are pinned by their source,
// because neither can be instantiated without a window.
public sealed class NoButtonShadowTests
{
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static readonly string[] ButtonTypes =
        ["Button", "ToggleButton", "RadioButton", "CheckBox", "RepeatButton"];

    private static readonly string[] ShadowResourceMarkers =
        ["TextStroke", "IconShadowOpacity", "ThemeRadioShadowOpacity", "ComparisonBarTextEffect", "ShadowIcon", "ShadowText"];

    [Theory]
    [InlineData("Shell/MainWindow.xaml")]
    [InlineData("Controls/Controls.xaml")]
    public void NoButtonStyleCastsAShadow(string file)
    {
        var document = XDocument.Parse(ShellSource.Read(file.Split('/')));
        var offenders = new List<string>();
        foreach (XElement style in document.Descendants().Where(IsButtonStyle))
        {
            string key = (string?)style.Attribute(X + "Key") ?? "(implicit " + (string?)style.Attribute("TargetType") + ")";
            CollectShadows(style, key, offenders);
        }
        foreach (XElement button in document.Descendants().Where(element => ButtonTypes.Contains(element.Name.LocalName)))
        {
            string key = (string?)button.Attribute(X + "Name") ?? (string?)button.Attribute("Name") ?? "(unnamed " + button.Name.LocalName + ")";
            CollectShadows(button, key + " (inline)", offenders);
        }
        Assert.True(offenders.Count == 0,
            file + " has button styles that cast a shadow:" + Environment.NewLine +
            string.Join(Environment.NewLine, offenders.Distinct()));
    }

    [Fact]
    public void ComparisonBarShadowsOnlyItsPlainLabels()
    {
        var document = XDocument.Parse(ShellSource.Read("Shell", "MainWindow.xaml"));
        XElement bar = document.Descendants().Single(element => (string?)element.Attribute(X + "Name") == "ComparisonBar");

        XElement implicitText = bar.Descendants()
            .Single(element => element.Name.LocalName == "Style" && (string?)element.Attribute("TargetType") == "TextBlock");
        Assert.DoesNotContain(implicitText.Elements(), element =>
            element.Name.LocalName == "Setter" && (string?)element.Attribute("Property") == "Effect");

        foreach (XElement button in bar.Descendants().Where(element => element.Name.LocalName == "Button"))
        {
            Assert.DoesNotContain(button.Descendants(), element =>
                element.Attribute("Effect") is { } effect && !IsNullValue(effect.Value));
        }

        foreach (string name in new[] { "ComparisonFilesText", "ComparisonRegionText" })
        {
            XElement label = bar.Descendants().Single(element => (string?)element.Attribute(X + "Name") == name);
            Assert.Equal("{DynamicResource ComparisonBarTextEffect}", (string?)label.Attribute("Effect"));
        }
    }

    [Fact]
    public void ButtonsBuiltInCodeCastNoShadow()
    {
        string signing = ShellSource.Read("Shell", "Signing.cs");
        Assert.DoesNotContain("closeText.Effect", signing, StringComparison.Ordinal);

        string files = ShellSource.Read("Shell", "FileOperations.cs");
        Assert.DoesNotContain("delIcon.Effect", files, StringComparison.Ordinal);
        Assert.DoesNotMatch(new Regex(@"\bicon\.Effect\b"), files);
    }

    private static void CollectShadows(XElement root, string key, List<string> offenders)
    {
        foreach (XElement element in root.Descendants())
        {
            string name = element.Name.LocalName;
            if (name is "DropShadowEffect" or "BlurEffect")
                offenders.Add(key + ": " + name);
            else if (name == "Setter" && IsEffectProperty((string?)element.Attribute("Property")) &&
                     !IsNullValue((string?)element.Attribute("Value")) &&
                     (element.Attribute("Value") is not null || element.HasElements))
                offenders.Add(key + ": Effect setter on " + ((string?)element.Attribute("TargetName") ?? "the button"));

            foreach (XAttribute attribute in element.Attributes())
            {
                if (attribute.Name.LocalName == "Effect" && !IsNullValue(attribute.Value))
                    offenders.Add(key + ": Effect attribute " + attribute.Value);
                else if (ShadowResourceMarkers.Any(marker => attribute.Value.Contains(marker, StringComparison.Ordinal)))
                    offenders.Add(key + ": uses " + attribute.Value);
            }
        }
    }

    private static bool IsButtonStyle(XElement element)
    {
        if (element.Name.LocalName != "Style") return false;
        string target = ((string?)element.Attribute("TargetType") ?? "").Trim('{', '}');
        target = target.Replace("x:Type", "").Trim().Split(':').Last();
        return ButtonTypes.Contains(target);
    }

    private static bool IsEffectProperty(string? property) =>
        property is not null && (property == "Effect" || property.EndsWith(".Effect", StringComparison.Ordinal));

    private static bool IsNullValue(string? value) => value?.Trim() == "{x:Null}";
}
