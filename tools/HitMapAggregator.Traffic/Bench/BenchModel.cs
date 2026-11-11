using System.Text.Json;

namespace HitMapAggregator.Traffic.Bench;

// Ce que voit la page : le meme modele sert au direct (par evenements) et au rapport
// (serialise d'un bloc dans le HTML).

internal sealed class BenchRun
{
    public required string StartedAt { get; init; }

    public required string Machine { get; init; }

    public required double PassSeconds { get; init; }

    public string Status { get; set; } = "warmup";

    public List<PassResult> Passes { get; } = [];
}

internal sealed class PassResult
{
    public required string Id { get; init; }

    public required string Config { get; init; }

    public required string Title { get; init; }

    public required string Description { get; init; }

    public required string Distribution { get; init; }

    public required int Audience { get; init; }

    public required int Clickers { get; init; }

    public double? StopAtS { get; init; }

    public double? RestartAtS { get; init; }

    public string Status { get; set; } = "running";

    public List<Sample> Samples { get; } = [];

    /// <summary>Dernier instantane recu par le flux SSE, tel que l'overlay le recoit.</summary>
    public JsonElement? Snapshot { get; set; }

    /// <summary>Config 400 : la carte telle qu'elle est restee gelee pendant l'arret.</summary>
    public JsonElement? FrozenSnapshot { get; set; }

    public PassSummary? Summary { get; set; }

    public List<Check> Checks { get; } = [];
}

/// <param name="TickMaxMs">Pire tick serveur depuis l'echantillon precedent.</param>
/// <param name="ClickAgeMaxMs">Pire delai reception → publication depuis l'echantillon precedent.</param>
internal sealed record Sample(
    double T,
    bool Running,
    double ClicksPerSecond,
    double PostP50Ms,
    double PostP99Ms,
    double TickMaxMs,
    double ClickAgeMaxMs,
    int Votes,
    int Zones,
    double TopPct);

internal sealed record PassSummary(
    long Sent,
    long Accepted,
    long Conflicts,
    long Failed,
    int Votes,
    int Zones,
    double PeakClicksPerSecond,
    double TickP50Ms,
    double TickP99Ms,
    double TickMaxMs,
    double ClickAgeP50Ms,
    double ClickAgeP99Ms,
    double PostP50Ms,
    double PostP99Ms);

internal sealed record Check(string Label, bool Ok, string Detail);
