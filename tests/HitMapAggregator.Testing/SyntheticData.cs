using HitMapAggregator.Core;
using HitMapAggregator.Core.Models;

namespace HitMapAggregator.Testing;

public enum Scenario
{
    BinaryVote,
    Consensus,
    Fragmented,
    Elongated,
    LShape,
    Concentric,
}

/// <summary>
/// Generateur deterministe de jeux de clics synthetiques.
/// </summary>
/// <remarks>
/// Les amas sont decrits dans l'espace corrige de l'aspect (voir <see cref="Grid"/>) :
/// un sigma exprime une fraction de la LARGEUR d'ecran, et un angle est celui qu'on
/// verrait reellement a l'ecran. C'est indispensable pour que le test d'angle ait un
/// sens : on injecte une orientation visuelle et on doit retrouver la meme.
///
/// Le PRNG est ecrit ici plutot que d'utiliser <c>System.Random</c> : la sequence
/// produite par <c>Random(seed)</c> n'est pas garantie stable entre versions du
/// runtime, ce qui ruinerait la reproductibilite des tests a la premiere mise a jour.
/// </remarks>
public static class SyntheticData
{
    public const int DefaultSeed = 1729;
    public const long DefaultTimestampMs = 1_000_000L;

    /// <summary>Marge de securite : on evite de coller aux bords, ou le binning saturerait.</summary>
    private const float Margin = 1e-4f;

    public static NormalizedPoint[] Generate(Scenario scenario, int count, int seed = DefaultSeed) => scenario switch
    {
        Scenario.BinaryVote => BinaryVote(count, seed),
        Scenario.Consensus => Consensus(count, seed),
        Scenario.Fragmented => Fragmented(count, seed),
        Scenario.Elongated => Elongated(count, seed),
        Scenario.LShape => LShape(count, seed),
        Scenario.Concentric => Concentric(count, seed),
        _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
    };

    public static IReadOnlyList<Scenario> All { get; } =
    [
        Scenario.BinaryVote,
        Scenario.Consensus,
        Scenario.Fragmented,
        Scenario.Elongated,
        Scenario.LShape,
        Scenario.Concentric,
    ];

    /// <summary>Deux camps serres et equilibres (~48 % / ~44 %), plus du bruit.</summary>
    public static NormalizedPoint[] BinaryVote(int count, int seed = DefaultSeed)
    {
        Rng rng = Seeded(seed);
        var sink = new List<NormalizedPoint>(count);
        AddCluster(sink, rng, 0.30f, 0.42f, 0.035f, 0.035f, 0f, Share(count, 0.48f));
        AddCluster(sink, rng, 0.72f, 0.56f, 0.035f, 0.035f, 0f, Share(count, 0.44f));
        AddUniform(sink, rng, count - sink.Count);
        return [.. sink];
    }

    /// <summary>Un amas dominant (~88 %), le reste disperse.</summary>
    public static NormalizedPoint[] Consensus(int count, int seed = DefaultSeed)
    {
        Rng rng = Seeded(seed);
        var sink = new List<NormalizedPoint>(count);
        AddCluster(sink, rng, 0.55f, 0.48f, 0.040f, 0.036f, 0f, Share(count, 0.88f));
        AddUniform(sink, rng, count - sink.Count);
        return [.. sink];
    }

    /// <summary>Six petits amas de 12 a 18 %, bien separes.</summary>
    public static NormalizedPoint[] Fragmented(int count, int seed = DefaultSeed)
    {
        ReadOnlySpan<float> shares = [0.18f, 0.17f, 0.16f, 0.15f, 0.14f, 0.12f];
        ReadOnlySpan<float> xs = [0.20f, 0.50f, 0.80f, 0.20f, 0.50f, 0.80f];
        ReadOnlySpan<float> ys = [0.28f, 0.28f, 0.28f, 0.72f, 0.72f, 0.72f];

        Rng rng = Seeded(seed);
        var sink = new List<NormalizedPoint>(count);
        for (int i = 0; i < shares.Length; i++)
        {
            AddCluster(sink, rng, xs[i], ys[i], 0.025f, 0.025f, 0f, Share(count, shares[i]));
        }
        AddUniform(sink, rng, count - sink.Count);
        return [.. sink];
    }

    /// <summary>Un amas tres allonge horizontalement (sigma majeur = 6,7x le mineur) et un amas rond.</summary>
    public static NormalizedPoint[] Elongated(int count, int seed = DefaultSeed)
    {
        Rng rng = Seeded(seed);
        var sink = new List<NormalizedPoint>(count);
        AddCluster(sink, rng, 0.35f, 0.30f, 0.100f, 0.015f, 0f, Share(count, 0.55f));
        AddCluster(sink, rng, 0.72f, 0.70f, 0.035f, 0.035f, 0f, Share(count, 0.37f));
        AddUniform(sink, rng, count - sink.Count);
        return [.. sink];
    }

    /// <summary>Deux segments perpendiculaires : une forme non convexe, que l'ellipse ne peut pas decrire.</summary>
    public static NormalizedPoint[] LShape(int count, int seed = DefaultSeed)
    {
        Rng rng = Seeded(seed);
        var sink = new List<NormalizedPoint>(count);
        int half = count / 2;
        AddSegment(sink, rng, 0.25f, 0.25f, 0.25f, 0.75f, 0.012f, half);
        AddSegment(sink, rng, 0.25f, 0.75f, 0.75f, 0.75f, 0.012f, count - half);
        return [.. sink];
    }

    /// <summary>Noyau dense et halo diffus, exactement au meme centre.</summary>
    public static NormalizedPoint[] Concentric(int count, int seed = DefaultSeed)
    {
        Rng rng = Seeded(seed);
        var sink = new List<NormalizedPoint>(count);
        AddCluster(sink, rng, 0.50f, 0.50f, 0.022f, 0.022f, 0f, Share(count, 0.55f));
        AddCluster(sink, rng, 0.50f, 0.50f, 0.130f, 0.130f, 0f, count - sink.Count);
        return [.. sink];
    }

    /// <summary>Bruit uniforme pur : aucune zone ne devrait en sortir.</summary>
    public static NormalizedPoint[] UniformNoise(int count, int seed = DefaultSeed)
    {
        Rng rng = Seeded(seed);
        var sink = new List<NormalizedPoint>(count);
        AddUniform(sink, rng, count);
        return [.. sink];
    }

    /// <summary>
    /// Un amas gaussien unique d'orientation choisie. Sert au test de restitution
    /// d'angle : l'orientation injectee ici est celle attendue en sortie.
    /// </summary>
    public static NormalizedPoint[] SingleCluster(
        float centerX,
        float centerY,
        float sigmaMajor,
        float sigmaMinor,
        float angleRadians,
        int count,
        int seed = DefaultSeed)
    {
        Rng rng = Seeded(seed);
        var sink = new List<NormalizedPoint>(count);
        AddCluster(sink, rng, centerX, centerY, sigmaMajor, sigmaMinor, angleRadians, count);
        return [.. sink];
    }

    /// <summary>Une graine negative reste une graine : on reinterprete les bits sans les perdre.</summary>
    private static Rng Seeded(int seed) => new((ulong)(uint)seed);

    /// <summary>
    /// N amas identiques repartis en grille, chacun portant la meme part du vote.
    /// Sert a explorer le regime « carte a nombreuses cibles ».
    /// </summary>
    public static NormalizedPoint[] EqualZones(int zones, int count, int seed = DefaultSeed)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(zones, 1);

        // Grille au ratio de l'ecran, pour maximiser l'ecart entre amas voisins.
        int columns = (int)Math.Ceiling(Math.Sqrt(zones / (double)Grid.AspectY));
        int rows = (int)Math.Ceiling(zones / (double)columns);

        var sink = new List<NormalizedPoint>(count);
        for (int i = 0; i < zones; i++)
        {
            sink.AddRange(SingleCluster(
                (i % columns + 0.5f) / columns,
                (i / columns + 0.5f) / rows,
                0.015f,
                0.015f,
                0f,
                count / zones,
                seed + i));
        }

        return [.. sink];
    }

    private static int Share(int total, float fraction) => (int)MathF.Round(total * fraction);

    private static void AddCluster(
        List<NormalizedPoint> sink,
        Rng rng,
        float centerX,
        float centerY,
        float sigmaMajor,
        float sigmaMinor,
        float angleRadians,
        int count)
    {
        float cy = centerY * Grid.AspectY;
        float cos = MathF.Cos(angleRadians);
        float sin = MathF.Sin(angleRadians);

        for (int i = 0; i < count; i++)
        {
            float a = (float)rng.NextGaussian() * sigmaMajor;
            float b = (float)rng.NextGaussian() * sigmaMinor;
            float x = centerX + a * cos - b * sin;
            float y = cy + a * sin + b * cos;
            sink.Add(FromCorrected(x, y));
        }
    }

    private static void AddSegment(
        List<NormalizedPoint> sink,
        Rng rng,
        float x0,
        float y0,
        float x1,
        float y1,
        float spread,
        int count)
    {
        float cy0 = y0 * Grid.AspectY;
        float cy1 = y1 * Grid.AspectY;

        for (int i = 0; i < count; i++)
        {
            float t = (float)rng.NextDouble();
            float x = x0 + (x1 - x0) * t + (float)rng.NextGaussian() * spread;
            float y = cy0 + (cy1 - cy0) * t + (float)rng.NextGaussian() * spread;
            sink.Add(FromCorrected(x, y));
        }
    }

    private static void AddUniform(List<NormalizedPoint> sink, Rng rng, int count)
    {
        for (int i = 0; i < count; i++)
        {
            sink.Add(new NormalizedPoint(
                Clamp01((float)rng.NextDouble()),
                Clamp01((float)rng.NextDouble()),
                DefaultTimestampMs));
        }
    }

    private static NormalizedPoint FromCorrected(float x, float correctedY) =>
        new(Clamp01(x), Clamp01(correctedY / Grid.AspectY), DefaultTimestampMs);

    private static float Clamp01(float v) => Math.Clamp(v, 0f, 1f - Margin);

    /// <summary>
    /// SplitMix64. Court, sans etat cache, et surtout : sa sortie ne depend que de
    /// la graine, definitivement.
    /// </summary>
    private sealed class Rng(ulong seed)
    {
        private ulong _state = seed;

        public ulong NextULong()
        {
            ulong z = _state += 0x9E3779B97F4A7C15UL;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }

        /// <summary>Uniforme dans [0,1).</summary>
        public double NextDouble() => (NextULong() >> 11) * (1.0 / 9007199254740992.0);

        /// <summary>Box-Muller. On jette la seconde valeur : le generateur n'est pas dans un chemin chaud.</summary>
        public double NextGaussian()
        {
            double u1 = 1.0 - NextDouble();
            double u2 = NextDouble();
            return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
        }
    }
}
