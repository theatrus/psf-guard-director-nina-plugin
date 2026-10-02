using System.Text.Json;
using System.Collections.Immutable;
using System.Text.Json.Nodes;
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
