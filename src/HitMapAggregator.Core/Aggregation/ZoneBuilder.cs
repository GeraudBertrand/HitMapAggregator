using System.Numerics;
using HitMapAggregator.Core.Models;

namespace HitMapAggregator.Core.Aggregation;

/// <summary>
/// Affecte les clics aux pics et en deduit l'ellipse de covariance de chaque zone.
/// </summary>
public static class ZoneBuilder
{
    /// <summary>Plancher des valeurs propres : evite une racine de negatif et une ellipse degeneree en segment.</summary>
    private const double Epsilon = 1e-12;

    private static readonly Comparison<Zone> ByPointCountDescending = static (a, b) =>
    {
        int byCount = b.PointCount.CompareTo(a.PointCount);
        if (byCount != 0)
        {
            return byCount;
        }

        int byX = a.Center.X.CompareTo(b.Center.X);
        return byX != 0 ? byX : a.Center.Y.CompareTo(b.Center.Y);
    };

    /// <summary>
    /// Construit les zones. Une seule passe sur les clics, quel que soit le nombre de pics.
    /// </summary>
    /// <param name="scatteredCount">
    /// Clics n'appartenant a aucune zone retenue : hors rayon d'affectation, ou membres
    /// d'une zone abandonnee pour cause d'effectif trop faible.
    /// </param>
    /// <returns>Le nombre de zones ecrites dans <paramref name="destination"/>.</returns>
    public static int Build(
        ReadOnlySpan<NormalizedPoint> points,
        ReadOnlySpan<Peak> peaks,
        float assignmentRadius,
        float minZonePercentage,
        Span<Zone> destination,
        out int scatteredCount)
    {
        int peakCount = Math.Min(peaks.Length, Math.Min(PeakDetector.HardCeiling, destination.Length));
        if (peakCount == 0 || points.Length == 0)
        {
            scatteredCount = points.Length;
            return 0;
        }

        Span<int> counts = stackalloc int[PeakDetector.HardCeiling];
        Span<double> sumX = stackalloc double[PeakDetector.HardCeiling];
        Span<double> sumY = stackalloc double[PeakDetector.HardCeiling];
        Span<double> sumXX = stackalloc double[PeakDetector.HardCeiling];
        Span<double> sumYY = stackalloc double[PeakDetector.HardCeiling];
        Span<double> sumXY = stackalloc double[PeakDetector.HardCeiling];

        int scattered = Accumulate(
            points, peaks[..peakCount], assignmentRadius,
            counts, sumX, sumY, sumXX, sumYY, sumXY);

        int zoneCount = 0;
        for (int k = 0; k < peakCount; k++)
        {
            int count = counts[k];
            if (count == 0)
            {
                continue;
            }

            float percentage = count * 100f / points.Length;
            if (percentage < minZonePercentage)
            {
                // La zone disparait mais ses clics existent toujours : ils basculent chez
                // les disperses, sinon le total ne ferait plus 100 %.
                scattered += count;
                continue;
            }

            destination[zoneCount++] = BuildZone(
                count, percentage,
                sumX[k], sumY[k], sumXX[k], sumYY[k], sumXY[k]);
        }

        destination[..zoneCount].Sort(ByPointCountDescending);
        scatteredCount = scattered;
        return zoneCount;
    }

    /// <summary>
    /// Passe unique : chaque clic rejoint le pic le plus proche et alimente les cinq
    /// sommes dont la covariance se deduit analytiquement.
    /// </summary>
    private static int Accumulate(
        ReadOnlySpan<NormalizedPoint> points,
        ReadOnlySpan<Peak> peaks,
        float assignmentRadius,
        Span<int> counts,
        Span<double> sumX,
        Span<double> sumY,
        Span<double> sumXX,
        Span<double> sumYY,
        Span<double> sumXY)
    {
        float radiusSquared = assignmentRadius * assignmentRadius;
        int scattered = 0;

        for (int i = 0; i < points.Length; i++)
        {
            ref readonly NormalizedPoint p = ref points[i];

            int nearest = -1;
            float nearestSquared = float.MaxValue;
            for (int k = 0; k < peaks.Length; k++)
            {
                float distance = Grid.DistanceSquared(p.X, p.Y, peaks[k].X, peaks[k].Y);
                if (distance < nearestSquared)
                {
                    nearestSquared = distance;
                    nearest = k;
                }
            }

            if (nearest < 0 || nearestSquared > radiusSquared)
            {
                scattered++;
                continue;
            }

            // Accumulation en espace corrige : c'est la seule ou l'ellipse obtenue
            // correspond a ce qu'on voit. En double, parce que Sxx/n - mx^2 est une
            // soustraction de deux grands nombres presque egaux.
            double x = p.X;
            double y = p.Y * Grid.AspectY;

            counts[nearest]++;
            sumX[nearest] += x;
            sumY[nearest] += y;
            sumXX[nearest] += x * x;
            sumYY[nearest] += y * y;
            sumXY[nearest] += x * y;
        }

        return scattered;
    }

    private static Zone BuildZone(
        int count,
        float percentage,
        double sumX,
        double sumY,
        double sumXX,
        double sumYY,
        double sumXY)
    {
        double inverse = 1.0 / count;
        double meanX = sumX * inverse;
        double meanY = sumY * inverse;

        // Le clamp absorbe les variances legerement negatives que la cancellation
        // produit quand tous les points sont confondus.
        double varXX = Math.Max(0.0, sumXX * inverse - meanX * meanX);
        double varYY = Math.Max(0.0, sumYY * inverse - meanY * meanY);
        double varXY = sumXY * inverse - meanX * meanY;

        double trace = varXX + varYY;
        double determinant = varXX * varYY - varXY * varXY;
        double spread = Math.Sqrt(Math.Max(0.0, trace * trace / 4.0 - determinant));

        double lambdaA = Math.Max(Epsilon, trace / 2.0 + spread);
        double lambdaB = Math.Max(Epsilon, trace / 2.0 - spread);

        double angle;
        if (Math.Abs(varXY) < Epsilon)
        {
            // Axes deja alignes : atan2 serait indetermine.
            angle = varXX >= varYY ? 0.0 : Math.PI / 2.0;
        }
        else
        {
            angle = Math.Atan2(lambdaA - varXX, varXY);
        }

        // Un axe n'est pas oriente : theta et theta+pi decrivent la meme ellipse. On
        // replie dans [0, pi) pour que la valeur soit comparable d'un tick a l'autre.
        angle %= Math.PI;
        if (angle < 0.0)
        {
            angle += Math.PI;
        }

        return new Zone(
            new Vector2((float)meanX, (float)(meanY / Grid.AspectY)),
            (float)Math.Sqrt(lambdaA),
            (float)Math.Sqrt(lambdaB),
            (float)angle,
            count,
            percentage);
    }
}
