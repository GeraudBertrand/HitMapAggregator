using System.Diagnostics;
using System.Reflection;
using HitMapAggregator.Core.Models;
using HitMapAggregator.Core.Session;

namespace HitMapAggregator.Tests;

/// <summary>
/// Mesures indicatives, exclues par <c>-notrait "Category=Performance"</c>.
/// </summary>
[Trait("Category", "Performance")]
public class PerformanceTests(ITestOutputHelper output)
{
    private const int Points = 50_000;

    private const string NeedsOptimizedBuild = "mesure non significative sur une build non optimisee : relancer en Release";

    /// <summary>
    /// Le JIT sans optimisations donne des temps trois a quatre fois plus longs, qui ne
    /// disent rien du comportement en production. Mesurer la n'aurait aucun sens, et
    /// relacher le seuil pour que ca passe quand meme en dirait encore moins.
    /// </summary>
    /// <remarks>
    /// Publique et statique parce que xUnit l'evalue a la decouverte, via
    /// <c>SkipUnless</c>. C'est un saut <i>statique</i> : contrairement a
    /// <c>Assert.SkipUnless</c>, il ne leve aucune exception, donc le debogueur de
    /// Visual Studio ne s'arrete pas dessus.
    /// </remarks>
    public static bool CoreIsOptimized =>
        typeof(ChannelSession).Assembly.GetCustomAttribute<DebuggableAttribute>()
            is not { IsJITOptimizerDisabled: true };

    [Fact(Skip = NeedsOptimizedBuild, SkipUnless = nameof(CoreIsOptimized))]
    public void Full_tick_on_fifty_thousand_points_stays_under_ten_milliseconds()
    {
        ChannelSession session = Loaded();
        Warm(session);

        const int Iterations = 51;
        var timings = new double[Iterations];
        for (int i = 0; i < Iterations; i++)
        {
            long start = Stopwatch.GetTimestamp();
            session.BuildSnapshot();
            timings[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        }

        Array.Sort(timings);
        double median = timings[Iterations / 2];
        double p99 = timings[(int)(Iterations * 0.99)];

        output.WriteLine($"tick sur {Points} points : median {median:F2} ms, p99 {p99:F2} ms");
        Assert.True(median < 10.0, $"tick median {median:F2} ms, p99 {p99:F2} ms sur {Points} points");
    }

    [Fact]
    public void A_tick_barely_allocates()
    {
        // Les tampons du champ de densite sont pre-alloues et reutilises. Ce qui reste
        // par tick, c'est l'instantane et son tableau de zones : quelques centaines
        // d'octets. Un tableau de grille qui reapparaitrait ici ferait exploser le
        // compte (9216 flottants = 36 Ko). Le comptage ne depend pas des optimisations,
        // donc ce test-la vaut aussi en Debug.
        ChannelSession session = Loaded();
        Warm(session);

        const int Iterations = 100;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < Iterations; i++)
        {
            session.BuildSnapshot();
        }

        long perTick = (GC.GetAllocatedBytesForCurrentThread() - before) / Iterations;

        output.WriteLine($"{perTick} octets alloues par tick");
        Assert.True(perTick < 2_048, $"{perTick} octets alloues par tick");
    }

    private static void Warm(ChannelSession session)
    {
        for (int i = 0; i < 20; i++)
        {
            session.BuildSnapshot();
        }
    }

    private static ChannelSession Loaded()
    {
        var session = new ChannelSession("perf");
        session.Start();
        NormalizedPoint[] points = SyntheticData.Generate(Scenario.Fragmented, Points);

        for (int i = 0; i < points.Length; i++)
        {
            session.Upsert((ulong)i, points[i]);
        }

        return session;
    }
}
