using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using KillerPDF.Features;

namespace KillerPDF.Shell
{
    // Object cards for things the viewer owns rather than the markup list, such as a form field: the same layout as the
    // markup and picture cards, so every right-click on an object opens with the nav row, a header with Delete at its
    // end, and tile rows, most used first (KillerUI card menu).
    public partial class MainWindow
    {
        private void ShowObjectCard(ViewerObjectCard card, UIElement target)
        {
            var menu = MakeThemedMenu();
            if (TryFindResource("CardMenu") is Style style) menu.Style = style;
            AddNavRow(menu, card.PageIndex);
            FrameworkElement header = ObjectCardHeader(card.Title, card.Meta, null);
            if (card.Delete is { } delete)
                header = WithHeaderAction(header, NavRowButton(menu, "\uE74D", "Str_Ctx_DeleteSel", "Del", delete, true));
            menu.Items.Add(CardSlot(header, new Thickness(4, 2, 4, 6)));
            IEnumerable<CardTile>[] groups = [.. card.Rows.Select(row => row.Select(ToCardTile))];
            // A customizable card locks nothing, so every tile can be hidden; its spare tiles start in the Hidden drawer.
            var strip = card.CardKey is { } key ? EditableTileGroups(menu, key, card.LockedGlyphs, [], groups) : TileGroups(menu, groups);
            menu.Items.Add(CardSlot(strip, new Thickness(0)));
            FocusFirstActionFromKeyboard(menu, strip);
            menu.PlacementTarget = target;
            menu.Placement = PlacementMode.MousePoint;
            menu.IsOpen = true;
        }

        private CardTile ToCardTile(ViewerCardTile t)
        {
            CardTile tile = t.Flyout is { } rows
                ? FlyoutTile(t.Glyph, t.Caption, t.Gesture, rows) with { Enabled = t.Enabled && rows.IsEnabled }
                : new CardTile(t.Glyph, t.Caption, t.Gesture, t.Act, false, t.Enabled) { Checked = t.Checked };
            return t.SpareId is { } id ? Spare(id, tile) : tile;
        }
    }
}
