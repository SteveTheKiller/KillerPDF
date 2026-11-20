using System;
using System.Windows;
using Xunit;
using KillerPDF.Shell;

namespace KillerPDF.Tests
{
    public class SidebarSideTests
    {
        // The side enum is internal, so the cases name sides as strings.
        [Theory]
        [InlineData("Left", "Right")]
        [InlineData("Right", "Top")]
        [InlineData("Top", "Bottom")]
        [InlineData("Bottom", "Left")]
        public void NextStepsDownTheMenuAndWraps(string from, string to)
            => Assert.Equal(Enum.Parse<SidebarSide>(to), Enum.Parse<SidebarSide>(from).Next());

        [Theory]
        [InlineData("Left", "Left")]
        [InlineData("Right", "Right")]
        [InlineData("right", "Right")]
        [InlineData("Top", "Top")]
        [InlineData("Bottom", "Bottom")]
        [InlineData(null, "Left")]
        [InlineData("", "Left")]
        [InlineData("Middle", "Left")]
        [InlineData("7", "Left")]
        public void SavedSideReadsBackAndUnknownFallsBackToLeft(string? saved, string expected)
            => Assert.Equal(Enum.Parse<SidebarSide>(expected), SidebarSides.Parse(saved));

        [Fact]
        public void SavedNamesRoundTrip()
        {
            foreach (SidebarSide side in new[] { SidebarSide.Left, SidebarSide.Right, SidebarSide.Top, SidebarSide.Bottom })
                Assert.Equal(side, SidebarSides.Parse(side.ToString()));
        }

        [Fact]
        public void OnlyTopAndBottomAreStrips()
        {
            Assert.False(SidebarSide.Left.IsStrip());
            Assert.False(SidebarSide.Right.IsStrip());
            Assert.True(SidebarSide.Top.IsStrip());
            Assert.True(SidebarSide.Bottom.IsStrip());
            Assert.True(SidebarSide.Right.IsTrailing());
            Assert.True(SidebarSide.Bottom.IsTrailing());
            Assert.False(SidebarSide.Left.IsTrailing());
            Assert.False(SidebarSide.Top.IsTrailing());
        }

        [Fact]
        public void TransposeTurnsAVerticalRailGapIntoAHorizontalOneAndBack()
        {
            var below = new Thickness(1, 0, 0, 6);
            var turned = SidebarSides.Transpose(below);
            Assert.Equal(new Thickness(0, 1, 6, 0), turned);
            Assert.Equal(below, SidebarSides.Transpose(turned));
        }

        [Fact]
        public void EverySideHasAMenuString()
        {
            foreach (SidebarSide side in new[] { SidebarSide.Left, SidebarSide.Right, SidebarSide.Top, SidebarSide.Bottom })
                Assert.Equal("Str_Sidebar_" + side, side.StringKey());
        }
    }
}
