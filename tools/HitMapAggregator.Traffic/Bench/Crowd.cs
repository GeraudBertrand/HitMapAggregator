using HitMapAggregator.Core;
using HitMapAggregator.Core.Models;

namespace HitMapAggregator.Traffic.Bench;

/// <summary>Cible a l'ecran : centre normalise, taille en fraction de largeur, part du vote.</summary>
internal readonly record struct TargetSpec(float X, float Y, float Size, float Share);

/// <summary>
/// Foule de viewers qui visent des cibles de forme irreguliere.
/// </summary>
/// <remarks>
/// Pas une gaussienne par cible : lissee par le champ, elle redonne toujours un disque ou
/// une ellipse. Une cible est un chapelet de lobes le long d'un trajet courbe, et chaque
/// viewer garde son propre point vise : ses reclics tombent autour avec un tremblement,
/// au lieu d'etre retires au hasard dans toute la cible. Le bruit (viewers sans cible)
/// penche vers le centre de l'ecran, la ou l'oeil se pose.
///
/// Espace corrige de l'aspect, comme <c>SyntheticData</c> : une taille est une fraction
/// de la largeur, un angle est celui qu'on voit a l'ecran.
/// </remarks>
internal sealed class Crowd
{
    private const float Tremor = 0.004f;
    private const float SloppyShare = 0.10f;
    private const float SloppyFactor = 5f;
    private const float Margin = 1e-4f;

    private readonly Random _random;
    private readonly Lobe[][] _targets;
    private readonly float[] _weights;
    private readonly float _targetShare;
    private readonly float _mindChange;
    private readonly bool _drifts;
    private readonly (float X, float Y)?[] _aims;

    private Crowd(int seed, int viewers, float mindChange, bool drifts, IReadOnlyList<TargetSpec> targets)
    {
        _random = new Random(seed);
        _aims = new (float, float)?[viewers];
        _mindChange = mindChange;
        _drifts = drifts;
        // Decalage aleatoire des centres : une scene fixe garde ses parts, pas sa grille parfaite.
        _targets = [.. targets.Select(t => BuildTarget(t with
        {
            X = t.X + Gaussian() * 0.03f,
            Y = t.Y + Gaussian() * 0.03f / Grid.AspectY,
        }))];
        _weights = [.. targets.Select(t => t.Share)];
        _targetShare = _weights.Sum();
    }

    /// <summary>Cibles imposees ; leurs parts sont celles du vote, le reste est du bruit.</summary>
    public static Crowd Fixed(int seed, int viewers, float mindChange, params TargetSpec[] targets) =>
        new(seed, viewers, mindChange, drifts: false, targets);

    /// <summary>Six cibles tirees au hasard, dont les parts derivent pendant la passe.</summary>
    public static Crowd Drifting(int seed, int viewers, float mindChange)
    {
        var random = new Random(seed);
        TargetSpec[] targets = [.. Enumerable.Range(0, 6).Select(_ => new TargetSpec(
            Lerp(0.12f, 0.88f, random.NextSingle()),
            Lerp(0.12f, 0.88f, random.NextSingle()),
            Lerp(0.05f, 0.10f, random.NextSingle()),
            Lerp(0.4f, 1.6f, random.NextSingle())))];

        float total = targets.Sum(t => t.Share);
        targets = [.. targets.Select(t => t with { Share = t.Share / total * 0.92f })];
        return new Crowd(seed + 1, viewers, mindChange, drifts: true, targets);
    }

    public NormalizedPoint Next(int viewer)
    {
        lock (_random)
        {
            ref (float X, float Y)? aim = ref _aims[viewer];
            if (aim is null || _random.NextSingle() < _mindChange)
            {
                aim = NewAim();
            }

            float spread = _random.NextSingle() < SloppyShare ? Tremor * SloppyFactor : Tremor;
            float x = aim.Value.X + Gaussian() * spread;
            float y = aim.Value.Y + Gaussian() * spread;
            return new NormalizedPoint(Clamp01(x), Clamp01(y / Grid.AspectY), 0L);
        }
    }

    /// <summary>Un pas de marche aleatoire sur les parts ; sans effet sur une scene fixe.</summary>
    public void Drift()
    {
        if (!_drifts)
        {
            return;
        }

        lock (_random)
        {
            for (int i = 0; i < _weights.Length; i++)
            {
                // Plancher : une cible a part nulle ne reviendrait jamais.
                _weights[i] = MathF.Max(0.01f, _weights[i] + Gaussian() * 0.02f * _targetShare);
            }

            float total = _weights.Sum();
            for (int i = 0; i < _weights.Length; i++)
            {
                _weights[i] *= _targetShare / total;
            }
        }
    }

    private (float X, float Y) NewAim()
    {
        float pick = _random.NextSingle();
        for (int i = 0; i < _targets.Length; i++)
        {
            if (pick < _weights[i])
            {
                return SampleTarget(_targets[i]);
            }

            pick -= _weights[i];
        }

        return (0.5f + Gaussian() * 0.22f, (0.5f + Gaussian() * 0.22f) * Grid.AspectY);
    }

    private (float X, float Y) SampleTarget(Lobe[] lobes)
    {
        float pick = _random.NextSingle();
        Lobe lobe = lobes[^1];
        foreach (Lobe candidate in lobes)
        {
            if (pick < candidate.Weight)
            {
                lobe = candidate;
                break;
            }

            pick -= candidate.Weight;
        }

        float a = Gaussian() * lobe.SigmaA;
        float b = Gaussian() * lobe.SigmaB;
        float cos = MathF.Cos(lobe.Angle);
        float sin = MathF.Sin(lobe.Angle);
        return (lobe.X + a * cos - b * sin, lobe.Y + a * sin + b * cos);
    }

    /// <summary>
    /// 4 a 7 lobes de tailles et d'orientations variees, le long d'un trajet qui tourne.
    /// </summary>
    /// <remarks>
    /// Le champ est lisse sur ~3,5 % de la largeur (rayon 4, 4 passes) : une forme plus
    /// petite que ca redevient un disque. D'ou des cibles de 6 a 10 % de large.
    /// </remarks>
    private Lobe[] BuildTarget(TargetSpec spec)
    {
        int count = 4 + _random.Next(4);
        var lobes = new Lobe[count];
        float heading = _random.NextSingle() * MathF.Tau;
        float x = 0f;
        float y = 0f;

        for (int i = 0; i < count; i++)
        {
            float sigma = spec.Size * Lerp(0.25f, 0.6f, _random.NextSingle());
            lobes[i] = new Lobe(
                x,
                y,
                sigma,
                sigma / Lerp(1f, 2.5f, _random.NextSingle()),
                heading + Gaussian() * 0.4f,
                Lerp(0.3f, 1f, _random.NextSingle()));

            float step = spec.Size * Lerp(0.5f, 0.9f, _random.NextSingle());
            x += MathF.Cos(heading) * step;
            y += MathF.Sin(heading) * step;
            heading += Gaussian() * 0.7f;
        }

        // Recentre le barycentre sur la cible demandee : la scene fixe garde ses positions.
        float total = lobes.Sum(l => l.Weight);
        float meanX = lobes.Sum(l => l.X * l.Weight) / total;
        float meanY = lobes.Sum(l => l.Y * l.Weight) / total;
        float targetY = spec.Y * Grid.AspectY;

        return [.. lobes.Select(l => l with
        {
            X = l.X - meanX + spec.X,
            Y = l.Y - meanY + targetY,
            Weight = l.Weight / total,
        })];
    }

    private float Gaussian()
    {
        float u1 = 1f - _random.NextSingle();
        float u2 = _random.NextSingle();
        return MathF.Sqrt(-2f * MathF.Log(u1)) * MathF.Cos(MathF.Tau * u2);
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    private static float Clamp01(float v) => Math.Clamp(v, 0f, 1f - Margin);

    private readonly record struct Lobe(float X, float Y, float SigmaA, float SigmaB, float Angle, float Weight);
}
