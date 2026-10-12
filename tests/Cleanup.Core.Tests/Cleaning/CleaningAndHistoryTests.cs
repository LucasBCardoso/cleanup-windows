using Cleanup.Core.Cleaning;
using Cleanup.Core.History;
using Cleanup.Core.Models;

namespace Cleanup.Core.Tests.Cleaning;

public class CleaningAndHistoryTests : IDisposable
{
    private readonly TempHome home = new();
    public void Dispose() => home.Dispose();

    [Fact]
    public void SafetyIsCheckedBeforeDedupAndBlockedItemsAreReported()
    {
        var remover = new FakeRemover();
        var temp = home.Dir(@"AppData\Local\Temp");
        var child = home.File(@"AppData\Local\Temp\a\b.txt");
        var result = new Cleaner(home.Policy(), remover).Clean([Items.At(temp), Items.At(child)]);
        // The container is blocked, and must not hide its (allowed) child.
        Assert.Equal([child], remover.Recycled);
        Assert.Contains(result.Failed, f => f.Item.Path == temp && f.Reason.Contains("política"));
    }

    [Fact]
    public void NestedItemsAreRemovedOnce()
    {
        var remover = new FakeRemover();
        var parent = home.Dir(@"AppData\Local\Temp\a");
        var child = home.File(@"AppData\Local\Temp\a\b.txt");
        new Cleaner(home.Policy(), remover).Clean([Items.At(child), Items.At(parent)]);
        Assert.Equal([parent], remover.Recycled);
    }

    [Fact]
    public void DryRunTouchesNothingAndAdminItemsNeverGoThroughTheUserCleaner()
    {
        var remover = new FakeRemover();
        var a = home.File(@"AppData\Local\Temp\a.tmp");
        var dry = new Cleaner(home.Policy(), remover, RemovalMode.DryRun).Clean([Items.At(a)]);
        Assert.Single(dry.Removed);
        Assert.Empty(remover.Recycled);

        var admin = Path.Combine(home.Windows, @"Temp\x.tmp");
        var result = new Cleaner(home.Policy(), remover).Clean([Items.At(admin, admin: true)]);
        Assert.Empty(remover.Recycled);
        Assert.Contains("administrador", Assert.Single(result.Failed).Reason);
    }

    [Fact]
    public void LockedItemsAreReportedAndRecycleBinItemEmptiesIt()
    {
        var remover = new FakeRemover();
        var a = home.File(@"AppData\Local\Temp\a.tmp");
        remover.Locked.Add(a);
        var bin = Items.At("shell:RecycleBinFolder", 500) with { Kind = ItemKind.RecycleBin };
        var result = new Cleaner(home.Policy(), remover).Clean([Items.At(a), bin]);
        Assert.True(remover.Emptied);
        Assert.Equal("em uso por outro programa", Assert.Single(result.Failed).Reason);
        Assert.Equal(500, result.FreedBytes);
    }

    [Fact]
    public void RecordKeepsRecycleBinLocations()
    {
        var remover = new FakeRemover();
        var a = home.File(@"AppData\Local\Temp\a.tmp", 7);
        var result = new Cleaner(home.Policy(), remover).Clean([Items.At(a, 7)]);
        var record = CleanupRecord.From(result, null, "Smart Scan", _ => "id-1")!;
        var entry = Assert.Single(record.Entries);
        Assert.Equal(a, entry.OriginalPath);
        Assert.StartsWith(@"C:\$Recycle.Bin", entry.TrashPath);
        Assert.Equal("id-1", entry.FileId);
        Assert.Null(CleanupRecord.From(result with { Mode = RemovalMode.DryRun }, null, "x", _ => null));
    }

    private static CleanupRecord Record(DateTimeOffset date, params CleanupRecord.Entry[] entries) =>
        new() { Date = date, Source = "test", Entries = entries };

    private static CleanupRecord.Entry Entry(string original, string? trash) =>
        new() { OriginalPath = original, TrashPath = trash, Size = 1, FileId = trash is null ? null : "same" };

    [Fact]
    public void HistoryIsNewestFirstCappedAndRoundTrips()
    {
        var history = new CleanupHistory();
        for (var i = 0; i < 35; i++) history = history.Adding(Record(DateTimeOffset.Now.AddMinutes(i), Entry($@"C:\x\{i}", null)));
        Assert.Equal(CleanupHistory.Limit, history.Records.Count);
        Assert.Equal(@"C:\x\34", history.Records[0].Entries[0].OriginalPath);

        var path = Path.Combine(home.Root, "history.json");
        history.Save(path);
        var loaded = CleanupHistory.Load(path);
        Assert.Equal(history.Records.Select(r => r.Id), loaded.Records.Select(r => r.Id));
        Assert.Contains("\"fileID\"", File.ReadAllText(path)); // same keys as the macOS file
    }

    [Fact]
    public void ReusedRecycleBinLocationsStopBeingRestorable()
    {
        var old = Record(DateTimeOffset.Now.AddHours(-1), Entry(@"C:\a", @"C:\$Recycle.Bin\S\$R1"), Entry(@"C:\b", @"C:\$Recycle.Bin\S\$R2"));
        var history = new CleanupHistory().Adding(old).Adding(Record(DateTimeOffset.Now, Entry(@"C:\c", @"C:\$Recycle.Bin\S\$R1")));
        var updated = history.Records.Single(r => r.Id == old.Id);
        Assert.Null(updated.Entries[0].TrashPath);
        Assert.NotNull(updated.Entries[1].TrashPath);
    }

    [Fact]
    public void CorruptHistoryIsMovedAsideByUpdate()
    {
        var path = Path.Combine(home.Root, "cleanup-history.json");
        File.WriteAllText(path, "{ not json");
        Assert.Throws<CleanupHistoryException>(() => CleanupHistory.Load(path));
        var outcome = CleanupHistory.Update(h => h.Adding(Record(DateTimeOffset.Now, Entry(@"C:\a", null))), path);
        Assert.NotNull(outcome.MovedAside);
        Assert.True(File.Exists(outcome.MovedAside));
        Assert.Single(CleanupHistory.Load(path).Records);
    }

    private Restorer.Context Context(string binFolder, Dictionary<string, string?>? ids = null) => new(
        home.Policy(),
        folder => string.Equals(folder, binFolder, StringComparison.OrdinalIgnoreCase),
        path => ids is not null && ids.TryGetValue(path, out var id) ? id : File.Exists(path) || Directory.Exists(path) ? "same" : null,
        (from, to) => File.Move(from, to, overwrite: false));

    [Fact]
    public void RestorePutsItemsBackWithoutOverwriting()
    {
        var bin = home.Dir("bin");
        var original = Path.Combine(home.Home, @"AppData\Local\Temp\a.txt");
        var trashed = home.File(@"bin\$R1.txt");
        var record = Record(DateTimeOffset.Now, Entry(original, trashed) with { FileId = "same" }, Entry(@"C:\perm", null));

        var availability = Restorer.AvailabilityOf(record, Context(bin));
        Assert.Equal((1, 1), (availability.RestorableCount, availability.PermanentCount));

        var result = Restorer.Restore(record, Context(bin));
        Assert.Single(result.Restored);
        Assert.True(File.Exists(original));
        Assert.Single(result.Remaining!.Entries); // the permanent one stays listed
    }

    [Fact]
    public void RestoreRefusesAnEntryWithoutIdentity()
    {
        var bin = home.Dir("bin");
        var trashed = home.File(@"bin\$R9.txt");
        var entry = Entry(Path.Combine(home.Home, @"AppData\Local\Temp\n.txt"), trashed) with { FileId = null };
        Assert.Equal("sem identidade do item na Lixeira", Restorer.Restore(Record(DateTimeOffset.Now, entry), Context(bin)).Failed.Single().Reason);
        Assert.True(File.Exists(trashed));
    }

    [Fact]
    public void restorerefusesuntrustedentries()
    {
        var bin = home.Dir("bin");
        var trashed = home.File(@"bin\$R1.txt");
        var occupied = home.File(@"AppData\Local\Temp\taken.txt");
        string[] reasons =
        [
            Restorer.Restore(Record(DateTimeOffset.Now, Entry(occupied, trashed)), Context(bin)).Failed.Single().Reason,
            Restorer.Restore(Record(DateTimeOffset.Now, Entry(Path.Combine(home.Home, ".ssh", "id"), trashed)), Context(bin)).Failed.Single().Reason,
            Restorer.Restore(Record(DateTimeOffset.Now, Entry(Path.Combine(home.Home, @"AppData\Local\Temp\x"), home.File(@"elsewhere\$R2"))), Context(bin)).Failed.Single().Reason,
            Restorer.Restore(Record(DateTimeOffset.Now, Entry(Path.Combine(home.Home, @"AppData\Local\Temp\y"), trashed) with { FileId = "old" }), Context(bin)).Failed.Single().Reason,
            Restorer.Restore(Record(DateTimeOffset.Now, Entry(Path.Combine(home.Home, @"AppData\..\AppData\Local\Temp\z"), trashed)), Context(bin)).Failed.Single().Reason,
        ];
        Assert.Equal(
        [
            "já existe um item com esse nome no lugar original", "o lugar original não é permitido", "não é um item da Lixeira",
            "o item na Lixeira mudou", "o lugar original não é permitido",
        ], reasons);
        Assert.True(File.Exists(trashed));
    }
}
