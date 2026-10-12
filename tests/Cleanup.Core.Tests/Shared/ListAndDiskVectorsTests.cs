using Cleanup.Core.Lists;
using Cleanup.Core.SpaceLens;

namespace Cleanup.Core.Tests.Shared;

/// <summary>The search, selection and disk-space vectors, run against the real .NET code (W1).</summary>
public class ListAndDiskVectorsTests
{
    private sealed record SearchVectors(List<SearchVectors.Case> Cases)
    {
        public sealed record Case(string Query, List<string> Fields, bool Expected);
    }

    private sealed record SelectionVectors(List<SelectionVectors.StateCase> State, List<SelectionVectors.ToggleCase> Toggle,
        List<SelectionVectors.BarCase> Bar)
    {
        public sealed record StateCase(List<string> Selected, List<string> Ids, string Expected);
        public sealed record ToggleCase(List<string> Selected, List<string> Ids, List<string> Expected);
        public sealed record BarCase(List<string> Selected, List<string> Ids, int HiddenSelected, int SelectedCount, int Total,
            string State, string CountLabel, string? HiddenLabel);
    }

    private sealed record DiskVectors(List<DiskVectors.Case> Cases)
    {
        public sealed record Case(long Total, long Available, long ImportantAvailable, long Free, long Purgeable, long Used);
    }

    [Fact]
    public void SearchFilter()
    {
        var vectors = SharedFiles.Load<SearchVectors>("test-vectors/search-filter.json");
        Assert.All(vectors.Cases, c =>
            Assert.True(Lists.SearchFilter.Matches(c.Query, c.Fields) == c.Expected, $"'{c.Query}' in [{string.Join(", ", c.Fields)}]"));
    }

    [Fact]
    public void SelectionStateToggleAndBar()
    {
        var vectors = SharedFiles.Load<SelectionVectors>("test-vectors/selection.json");
        Assert.All(vectors.State, c =>
            Assert.Equal(c.Expected, Selection.State(c.Selected.ToHashSet(), c.Ids).ToString().ToLowerInvariant()));
        Assert.All(vectors.Toggle, c =>
            Assert.True(Selection.Toggled(c.Selected.ToHashSet(), c.Ids).SetEquals(c.Expected), string.Join(",", c.Selected)));
        Assert.All(vectors.Bar, c =>
        {
            var selected = c.Selected.ToHashSet();
            var count = c.Ids.Count(selected.Contains);
            Assert.Equal(c.SelectedCount, count);
            Assert.Equal(c.Total, c.Ids.Count);
            Assert.Equal(c.State, Selection.State(selected, c.Ids).ToString().ToLowerInvariant());
            Assert.Equal(c.CountLabel, Selection.CountLabel(count, c.Ids.Count));
            Assert.Equal(c.HiddenLabel, Selection.HiddenLabel(c.HiddenSelected));
        });
    }

    [Fact]
    public void DiskSpaceBreakdown()
    {
        var vectors = SharedFiles.Load<DiskVectors>("test-vectors/disk-space.json");
        Assert.All(vectors.Cases, c =>
        {
            var disk = new DiskSpace(c.Total, c.Available, c.ImportantAvailable);
            Assert.Equal((c.Free, c.Purgeable, c.Used), (disk.Free, disk.Purgeable, disk.Used));
        });
    }
}
