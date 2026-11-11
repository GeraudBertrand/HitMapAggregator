using HitMapAggregator.Core;
using HitMapAggregator.Core.Models;

namespace HitMapAggregator.Visualizer;

/// <summary>
/// Une carte de clics tiree au hasard, dont les poids derivent dans le temps.
/// </summary>
/// <remarks>
/// Rien n'est prefabrique : positions, tailles, orientations et parts sont tirees a
/// chaque nouveau monde. Surtout, les poids suivent une marche aleatoire a chaque tick,
/// et c'est la le point : sans derive, les pourcentages resteraient colles a leur valeur
/// injectee et aucun classement ne s'inverserait jamais. C'est cette derive qui produit
/// les depassements qu'on veut pouvoir detecter.
///
/// Les amas sont decrits dans l'espace corrige de l'aspect (voir <see cref="Grid"/>) :
/// un sigma est une fraction de la LARGEUR, un angle est celui qu'on voit a l'ecran.
/// </remarks>
internal sealed class RandomWorld
{
    private const float Margin = 1e-4f;

    private readonly Random _random;
    private readonly Blob[] _blobs;
    private double _spare;
    private bool _hasSpare;

    public RandomWorld(int seed, int clusterCount, float noiseFraction)
    {
        _random = new Random(seed);
        NoiseFraction = noiseFraction;

        _blobs = new Blob[Math.Max(1, clusterCount)];
        for (int i = 0; i < _blobs.Length; i++)
        {
            // On evite les bords : un amas coupe par le cadre fausserait sa covariance
            // sans que ce soit imputable a l'algorithme.
            float sigmaA = Lerp(0.020f, 0.060f, (float)_random.NextDouble());
            float elongation = Lerp(1.0f, 3.5f, (float)_random.NextDouble());

            _blobs[i] = new Blob
            {
                X = Lerp(0.12f, 0.88f, (float)_random.NextDouble()),
                Y = Lerp(0.12f, 0.88f, (float)_random.NextDouble()) * Grid.AspectY,
                SigmaA = sigmaA,
                SigmaB = sigmaA / elongation,
                Angle = (float)(_random.NextDouble() * Math.PI),
                Weight = Lerp(0.4f, 1.6f, (float)_random.NextDouble()),
            };
        }

        Normalize();
    }

    /// <summary>Part des clics tires uniformement sur tout l'ecran.</summary>
    public float NoiseFraction { get; set; }

    public int ClusterCount => _blobs.Length;

    /// <summary>Parts courantes, dans l'ordre de creation. Sert a afficher la verite terrain.</summary>
    public IReadOnlyList<float> Weights => Array.ConvertAll(_blobs, static b => b.Weight);

    /// <summary>
    /// Fait deriver les poids d'un cran. C'est ce qui cree les renversements de classement.
    /// </summary>
    public void Drift(float volatility)
    {
        if (volatility <= 0f)
        {
            return;
        }

        foreach (Blob blob in _blobs)
        {
            // Plancher strictement positif : un amas a poids nul disparaitrait
            // definitivement, la marche aleatoire ne pourrait plus le ramener.
            blob.Weight = MathF.Max(0.05f, blob.Weight + (float)(NextGaussian() * volatility));
        }

        Normalize();
    }

    /// <summary>Tire une position de clic dans le melange courant.</summary>
    public NormalizedPoint Sample(long timestampMs)
    {
        if (_random.NextDouble() < NoiseFraction)
        {
            return new NormalizedPoint(
                Clamp01((float)_random.NextDouble()),
                Clamp01((float)_random.NextDouble()),
                timestampMs);
        }

        Blob blob = PickWeighted();

        float major = (float)NextGaussian() * blob.SigmaA;
        float minor = (float)NextGaussian() * blob.SigmaB;
        float cos = MathF.Cos(blob.Angle);
        float sin = MathF.Sin(blob.Angle);

        float x = blob.X + major * cos - minor * sin;
        float y = blob.Y + major * sin + minor * cos;

        return new NormalizedPoint(Clamp01(x), Clamp01(y / Grid.AspectY), timestampMs);
    }

    private Blob PickWeighted()
    {
        double target = _random.NextDouble();
        double cumulative = 0.0;

        for (int i = 0; i < _blobs.Length; i++)
        {
            cumulative += _blobs[i].Weight;
            if (target <= cumulative)
            {
                return _blobs[i];
            }
        }

        return _blobs[^1];
    }

    private void Normalize()
    {
        float total = 0f;
        foreach (Blob blob in _blobs)
        {
            total += blob.Weight;
        }

        foreach (Blob blob in _blobs)
        {
            blob.Weight /= total;
        }
    }

    /// <summary>Box-Muller, avec conservation de la seconde valeur.</summary>
    private double NextGaussian()
    {
        if (_hasSpare)
        {
            _hasSpare = false;
            return _spare;
        }

        double u1 = 1.0 - _random.NextDouble();
        double u2 = _random.NextDouble();
        double magnitude = Math.Sqrt(-2.0 * Math.Log(u1));

        _spare = magnitude * Math.Sin(2.0 * Math.PI * u2);
        _hasSpare = true;
        return magnitude * Math.Cos(2.0 * Math.PI * u2);
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    private static float Clamp01(float v) => Math.Clamp(v, 0f, 1f - Margin);

    private sealed class Blob
    {
        public float X;
        public float Y;
        public float SigmaA;
        public float SigmaB;
        public float Angle;
        public float Weight;
    }
}
