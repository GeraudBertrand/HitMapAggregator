using HitMapAggregator.Core.Models;

namespace HitMapAggregator.Core.Aggregation;

/// <summary>
/// Binning des positions dans la grille, puis lissage par flou separable a somme glissante.
/// </summary>
public static class DensityFieldBuilder
{
    /// <summary>Au-dela, le noyau couvre plus que l'ecran : la borne evite les reglages absurdes.</summary>
    public const int MaxSmoothingRadius = 32;

    /// <summary>Quatre passes de boite par axe : l'empilement converge vers une gaussienne.</summary>
    private const int BlurPassesPerAxis = 4;

    /// <summary>
    /// Construit le champ de densite. N'alloue rien : les deux tampons appartiennent
    /// a l'appelant et sont echanges a chaque passe.
    /// </summary>
    /// <returns>
    /// Un champ adosse a celui des deux tampons qui contient le resultat — pas
    /// necessairement <paramref name="primary"/>.
    /// </returns>
    public static DensityField Build(
        ReadOnlySpan<NormalizedPoint> points,
        int smoothingRadius,
        float[] primary,
        float[] scratch)
    {
        ArgumentNullException.ThrowIfNull(primary);
        ArgumentNullException.ThrowIfNull(scratch);
        ArgumentOutOfRangeException.ThrowIfLessThan(primary.Length, Grid.CellCount, nameof(primary));
        ArgumentOutOfRangeException.ThrowIfLessThan(scratch.Length, Grid.CellCount, nameof(scratch));

        float[] source = primary;
        float[] target = scratch;

        Array.Clear(source, 0, Grid.CellCount);
        Bin(points, source);

        int radius = Math.Clamp(smoothingRadius, 0, MaxSmoothingRadius);
        if (radius > 0)
        {
            Span<float> weightsX = stackalloc float[Grid.Width];
            Span<float> weightsY = stackalloc float[Grid.Height];
            FillScatterWeights(weightsX, radius);
            FillScatterWeights(weightsY, radius);

            for (int pass = 0; pass < BlurPassesPerAxis; pass++)
            {
                BlurHorizontal(source, target, radius, weightsX);
                (source, target) = (target, source);
            }

            for (int pass = 0; pass < BlurPassesPerAxis; pass++)
            {
                BlurVertical(source, target, radius, weightsY);
                (source, target) = (target, source);
            }
        }

        return new DensityField(source, Grid.Width, Grid.Height, Max(source));
    }

    private static void Bin(ReadOnlySpan<NormalizedPoint> points, float[] grid)
    {
        for (int i = 0; i < points.Length; i++)
        {
            ref readonly NormalizedPoint p = ref points[i];
            int gx = Math.Clamp((int)(p.X * Grid.Width), 0, Grid.Width - 1);
            int gy = Math.Clamp((int)(p.Y * Grid.Height), 0, Grid.Height - 1);
            grid[gy * Grid.Width + gx] += 1f;
        }
    }

    /// <summary>
    /// Poids de dispersion : l'inverse du nombre de cellules qu'une source atteint
    /// reellement.
    /// </summary>
    /// <remarks>
    /// C'est ce qui rend le flou exactement conservatif. Normaliser par la taille de
    /// fenetre (2r+1) ferait fuir de la masse par les bords ; prolonger les bords en
    /// creerait. Ici chaque cellule source repartit exactement 1,0 de son poids sur
    /// ses cibles valides, donc la somme du champ est invariante — quel que soit le
    /// contenu, bords compris.
    /// </remarks>
    private static void FillScatterWeights(Span<float> weights, int radius)
    {
        int n = weights.Length;
        for (int j = 0; j < n; j++)
        {
            int lo = Math.Max(0, j - radius);
            int hi = Math.Min(n - 1, j + radius);
            weights[j] = 1f / (hi - lo + 1);
        }
    }

    private static void BlurHorizontal(float[] source, float[] target, int radius, ReadOnlySpan<float> weights)
    {
        Span<float> row = stackalloc float[Grid.Width];

        for (int y = 0; y < Grid.Height; y++)
        {
            int offset = y * Grid.Width;
            for (int x = 0; x < Grid.Width; x++)
            {
                row[x] = source[offset + x] * weights[x];
            }

            Accumulate(row, target, offset, 1, radius);
        }
    }

    private static void BlurVertical(float[] source, float[] target, int radius, ReadOnlySpan<float> weights)
    {
        Span<float> column = stackalloc float[Grid.Height];

        for (int x = 0; x < Grid.Width; x++)
        {
            for (int y = 0; y < Grid.Height; y++)
            {
                column[y] = source[y * Grid.Width + x] * weights[y];
            }

            Accumulate(column, target, x, Grid.Width, radius);
        }
    }

    /// <summary>
    /// Somme glissante sur une ligne ou une colonne : deux operations par cellule,
    /// quel que soit le rayon.
    /// </summary>
    private static void Accumulate(ReadOnlySpan<float> line, float[] target, int offset, int stride, int radius)
    {
        int n = line.Length;

        float sum = 0f;
        int initial = Math.Min(radius, n - 1);
        for (int i = 0; i <= initial; i++)
        {
            sum += line[i];
        }

        target[offset] = sum;

        for (int i = 1; i < n; i++)
        {
            int entering = i + radius;
            if (entering < n)
            {
                sum += line[entering];
            }

            int leaving = i - radius - 1;
            if (leaving >= 0)
            {
                sum -= line[leaving];
            }

            target[offset + i * stride] = sum;
        }
    }

    private static float Max(float[] values)
    {
        float max = 0f;
        for (int i = 0; i < Grid.CellCount; i++)
        {
            if (values[i] > max)
            {
                max = values[i];
            }
        }

        return max;
    }
}
