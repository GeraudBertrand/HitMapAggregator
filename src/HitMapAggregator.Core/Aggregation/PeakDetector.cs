using HitMapAggregator.Core.Models;

namespace HitMapAggregator.Core.Aggregation;

/// <summary>
/// Detection des maxima locaux du champ de densite, tries par intensite et fusionnes
/// quand ils sont trop proches.
/// </summary>
public static class PeakDetector
{
    /// <summary>
    /// Borne structurelle, pas un reglage produit.
    /// </summary>
    /// <remarks>
    /// Elle n'existe que pour dimensionner a la compilation les tampons sur la pile :
    /// <see cref="ZoneBuilder"/> accumule six spans indexes par pic. Le nombre de zones
    /// reellement affichees se regle par <c>SessionSettings.MaxZones</c>, et le critere
    /// qui decide de leur existence est le pourcentage minimal.
    /// </remarks>
    public const int HardCeiling = 32;

    /// <summary>Voisinage 5x5.</summary>
    private const int NeighborhoodRadius = 2;

    /// <summary>
    /// Borne du tampon de candidats. Un champ lisse en produit une poignee ; la borne
    /// n'existe que pour garder la detection sur la pile, sans allocation.
    /// </summary>
    private const int MaxCandidates = 256;

    private static readonly Comparison<Peak> ByIntensityDescending = static (a, b) =>
    {
        int byIntensity = b.Intensity.CompareTo(a.Intensity);
        if (byIntensity != 0)
        {
            return byIntensity;
        }

        // Le tri n'est pas stable : on departage sur l'index de cellule pour que la
        // sortie soit strictement reproductible.
        int indexA = a.CellY * Grid.Width + a.CellX;
        int indexB = b.CellY * Grid.Width + b.CellX;
        return indexA.CompareTo(indexB);
    };

    /// <summary>
    /// Ecrit les pics retenus dans <paramref name="destination"/>, du plus intense au
    /// moins intense.
    /// </summary>
    /// <returns>Le nombre de pics ecrits.</returns>
    public static int Detect(
        in DensityField field,
        float relativeThreshold,
        float mergeDistance,
        float minFieldContrast,
        Span<Peak> destination)
    {
        int capacity = Math.Min(HardCeiling, destination.Length);
        if (capacity == 0 || field.Max <= 0f)
        {
            return 0;
        }

        // Un champ plat n'a pas de zones : sans ce test, le seuil de detection etant
        // relatif au maximum, il suivrait les fluctuations au lieu de les rejeter.
        float[] values = field.Values;
        if (Mean(values) * minFieldContrast > field.Max)
        {
            return 0;
        }

        float threshold = Math.Max(relativeThreshold, 0f) * field.Max;

        Span<Peak> candidates = stackalloc Peak[MaxCandidates];
        int candidateCount = 0;

        for (int y = 0; y < Grid.Height; y++)
        {
            for (int x = 0; x < Grid.Width; x++)
            {
                float value = values[y * Grid.Width + x];
                if (value <= threshold || !IsLocalMaximum(values, x, y, value))
                {
                    continue;
                }

                var peak = new Peak(
                    x,
                    y,
                    (x + 0.5f) / Grid.Width,
                    (y + 0.5f) / Grid.Height,
                    value);

                Offer(candidates, ref candidateCount, peak);
            }
        }

        Span<Peak> found = candidates[..candidateCount];
        found.Sort(ByIntensityDescending);

        return Merge(found, mergeDistance, destination, capacity);
    }

    private static float Mean(float[] values)
    {
        double sum = 0.0;
        for (int i = 0; i < Grid.CellCount; i++)
        {
            sum += values[i];
        }

        return (float)(sum / Grid.CellCount);
    }

    private static bool IsLocalMaximum(float[] values, int x, int y, float value)
    {
        int x0 = Math.Max(0, x - NeighborhoodRadius);
        int x1 = Math.Min(Grid.Width - 1, x + NeighborhoodRadius);
        int y0 = Math.Max(0, y - NeighborhoodRadius);
        int y1 = Math.Min(Grid.Height - 1, y + NeighborhoodRadius);
        int self = y * Grid.Width + x;

        for (int ny = y0; ny <= y1; ny++)
        {
            int offset = ny * Grid.Width;
            for (int nx = x0; nx <= x1; nx++)
            {
                int index = offset + nx;
                if (index == self)
                {
                    continue;
                }

                // Comparaison stricte d'un cote, large de l'autre : sur un plateau de
                // valeurs egales, seule la cellule de plus petit index survit. Sans ca
                // un plateau ne produirait aucun pic, ou bien tous.
                float neighbour = values[index];
                bool loses = index < self ? neighbour >= value : neighbour > value;
                if (loses)
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static void Offer(Span<Peak> candidates, ref int count, in Peak peak)
    {
        if (count < MaxCandidates)
        {
            candidates[count++] = peak;
            return;
        }

        int weakest = 0;
        for (int i = 1; i < MaxCandidates; i++)
        {
            if (candidates[i].Intensity < candidates[weakest].Intensity)
            {
                weakest = i;
            }
        }

        if (peak.Intensity > candidates[weakest].Intensity)
        {
            candidates[weakest] = peak;
        }
    }

    /// <summary>
    /// Suppression des non-maxima : en parcourant du plus intense au moins intense,
    /// un candidat trop proche d'un pic deja retenu est absorbe par celui-ci.
    /// </summary>
    /// <remarks>
    /// On abandonne le plus faible plutot que de moyenner les deux positions : le
    /// centre definitif de la zone est de toute facon recalcule par
    /// <see cref="ZoneBuilder"/> comme le barycentre des clics reels, bien plus juste
    /// qu'une moyenne de sommets de grille.
    /// </remarks>
    private static int Merge(ReadOnlySpan<Peak> sorted, float mergeDistance, Span<Peak> destination, int capacity)
    {
        float mergeSquared = mergeDistance * mergeDistance;
        int kept = 0;

        for (int i = 0; i < sorted.Length && kept < capacity; i++)
        {
            ref readonly Peak candidate = ref sorted[i];

            bool absorbed = false;
            for (int k = 0; k < kept; k++)
            {
                if (Grid.DistanceSquared(candidate.X, candidate.Y, destination[k].X, destination[k].Y) < mergeSquared)
                {
                    absorbed = true;
                    break;
                }
            }

            if (!absorbed)
            {
                destination[kept++] = candidate;
            }
        }

        return kept;
    }
}
