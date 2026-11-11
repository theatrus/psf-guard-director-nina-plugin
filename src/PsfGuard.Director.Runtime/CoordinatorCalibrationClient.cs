using System.Collections.Immutable;
using System.Text.Json;

namespace PsfGuard.Director.Runtime;

public sealed record CalibrationPlan(DirectorConfiguration Configuration, ImmutableArray<ExposureRecipe> Recipes);

/// <summary>Read calibration settings and exchange flat coverage using the existing Director pairing.</summary>
public sealed class CoordinatorCalibrationClient : IDisposable
{
    private readonly CoordinatorTransport transport;
    private readonly CoordinatorBinding binding;
    private readonly DirectorFlatCoverage coverage;
    public CoordinatorCalibrationClient(Uri endpoint, CoordinatorBinding binding, DirectorFlatCoverage coverage,
        Func<CancellationToken, ValueTask<string?>> credential, bool allowInsecureHttp = false)
        : this(endpoint, binding, coverage, credential, CoordinatorTransport.Handler(), allowInsecureHttp) { }
    internal CoordinatorCalibrationClient(Uri endpoint, CoordinatorBinding binding, DirectorFlatCoverage coverage,
        Func<CancellationToken, ValueTask<string?>> credential, HttpMessageHandler handler, bool allowInsecureHttp = false)
    {
        CoordinatorCheckpointClient.ValidateBinding(binding);
        this.binding = binding; this.coverage = coverage;
        transport = new(endpoint, credential, handler, allowInsecureHttp);
    }
    private sealed record Answer(uint ProtocolVersion, Guid CoordinatorInstanceId, Guid CatalogId, Guid RigId, Guid ProfileId,
        Guid OriginId, DirectorConfiguration Configuration, ImmutableArray<ExposureRecipe> Recipes,
        int Received, int Acknowledged, FlatCoverageDecision[] Decisions, FlatCoverageDecision[] Retired);

    public async Task<CalibrationPlan> RefreshAsync(DirectorConfiguration expected, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            CalibrationPlan? plan = null;
            var records = coverage.Records();
            // Replay is intentional: the server never revives invalidated coverage.
            foreach (var chunk in records.Chunk(500).DefaultIfEmpty([]))
            {
                var acknowledgements = coverage.Pending().Take(500).ToArray();
                var body = JsonSerializer.SerializeToUtf8Bytes(new
                {
                    protocol_version = 1,
                    coordinator_instance_id = binding.CoordinatorInstanceId,
                    catalog_id = binding.CatalogId,
                    records = chunk,
                    acknowledgements
                }, CoordinatorProgramContract.Options);
                var response = await transport.SendAsync($"api/director/v1/rigs/{binding.RigId:D}/calibration", body, deadline.Token, binding.ProfileId);
                using var document = JsonDocument.Parse(response.Bytes);
                CoordinatorProgramContract.CheckTree(document.RootElement);
                var root = document.RootElement;
                if (!root.GetProperty("success").GetBoolean()) throw new InvalidDataException("PSF Guard did not confirm calibration sync.");
                var answer = root.GetProperty("data").Deserialize<Answer>(CoordinatorProgramContract.Options) ?? throw new InvalidDataException();
                if (answer.ProtocolVersion != 1 || answer.CoordinatorInstanceId != binding.CoordinatorInstanceId
                    || answer.CatalogId != binding.CatalogId || answer.RigId != binding.RigId || answer.ProfileId != binding.ProfileId
                    || answer.OriginId != binding.ProfileId || answer.Received != chunk.Length || answer.Acknowledged != acknowledgements.Length
                    || answer.Configuration is null || answer.Recipes.IsDefault || answer.Recipes.Length > 10000 || answer.Decisions is null || answer.Decisions.Length > 1000
                    || answer.Retired is null || answer.Retired.Length > chunk.Length)
                    throw new InvalidDataException("PSF Guard returned calibration data for a different connection or request.");
                // Calibration does not depend on horizon, target movement, dithers
                // or operation ownership. Compare every camera/filter capability.
                if (DirectorFlatCoverage.Hash(answer.Configuration with { Id = "", EnableSlewCenter = false, DitherEvery = 0 })
                    != DirectorFlatCoverage.Hash(expected with { Id = "", EnableSlewCenter = false, DitherEvery = 0 }))
                    throw new CoordinatorIntakeException(CoordinatorIntakeFailure.ConfigurationMismatch);
                coverage.Apply(answer.Decisions);
                coverage.Acknowledged(acknowledgements);
                coverage.Apply(answer.Retired, acknowledge: false);
                plan = new(answer.Configuration, answer.Recipes);
            }
            return plan!;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new CoordinatorIntakeException(CoordinatorIntakeFailure.Timeout); }
        catch (JsonException error) { throw new InvalidDataException("PSF Guard returned unreadable calibration data.", error); }
    }
    public void Dispose() => transport.Dispose();
}
