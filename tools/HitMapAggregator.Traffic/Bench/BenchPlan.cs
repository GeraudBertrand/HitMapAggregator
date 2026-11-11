using HitMapAggregator.Core.Models;
using HitMapAggregator.Testing;

namespace HitMapAggregator.Traffic.Bench;

internal enum Distribution
{
    Known,
    Random,
}

/// <summary>
/// Script de clics d'une passe : qui clique, quand, combien de fois, et ou.
/// </summary>
/// <remarks>
/// Les viewers qui cliquent sont les indices <c>[0, Clickers)</c>. Ceux de
/// <c>[Clickers, Audience)</c> regardent sans cliquer : comme un viewer ne recoit rien du
/// serveur, ils ne coutent rien et n'existent ici que pour le compte rendu.
/// </remarks>
internal sealed class ClickPlan
{
    /// <summary>Clics illimites : le viewer clique jusqu'a la fin de la passe.</summary>
    public const int Continuous = int.MaxValue;

    public required int Audience { get; init; }

    public required int Clickers { get; init; }

    public required long[] StartMs { get; init; }

    public required int[] Clicks { get; init; }

    public required int IntervalMs { get; init; }

    public required PointSource Before { get; init; }

    public PointSource? After { get; init; }

    /// <summary>Arret de la collecte, puis reprise : seule la config 400 s'en sert.</summary>
    public long? StopAtMs { get; init; }

    public long? RestartAtMs { get; init; }

    public int? ExpectedVotes { get; init; }

    public long? ExpectedAccepted { get; init; }

    public PointSource SourceAt(long elapsedMs) =>
        After is not null && RestartAtMs is { } restart && elapsedMs >= restart ? After : Before;
}

/// <summary>Tire les positions : jeu synthetique fige, ou monde aleatoire qui derive.</summary>
internal sealed class PointSource
{
    private readonly NormalizedPoint[]? _pool;
    private readonly RandomWorld? _world;

    private PointSource(NormalizedPoint[]? pool, RandomWorld? world)
    {
        _pool = pool;
        _world = world;
    }

    public static PointSource Pool(NormalizedPoint[] pool) => new(pool, null);

    public static PointSource World(int seed) => new(null, new RandomWorld(seed, 6, 0.08f));

    public NormalizedPoint Next()
    {
        if (_pool is not null)
        {
            return _pool[Random.Shared.Next(_pool.Length)];
        }

        lock (_world!)
        {
            return _world.Sample(0L);
        }
    }

    /// <summary>Un pas de derive ; sans effet sur un jeu fige.</summary>
    public void Drift()
    {
        if (_world is null)
        {
            return;
        }

        lock (_world)
        {
            _world.Drift(0.02f);
        }
    }
}

internal sealed record BenchConfig(string Id, string Title, string Description, string KnownScenario)
{
    public static IReadOnlyList<BenchConfig> All { get; } =
    [
        new("10k", "10 000 viewers · 1 clic",
            "Chaque viewer clique une seule fois, arrivées étalées sur les trois premiers quarts de la passe.",
            "Vote binaire (48 % / 44 %)"),
        new("1k50", "1 000 viewers · 50 clics",
            "Chaque viewer clique 50 fois au rythme maximal autorisé par l'extension (250 ms) : ~4 000 clics/s pendant 12,5 s. Seul le dernier clic de chacun compte.",
            "Vote fragmenté"),
        new("50k", "50 000 viewers · 60 % cliquent",
            "30 000 viewers cliquent ; 5 000 d'entre eux (10 % de l'audience) cliquent 5 fois. Attendu : exactement 30 000 votes.",
            "Consensus (88 %)"),
        new("400", "400 viewers · arrêt / reprise",
            "Les 400 viewers cliquent en continu (1 clic/s chacun). La collecte est arrêtée 10 s en plein milieu (clics refusés en 409, carte gelée), puis reprend et les viewers visent une autre cible.",
            "Cible A puis cible B"),
    ];

    /// <summary>Passe d'echauffement : paie la compilation JIT, jamais comptee.</summary>
    public static BenchConfig WarmUp { get; } = new("warmup", "Échauffement", "Non comptée.", "");

    public ClickPlan CreatePlan(Distribution distribution, TimeSpan duration, int seed)
    {
        long durationMs = (long)duration.TotalMilliseconds;
        var random = new Random(seed);

        PointSource Source(Scenario scenario, int worldSeed) => distribution == Distribution.Known
            ? PointSource.Pool(SyntheticData.Generate(scenario, 20_000, seed))
            : PointSource.World(worldSeed);

        long[] Spread(int count, long windowMs) =>
            [.. Enumerable.Range(0, count).Select(_ => (long)(random.NextDouble() * windowMs))];

        switch (Id)
        {
            case "warmup":
                return new ClickPlan
                {
                    Audience = 2_000,
                    Clickers = 2_000,
                    StartMs = Spread(2_000, 1_000),
                    Clicks = Fill(2_000, ClickPlan.Continuous),
                    IntervalMs = 1_000,
                    Before = PointSource.World(seed),
                };

            case "10k":
                return new ClickPlan
                {
                    Audience = 10_000,
                    Clickers = 10_000,
                    StartMs = Spread(10_000, durationMs * 3 / 4),
                    Clicks = Fill(10_000, 1),
                    IntervalMs = 250,
                    Before = Source(Scenario.BinaryVote, seed),
                    ExpectedVotes = 10_000,
                    ExpectedAccepted = 10_000,
                };

            case "1k50":
                return new ClickPlan
                {
                    Audience = 1_000,
                    Clickers = 1_000,
                    StartMs = Spread(1_000, 250),
                    Clicks = Fill(1_000, 50),
                    IntervalMs = 250,
                    Before = Source(Scenario.Fragmented, seed),
                    ExpectedVotes = 1_000,
                    ExpectedAccepted = 50_000,
                };

            case "50k":
            {
                int[] clicks = Fill(30_000, 1);
                Array.Fill(clicks, 5, 0, 5_000);

                return new ClickPlan
                {
                    Audience = 50_000,
                    Clickers = 30_000,
                    StartMs = Spread(30_000, durationMs * 3 / 4),
                    Clicks = clicks,
                    IntervalMs = 250,
                    Before = Source(Scenario.Consensus, seed),
                    ExpectedVotes = 30_000,
                    ExpectedAccepted = 25_000 + 5_000 * 5,
                };
            }

            case "400":
            {
                long middle = durationMs / 2;
                bool known = distribution == Distribution.Known;

                return new ClickPlan
                {
                    Audience = 400,
                    Clickers = 400,
                    StartMs = Spread(400, 1_000),
                    Clicks = Fill(400, ClickPlan.Continuous),
                    IntervalMs = 1_000,
                    Before = known
                        ? PointSource.Pool(SyntheticData.SingleCluster(0.30f, 0.40f, 0.045f, 0.025f, 0.5f, 5_000, seed))
                        : PointSource.World(seed),
                    After = known
                        ? PointSource.Pool(SyntheticData.SingleCluster(0.70f, 0.62f, 0.045f, 0.025f, 2.2f, 5_000, seed))
                        : PointSource.World(seed + 1),
                    StopAtMs = middle - 5_000,
                    RestartAtMs = middle + 5_000,
                    ExpectedVotes = 400,
                };
            }

            default:
                throw new InvalidOperationException($"config inconnue : {Id}");
        }
    }

    private static int[] Fill(int count, int value)
    {
        int[] array = new int[count];
        Array.Fill(array, value);
        return array;
    }
}
