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

    public required Crowd Before { get; init; }

    public Crowd? After { get; init; }

    /// <summary>Arret de la collecte, puis reprise : seule la config 400 s'en sert.</summary>
    public long? StopAtMs { get; init; }

    public long? RestartAtMs { get; init; }

    public int? ExpectedVotes { get; init; }

    public long? ExpectedAccepted { get; init; }

    public Crowd SourceAt(long elapsedMs) =>
        After is not null && RestartAtMs is { } restart && elapsedMs >= restart ? After : Before;
}

internal sealed record BenchConfig(string Id, string Title, string Description, string KnownScenario)
{
    /// <summary>Un viewer qui reclique change d'avis en moyenne une fois par minute.</summary>
    private const float MindChangeMs = 60_000f;

    public static IReadOnlyList<BenchConfig> All { get; } =
    [
        new("10k", "10 000 viewers · 1 clic",
            "Un clic par viewer. La plupart répondent tout de suite, un quart à la relance de mi-passe.",
            "Vote binaire (48 % / 44 %)"),
        new("1k50", "1 000 viewers · 50 clics",
            "Un clic toutes les 250 ms, le maximum de l'extension : ~4 000 clics/s. Seul le dernier clic de chacun compte.",
            "Vote fragmenté (6 cibles)"),
        new("50k", "50 000 viewers · 60 % cliquent",
            "30 000 viewers cliquent, dont 5 000 cinq fois. 30 000 votes attendus.",
            "Consensus (88 %)"),
        new("400", "400 viewers · arrêt / reprise",
            "Un clic par seconde et par viewer. Collecte coupée 10 s au milieu, puis nouvelle cible.",
            "Cible A puis cible B"),
    ];

    /// <summary>Passe d'echauffement : paie la compilation JIT, jamais comptee.</summary>
    public static BenchConfig WarmUp { get; } = new("warmup", "Échauffement", "Non comptée.", "");

    public ClickPlan CreatePlan(Distribution distribution, TimeSpan duration, int seed)
    {
        long durationMs = (long)duration.TotalMilliseconds;
        var random = new Random(seed);

        Crowd Source(int viewers, int intervalMs, int worldSeed, params TargetSpec[] scene) =>
            distribution == Distribution.Known
                ? Crowd.Fixed(worldSeed, viewers, intervalMs / MindChangeMs, scene)
                : Crowd.Drifting(worldSeed, viewers, intervalMs / MindChangeMs);

        long[] Spread(int count, long windowMs) =>
            [.. Enumerable.Range(0, count).Select(_ => (long)(random.NextDouble() * windowMs))];

        // Temps de reaction log-normal : la plupart repondent vite, une traine arrive tard,
        // et un quart ne clique qu'a la relance de mi-passe.
        long[] Waves(int count, long windowMs) =>
            [.. Enumerable.Range(0, count).Select(_ =>
            {
                bool relaunch = random.NextDouble() < 0.25;
                double t;
                do
                {
                    double gaussian = Math.Sqrt(-2.0 * Math.Log(1.0 - random.NextDouble())) * Math.Cos(Math.Tau * random.NextDouble());
                    t = (relaunch ? 0.5 : 0.0) + (relaunch ? 0.06 : 0.12) * Math.Exp(gaussian * 0.8);
                }
                while (t >= 1.0);

                return (long)(t * windowMs);
            })];

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
                    Before = Crowd.Drifting(seed, 2_000, 1_000 / MindChangeMs),
                };

            case "10k":
                return new ClickPlan
                {
                    Audience = 10_000,
                    Clickers = 10_000,
                    StartMs = Waves(10_000, durationMs * 3 / 4),
                    Clicks = Fill(10_000, 1),
                    IntervalMs = 250,
                    Before = Source(10_000, 250, seed,
                        new(0.30f, 0.42f, 0.08f, 0.48f),
                        new(0.72f, 0.56f, 0.08f, 0.44f)),
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
                    Before = Source(1_000, 250, seed,
                        new(0.20f, 0.28f, 0.055f, 0.18f),
                        new(0.50f, 0.28f, 0.055f, 0.17f),
                        new(0.80f, 0.28f, 0.055f, 0.16f),
                        new(0.20f, 0.72f, 0.055f, 0.15f),
                        new(0.50f, 0.72f, 0.055f, 0.14f),
                        new(0.80f, 0.72f, 0.055f, 0.12f)),
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
                    StartMs = Waves(30_000, durationMs * 3 / 4),
                    Clicks = clicks,
                    IntervalMs = 250,
                    Before = Source(30_000, 250, seed, new TargetSpec(0.55f, 0.48f, 0.09f, 0.88f)),
                    ExpectedVotes = 30_000,
                    ExpectedAccepted = 25_000 + 5_000 * 5,
                };
            }

            case "400":
            {
                long middle = durationMs / 2;

                return new ClickPlan
                {
                    Audience = 400,
                    Clickers = 400,
                    StartMs = Spread(400, 1_000),
                    Clicks = Fill(400, ClickPlan.Continuous),
                    IntervalMs = 1_000,
                    Before = Source(400, 1_000, seed, new TargetSpec(0.30f, 0.40f, 0.08f, 0.92f)),
                    After = Source(400, 1_000, seed + 1, new TargetSpec(0.70f, 0.62f, 0.08f, 0.92f)),
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
