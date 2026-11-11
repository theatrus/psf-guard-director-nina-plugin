using System.Text.Json;
using System.Collections.Immutable;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using Moq;
using NINA.Profile.Interfaces;
using PsfGuard.Director.Plugin;
using PsfGuard.Director.Plugin.Sequencer;
using PsfGuard.Director.Runtime;
using Xunit;
using static PsfGuard.Director.Tests.CoordinatorProgramTests;

namespace PsfGuard.Director.Tests;

[CollectionDefinition("Saved run delivery", DisableParallelization = true)]
public sealed class SavedRunDeliveryCollection;

[Collection("Saved run delivery")]
public sealed class CoordinatorRunArchiveTests
{
    private const ulong Now = 1790409600000;
    private sealed class Fixture : IDisposable
    {
        internal readonly DirectoryInfo Root = Directory.CreateTempSubdirectory("director-saved-run-");
        internal readonly Guid Client = Guid.NewGuid();
        internal string RunRoot { get; }
        internal CoordinatorSavedRun Run { get; }
        internal CoordinatorRunArchive Archive { get; }
        internal Fixture()
        {
            RunRoot = Directory.CreateDirectory(Path.Combine(Root.FullName, Binding.ProfileId.ToString("N"), Guid.NewGuid().ToString("N"))).FullName;
            var snapshot = Envelope();
            var id = Guid.NewGuid();
            snapshot = snapshot with { Program = snapshot.Program with { Assignment = snapshot.Program.Assignment with { Id = $"allocation-{id:D}" } } };
            var allocation = new CoordinatorAllocationEnvelope(1, id, Binding.CoordinatorInstanceId, Binding.CatalogId, Binding.RigId, Client,
                Binding.ProfileId, snapshot.Revision, Now - 500, snapshot);
            var ledger = new LedgerIdentity(Guid.NewGuid().ToString("D"), snapshot.Program.Assignment.Id, 1, Binding.RigId.ToString("D"), "config");
            var constraints = new DirectorConstraints(1, new(ledger.RigId, "config", 1, new(35, -120, 100), new(0, 0, 0, Now - 1000, Now + 120000),
                new DirectorHorizon.FixedMinimum(), -89, 89, new(0, 0)), snapshot.Program.Assignment.Goals.Select(g => new DirectorGoalLimits(g.Id, -89, 89, 0)).ToImmutableArray());
            Run = new(allocation, constraints, new(ledger.RigId, "config", Now, Now + 60000, PlannerSafety.Safe, true, false, new(0, 0)), ledger, true, true);
            Archive = new(RunRoot, Endpoint, Binding, Client);
        }
        public void Dispose() => Root.Delete(true);

        internal async Task<CoordinatorSavedRun> StoreLedgerAsync(bool automatic = false)
        {
            var run = Run with { AutomaticWorkload = automatic };
            await using (var runtime = new RuntimeController(ProcessTests.BundleDirectory, Directory.CreateDirectory(Path.Combine(RunRoot, "ledger")).FullName))
            {
                await runtime.StartAsync(Binding.RigId.ToString("D"));
                run = run with { Ledger = (await runtime.OpenGeometryAsync(run.Allocation.Snapshot.Program, run.Constraints, run.InitialState)).Value! };
                Assert.NotNull(run.Ledger);
            }
            Archive.Store(run);
            return run;
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task UnchangedReportedRunsDoNotStartAnotherRuntime(bool automatic, bool completed)
    {
        using var f = new Fixture();
        await f.StoreLedgerAsync(automatic);
        if (completed)
        {
            f.Archive.MarkCompleted();
            if (automatic) f.Archive.MarkReleased();
        }
        var first = await CoordinatorRunCheckIn.DeliverAsync(f.Root.FullName, ProcessTests.BundleDirectory,
            Endpoint, Binding, f.Client, _ => throw new Exception("No pending events or release; no network expected"));
        Assert.Equal(1, first.Runs);
        Assert.Equal(0, first.SkippedRuns);
        var again = await CoordinatorRunCheckIn.DeliverAsync(f.Root.FullName, "no runtime installed here",
            Endpoint, Binding, f.Client, _ => throw new Exception("A reported run must not call the server"));
        Assert.True(again.CaughtUp);
        Assert.Equal(0, again.Runs);
        Assert.Equal(1, again.SkippedRuns);
        Assert.Equal(0, again.DeliveredEvents);
        Assert.Equal(first.AcknowledgedThrough, again.AcknowledgedThrough);
        Assert.True(File.Exists(Path.Combine(f.RunRoot, "ledger", "execution.sqlite")));
    }

    [Theory]
    [InlineData(14)]
    [InlineData(15)]
    public async Task RuntimeUpgradeDoesNotReopen120ReportedRuns(int previousProtocol)
    {
        using var f = new Fixture();
        await f.StoreLedgerAsync();
        await CoordinatorRunCheckIn.DeliverAsync(f.Root.FullName, ProcessTests.BundleDirectory,
            Endpoint, Binding, f.Client, _ => throw new Exception("No network expected"));
        var receiptPath = Directory.GetFiles(f.RunRoot, "reported-run-*.json").Single();
        var receipt = JsonNode.Parse(await File.ReadAllTextAsync(receiptPath))!;
        var fingerprint = receipt["fingerprint"]!.GetValue<string>();
        var captures = receipt["captures"]!.GetValue<ulong>();
        var operations = receipt["operations"]!.GetValue<ulong>();
        receipt["schema_version"] = 1;
        receipt["checksum"] = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            scope = Path.GetFileName(receiptPath),
            schemaVersion = 1,
            ProtocolVersion = previousProtocol,
            EngineVersion = "0.3.0",
            fingerprint,
            captures,
            operations
        }))).ToLowerInvariant();
        await File.WriteAllTextAsync(receiptPath, receipt.ToJsonString());
        for (var i = 1; i < 120; i++)
        {
            var copy = Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(f.RunRoot)!, Guid.NewGuid().ToString("N"))).FullName;
            foreach (var source in Directory.EnumerateFiles(f.RunRoot, "*", SearchOption.AllDirectories))
            {
                var target = Path.Combine(copy, Path.GetRelativePath(f.RunRoot, source));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(source, target);
            }
        }
        for (var pass = 0; pass < 2; pass++)
        {
            var result = await CoordinatorRunCheckIn.DeliverAsync(f.Root.FullName, "no runtime installed here",
                Endpoint, Binding, f.Client, _ => throw new Exception("Reported history must not contact the server"));
            Assert.Equal(0, result.Runs);
            Assert.Equal(120, result.SkippedRuns);
            Assert.Equal(0, result.DeliveredEvents);
        }
        Assert.Equal(2, JsonNode.Parse(await File.ReadAllTextAsync(receiptPath))!["schema_version"]!.GetValue<int>());
    }

    [Fact]
    public async Task ChangedLedgerInvalidatesReceiptEvenWhenItsFileDateIsRestored()
    {
        using var f = new Fixture();
        await f.StoreLedgerAsync();
        await CoordinatorRunCheckIn.DeliverAsync(f.Root.FullName, ProcessTests.BundleDirectory,
            Endpoint, Binding, f.Client, _ => throw new Exception("No network expected"));
        var receipt = new CoordinatorRunDeliveryReceipt(f.RunRoot, Endpoint, Binding, f.Client, f.Archive.StatePath, false);
        Assert.NotNull(await receipt.ReadCurrentAsync(default));
        var ledgerPath = Path.Combine(f.RunRoot, "ledger", "execution.sqlite");
        var originalDate = File.GetLastWriteTimeUtc(ledgerPath);
        var bytes = await File.ReadAllBytesAsync(ledgerPath);
        bytes[^1] ^= 1;
        await File.WriteAllBytesAsync(ledgerPath, bytes);
        File.SetLastWriteTimeUtc(ledgerPath, originalDate);
        Assert.Null(await receipt.ReadCurrentAsync(default));
    }

    [Fact]
    public async Task LaterCompletionRetriesOutstandingReleaseInsteadOfUsingAnOldReceipt()
    {
        using var f = new Fixture();
        await f.StoreLedgerAsync(automatic: true);
        await CoordinatorRunCheckIn.DeliverAsync(f.Root.FullName, ProcessTests.BundleDirectory,
            Endpoint, Binding, f.Client, _ => throw new Exception("Empty failed run needs no network"));
        f.Archive.MarkCompleted();
        for (var retry = 0; retry < 2; retry++)
        {
            var requested = false;
            await Assert.ThrowsAsync<CoordinatorIntakeException>(() => CoordinatorRunCheckIn.DeliverAsync(f.Root.FullName,
                ProcessTests.BundleDirectory, Endpoint, Binding, f.Client, _ => { requested = true; throw new Exception("Offline"); }));
            Assert.True(requested);
            Assert.False(f.Archive.Read()!.Released);
        }
    }

    [Fact]
    public async Task DamagedReceiptFallsBackToNormalDelivery()
    {
        using var f = new Fixture();
        await f.StoreLedgerAsync();
        await CoordinatorRunCheckIn.DeliverAsync(f.Root.FullName, ProcessTests.BundleDirectory,
            Endpoint, Binding, f.Client, _ => throw new Exception("No network expected"));
        File.WriteAllText(Directory.GetFiles(f.RunRoot, "reported-run-*.json").Single(), "not JSON");
        var retried = await CoordinatorRunCheckIn.DeliverAsync(f.Root.FullName, ProcessTests.BundleDirectory,
            Endpoint, Binding, f.Client, _ => throw new Exception("No network expected"));
        Assert.Equal(1, retried.Runs);
        Assert.Equal(0, retried.SkippedRuns);
    }

    [Fact]
    public async Task BacklogRechecksOnlyTheRunThatChanged()
    {
        using var f = new Fixture();
        var archives = new List<CoordinatorRunArchive>();
        for (var i = 0; i < 8; i++)
        {
            var runRoot = Directory.CreateDirectory(Path.Combine(f.Root.FullName, Binding.ProfileId.ToString("N"), Guid.NewGuid().ToString("N"))).FullName;
            var run = f.Run with { AutomaticWorkload = false };
            await using (var runtime = new RuntimeController(ProcessTests.BundleDirectory, Directory.CreateDirectory(Path.Combine(runRoot, "ledger")).FullName))
            {
                await runtime.StartAsync(Binding.RigId.ToString("D"));
                run = run with { Ledger = (await runtime.OpenGeometryAsync(run.Allocation.Snapshot.Program, run.Constraints, run.InitialState)).Value! };
            }
            var archive = new CoordinatorRunArchive(runRoot, Endpoint, Binding, f.Client);
            archive.Store(run);
            archives.Add(archive);
        }
        Task<CoordinatorRunCheckInProgress> Check(string bundle) => CoordinatorRunCheckIn.DeliverAsync(f.Root.FullName,
            bundle, Endpoint, Binding, f.Client, _ => throw new Exception("Empty runs must not call the server"));
        Assert.Equal(8, (await Check(ProcessTests.BundleDirectory)).Runs);
        var repeat = await Check("no runtime installed here");
        Assert.Equal(0, repeat.Runs);
        Assert.Equal(8, repeat.SkippedRuns);
        archives[3].MarkCompleted();
        var changed = await Check(ProcessTests.BundleDirectory);
        Assert.Equal(1, changed.Runs);
        Assert.Equal(7, changed.SkippedRuns);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ReportedReceiptCannotHideMissingOrTruncatedLedger(bool missing)
    {
        using var f = new Fixture();
        await f.StoreLedgerAsync();
        await CoordinatorRunCheckIn.DeliverAsync(f.Root.FullName, ProcessTests.BundleDirectory,
            Endpoint, Binding, f.Client, _ => throw new Exception("No network expected"));
        var ledger = Path.Combine(f.RunRoot, "ledger", "execution.sqlite");
        if (missing) File.Delete(ledger); else File.WriteAllBytes(ledger, []);
        await Assert.ThrowsAsync<InvalidDataException>(() => CoordinatorRunCheckIn.DeliverAsync(f.Root.FullName,
            "unused", Endpoint, Binding, f.Client, _ => throw new Exception("No network expected")));
    }

    [Fact]
    public void CheckInSummaryExplainsSkippedHistoryWithoutCursorJargon()
    {
        var result = new CoordinatorRunCheckInProgress(0, 0, 0, "", 6, true, 10, 200);
        Assert.Equal("All saved records are up to date.", DirectorCheckInService.DescribeFinished(result));
        Assert.Equal("Checking for unsent image records", DirectorCheckInService.DescribeProgress(result));
        Assert.Equal("Sending saved records: 6 updates sent", DirectorCheckInService.DescribeProgress(result with { DeliveredEvents = 6 }));
    }

    [Fact]
    public void SnapshotSurvivesRestartAndCompletionIsMonotonic()
    {
        using var f = new Fixture();
        f.Archive.Store(f.Run);
        var reopened = new CoordinatorRunArchive(f.RunRoot, Endpoint, Binding, f.Client);
        Assert.Throws<CoordinatorIntakeException>(() => CoordinatorAllocation.Read(CoordinatorAllocation.Wrap(f.Run.Allocation),
            Endpoint.AbsoluteUri, Binding, f.Client, Configuration, Now + 86400000));
        Assert.Equal(JsonSerializer.Serialize(f.Run), JsonSerializer.Serialize(reopened.Read()));
        Assert.Throws<InvalidDataException>(reopened.MarkReleased);
        reopened.MarkCompleted();
        reopened.MarkReleased();
        Assert.True(reopened.Read()!.Released);
        Assert.Throws<InvalidDataException>(() => reopened.Store(f.Run));
        Assert.DoesNotContain("token", File.ReadAllText(Directory.GetFiles(f.RunRoot, "checkin-run-*.json").Single()), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("profile")]
    [InlineData("rig")]
    [InlineData("catalog")]
    [InlineData("coordinator")]
    [InlineData("client")]
    [InlineData("origin")]
    public void PairingScopeCannotReadAnotherRunsRecord(string changed)
    {
        using var f = new Fixture();
        f.Archive.Store(f.Run);
        var binding = changed switch
        {
            "profile" => Binding with { ProfileId = Guid.NewGuid() },
            "rig" => Binding with { RigId = Guid.NewGuid() },
            "catalog" => Binding with { CatalogId = Guid.NewGuid() },
            "coordinator" => Binding with { CoordinatorInstanceId = Guid.NewGuid() },
            _ => Binding
        };
        var archive = new CoordinatorRunArchive(f.RunRoot, changed == "origin" ? new Uri("https://other.example/") : Endpoint,
            binding, changed == "client" ? Guid.NewGuid() : f.Client);
        Assert.Null(archive.Read());
    }

    [Theory]
    [InlineData("completed")]
    [InlineData("released")]
    [InlineData("unknown")]
    [InlineData("checksum")]
    public void TamperedTerminalEvidenceIsRejected(string field)
    {
        using var f = new Fixture();
        f.Archive.Store(f.Run);
        var file = Directory.GetFiles(f.RunRoot, "checkin-run-*.json").Single();
        var json = JsonNode.Parse(File.ReadAllText(file))!;
        if (field == "checksum") json["checksum"] = "changed";
        else json["run"]![field] = true;
        File.WriteAllText(file, json.ToJsonString());
        Assert.Throws<InvalidDataException>(() => f.Archive.Read());
    }

    [Fact]
    public async Task MissingLedgerIsNotRecreatedForReporting()
    {
        using var f = new Fixture();
        f.Archive.Store(f.Run);
        await Assert.ThrowsAsync<InvalidDataException>(() => CoordinatorRunCheckIn.DeliverAsync(f.Root.FullName, ProcessTests.BundleDirectory,
            Endpoint, Binding, f.Client, _ => throw new InvalidOperationException("No network expected")));
        Assert.False(Directory.Exists(Path.Combine(f.RunRoot, "ledger")));
        Assert.False(AcquisitionLease.IsActive);
    }

    [Fact]
    public async Task ActiveAcquisitionBlocksSavedRunDelivery()
    {
        using var f = new Fixture();
        using var lease = new AcquisitionLease(f.Root.FullName);
        await Assert.ThrowsAsync<InvalidOperationException>(() => CoordinatorRunCheckIn.DeliverAsync(f.Root.FullName, ProcessTests.BundleDirectory,
            Endpoint, Binding, f.Client, _ => throw new InvalidOperationException("No network expected")));
        Assert.True(AcquisitionLease.IsActive);
    }

    [Fact]
    public async Task DamagedOlderRunDoesNotPreventCheckingInALaterRun()
    {
        using var f = new Fixture();
        var profileRoot = Path.Combine(f.Root.FullName, Binding.ProfileId.ToString("N"));
        var brokenRoot = Directory.CreateDirectory(Path.Combine(profileRoot, "00000000000000000000000000000001")).FullName;
        var broken = new CoordinatorRunArchive(brokenRoot, Endpoint, Binding, f.Client);
        broken.Store(f.Run);
        var brokenFile = Directory.GetFiles(brokenRoot, "checkin-run-*.json").Single();
        var before = await File.ReadAllTextAsync(brokenFile);
        var laterRoot = Directory.CreateDirectory(Path.Combine(profileRoot, "00000000000000000000000000000002")).FullName;
        var ledgerRoot = Directory.CreateDirectory(Path.Combine(laterRoot, "ledger")).FullName;
        var run = f.Run with { AutomaticWorkload = false };
        await using (var runtime = new RuntimeController(ProcessTests.BundleDirectory, ledgerRoot))
        {
            await runtime.StartAsync(Binding.RigId.ToString("D"), default);
            var opened = await runtime.OpenGeometryAsync(run.Allocation.Snapshot.Program, run.Constraints, run.InitialState, default);
            run = run with { Ledger = opened.Value ?? throw new InvalidDataException("Fixture could not initialize its ledger.") };
        }
        new CoordinatorRunArchive(laterRoot, Endpoint, Binding, f.Client).Store(run);
        var progress = new List<CoordinatorRunCheckInProgress>();
        await Assert.ThrowsAsync<InvalidDataException>(() => CoordinatorRunCheckIn.DeliverAsync(f.Root.FullName, ProcessTests.BundleDirectory,
            Endpoint, Binding, f.Client, _ => throw new InvalidOperationException("An empty ledger needs no network"), new InlineProgress(progress.Add)));
        Assert.Contains(progress, update => update.Runs == 1 && update.RunId == Path.GetFileName(laterRoot));
        Assert.Equal(before, await File.ReadAllTextAsync(brokenFile));
        Assert.False(Directory.Exists(Path.Combine(brokenRoot, "ledger")));
        Assert.False(AcquisitionLease.IsActive);
    }

    private sealed class InlineProgress(Action<CoordinatorRunCheckInProgress> report) : IProgress<CoordinatorRunCheckInProgress>
    {
        public void Report(CoordinatorRunCheckInProgress value) => report(value);
    }

    [Fact]
    public void HistoricalBatchFailuresCannotHideAuthenticationOrCancellation()
    {
        Assert.True(CoordinatorRunCheckIn.HistoricalFailure(new AggregateException(new InvalidDataException(),
            new CoordinatorIntakeException(CoordinatorIntakeFailure.UnexpectedStatus, System.Net.HttpStatusCode.Conflict))));
        Assert.False(CoordinatorRunCheckIn.HistoricalFailure(new AggregateException(new InvalidDataException(), new OperationCanceledException())));
        Assert.False(CoordinatorRunCheckIn.HistoricalFailure(new AggregateException(new InvalidDataException(),
            new CoordinatorIntakeException(CoordinatorIntakeFailure.AuthenticationRequired))));
        Assert.False(CoordinatorRunCheckIn.HistoricalFailure(new AggregateException()));
    }

    [Fact]
    public async Task EmptyLedgerCannotBeInitializedByCheckIn()
    {
        using var f = new Fixture();
        f.Archive.Store(f.Run);
        var ledger = Path.Combine(Directory.CreateDirectory(Path.Combine(f.RunRoot, "ledger")).FullName, "execution.sqlite");
        File.WriteAllBytes(ledger, []);
        await Assert.ThrowsAsync<InvalidDataException>(() => CoordinatorRunCheckIn.DeliverAsync(f.Root.FullName, ProcessTests.BundleDirectory,
            Endpoint, Binding, f.Client, _ => throw new InvalidOperationException("No network expected")));
        Assert.Equal(0, new FileInfo(ledger).Length);
    }

    [Fact]
    public void ModeDefaultsToLiveAndCheckInItemRefusesSessionHooks()
    {
        var options = Newtonsoft.Json.JsonConvert.DeserializeObject<DirectorSessionOptions>("{}")!;
        Assert.Equal(DirectorCheckInMode.Live, options.CheckInMode);
        options.CheckInMode = (DirectorCheckInMode)100;
        Assert.Contains("Unknown check-in mode.", options.ValidateSettings());
        var item = new DirectorCheckInItem(new DirectorCheckInService(Mock.Of<IProfileService>()));
        Assert.True(item.Validate());
        var session = new DirectorSessionContainer();
        session.AfterEachExposure.Add(item);
        Assert.False(item.Validate());
        Assert.IsType<DirectorCheckInItem>(item.Clone());
    }
}
