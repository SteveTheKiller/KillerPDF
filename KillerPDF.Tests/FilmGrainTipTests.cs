using System.Collections.Generic;
using KillerPDF.Controls;
using Xunit;

namespace KillerPDF.Tests;

/// <summary>#506: the Film Grain tooltip names its chord with the active language's key names.</summary>
public sealed class FilmGrainTipTests
{
    [Fact]
    public void GermanTipSaysUmschalt()
    {
        var de = new Dictionary<string, string>
        {
            ["Str_Theme_FilmGrain"] = "Filmkorn",
            ["Str_Key_Alt"] = "Alt",
            ["Str_Key_Shift"] = "Umschalt",
        };
        Assert.Equal("Filmkorn (Alt+Umschalt+G)", FilmGrainTip.Text(k => de.GetValueOrDefault(k)));
    }

    [Fact]
    public void MissingKeysFallBackToEnglish()
        => Assert.Equal("Film Grain (Alt+Shift+G)", FilmGrainTip.Text(_ => null));
}
