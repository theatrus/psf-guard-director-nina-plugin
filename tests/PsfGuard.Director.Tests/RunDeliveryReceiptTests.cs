using PsfGuard.Director.Runtime;
using Xunit;
using static PsfGuard.Director.Tests.CoordinatorProgramTests;

namespace PsfGuard.Director.Tests;

public sealed class RunDeliveryReceiptTests : IDisposable
{
    private readonly DirectoryInfo root = Directory.CreateTempSubdirectory("director-report-receipt-");
    private readonly Guid client = Guid.NewGuid();
    private readonly string archive;
    private readonly CoordinatorRunDeliveryReceipt receipt;

    public RunDeliveryReceiptTests()
    {
        archive = new CoordinatorRunArchive(root.FullName, Endpoint, Binding, client).StatePath;
        Directory.CreateDirectory(Path.Combine(root.FullName, "ledger"));
        File.WriteAllText(archive, "original archive");
        File.WriteAllBytes(Path.Combine(root.FullName, "ledger", "execution.sqlite"), new byte[200]);
        receipt = new(root.FullName, Endpoint, Binding, client, archive, false);
    }

    [Theory]
    [InlineData("ledger/execution.sqlite")]
    [InlineData("ledger/execution.sqlite-wal")]
    [InlineData("ledger/execution.sqlite-journal")]
    [InlineData("capture-cursor-test.json")]
    [InlineData("preparation-cursor-test.json")]
    [InlineData("archive")]
    public async Task ChangedContentsInvalidateReceiptEvenWithSameSizeAndFileDate(string name)
    {
        var path = name == "archive" ? archive : Path.Combine(root.FullName, name);
        File.WriteAllBytes(path, new byte[200]);
        await receipt.StoreAsync(6, 10, default);
        var delivered = Assert.IsType<CoordinatorRunDeliveryReceipt.Receipt>(await receipt.ReadCurrentAsync(default));
        Assert.Equal(6UL, delivered.Captures);
        Assert.Equal(10UL, delivered.Operations);
        var modified = File.GetLastWriteTimeUtc(path);
        var bytes = File.ReadAllBytes(path);
        bytes[^1] = 1;
        File.WriteAllBytes(path, bytes);
        File.SetLastWriteTimeUtc(path, modified);
        Assert.Null(await receipt.ReadCurrentAsync(default));
    }

    [Theory]
    [InlineData("capture-cursor-test.json")]
    [InlineData("preparation-cursor-test.json")]
    [InlineData("ledger/execution.sqlite-wal")]
    public async Task AddedAndRemovedInputsRequireAnotherCheck(string name)
    {
        await receipt.StoreAsync(6, 10, default);
        var path = Path.Combine(root.FullName, name);
        File.WriteAllText(path, "new records");
        Assert.Null(await receipt.ReadCurrentAsync(default));
        await receipt.StoreAsync(8, 12, default);
        File.Delete(path);
        Assert.Null(await receipt.ReadCurrentAsync(default));
    }

    [Fact]
    public async Task ReceiptCannotBeReusedAfterPairingChanges()
    {
        await receipt.StoreAsync(6, 10, default);
        var other = new CoordinatorRunDeliveryReceipt(root.FullName, Endpoint, Binding, Guid.NewGuid(), archive, false);
        Assert.Null(await other.ReadCurrentAsync(default));
        var anotherRig = new CoordinatorRunDeliveryReceipt(root.FullName, Endpoint, Binding with { RigId = Guid.NewGuid() }, client, archive, false);
        Assert.Null(await anotherRig.ReadCurrentAsync(default));
    }

    [Fact]
    public async Task CancelledPassDoesNotWriteReceipt()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => receipt.StoreAsync(6, 10, cancellation.Token));
        Assert.Empty(Directory.GetFiles(root.FullName, "reported-run-*.json"));
    }

    public void Dispose() => root.Delete(true);
}
