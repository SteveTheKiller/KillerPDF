using Xunit;

namespace KillerPDF.Tests;

public sealed class FormFieldPresetsTests
{
    private static (FormFieldPresets.Store Store, Dictionary<string, string> Backing) NewStore()
    {
        var backing = new Dictionary<string, string>();
        var store = new FormFieldPresets.Store(
            name => backing.TryGetValue(name, out string? value) ? value : null,
            (name, value) => backing[name] = value,
            name => backing.Remove(name));
        return (store, backing);
    }

    private static FieldPreset Sample(string name = "answer_001") =>
        new(name, 200, 40, 12, "#000000", "#FFFFFF", "#FF0000", 1);

    [Fact]
    public void Load_EmptyStore_ReturnsEmpty()
    {
        var (store, _) = NewStore();
        Assert.Empty(FormFieldPresets.Load(store));
    }

    [Fact]
    public void Save_Load_RoundTrips()
    {
        var (store, _) = NewStore();
        FormFieldPresets.Save(store, [Sample(), Sample("answer_002") with { WidthPt = 300 }]);
        var loaded = FormFieldPresets.Load(store);
        Assert.Equal(2, loaded.Count);
        Assert.Equal("answer_001", loaded[0].Name);
        Assert.Equal(300, loaded[1].WidthPt);
        Assert.Equal("#FF0000", loaded[1].BorderHex);
    }

    [Fact]
    public void Load_CorruptJson_ReturnsEmpty()
    {
        var (store, backing) = NewStore();
        backing["FormFieldPresets"] = "{not json";
        Assert.Empty(FormFieldPresets.Load(store));
    }

    [Fact]
    public void Load_SkipsInvalidEntries()
    {
        var (store, _) = NewStore();
        FormFieldPresets.Save(store, [Sample(), Sample("bad") with { WidthPt = -5 }]);
        var loaded = FormFieldPresets.Load(store);
        Assert.Single(loaded);
    }

    [Fact]
    public void Save_Empty_RemovesSetting()
    {
        var (store, backing) = NewStore();
        FormFieldPresets.Save(store, [Sample()]);
        Assert.True(backing.ContainsKey("FormFieldPresets"));
        FormFieldPresets.Save(store, []);
        Assert.False(backing.ContainsKey("FormFieldPresets"));
    }

    [Fact]
    public void UniqueName_Deduplicates()
    {
        var presets = new List<FieldPreset> { Sample("answer_001") };
        Assert.Equal("answer_001 (2)", FormFieldPresets.UniqueName(presets, "answer_001"));
        Assert.Equal("fresh", FormFieldPresets.UniqueName(presets, "fresh"));
    }

    [Fact]
    public void Move_Reorders()
    {
        var presets = new List<FieldPreset> { Sample("a"), Sample("b"), Sample("c") };
        FormFieldPresets.Move(presets, 0, 2);
        Assert.Equal(["b", "c", "a"], presets.Select(p => p.Name).ToArray());
        FormFieldPresets.Move(presets, 5, 0);   // out of range: no-op
        Assert.Equal(["b", "c", "a"], presets.Select(p => p.Name).ToArray());
    }

    [Fact]
    public void Rename_WorksAndRejectsBadNames()
    {
        var presets = new List<FieldPreset> { Sample("a"), Sample("b") };
        Assert.True(FormFieldPresets.Rename(presets, "a", "renamed"));
        Assert.Equal("renamed", presets[0].Name);
        Assert.False(FormFieldPresets.Rename(presets, "renamed", "b"));   // duplicate
        Assert.False(FormFieldPresets.Rename(presets, "renamed", "   ")); // blank
        Assert.False(FormFieldPresets.Rename(presets, "missing", "x"));
    }

    [Fact]
    public void Remove_DeletesByName()
    {
        var presets = new List<FieldPreset> { Sample("a"), Sample("b") };
        Assert.True(FormFieldPresets.Remove(presets, "a"));
        Assert.Single(presets);
        Assert.False(FormFieldPresets.Remove(presets, "a"));
    }
}
