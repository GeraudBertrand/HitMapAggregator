using HitMapAggregator.Core;
using HitMapAggregator.Core.Aggregation;
using HitMapAggregator.Core.Models;

namespace HitMapAggregator.Tests;

/// <summary>
/// Raccourcis pour lancer un etage de l'agregation sans avoir a gerer les tampons.
/// Chaque appel alloue ses propres tampons : deux resultats sont comparables entre eux.
/// </summary>
internal static class AggregationHarness
{
    public static (float[] Primary, float[] Scratch) NewBuffers() =>
        (new float[Grid.CellCount], new float[Grid.CellCount]);

    public static DensityField Field(ReadOnlySpan<NormalizedPoint> points, int smoothingRadius = 4)
    {
        (float[] primary, float[] scratch) = NewBuffers();
        return DensityFieldBuilder.Build(points, smoothingRadius, primary, scratch);
    }

    public static Peak[] Peaks(ReadOnlySpan<NormalizedPoint> points, SessionSettings? settings = null)
    {
        settings ??= new SessionSettings();
        DensityField field = Field(points, settings.SmoothingRadius);

        int maxZones = Math.Clamp(settings.MaxZones, 1, PeakDetector.HardCeiling);
        Span<Peak> buffer = stackalloc Peak[PeakDetector.HardCeiling];
        int count = PeakDetector.Detect(
            field,
            settings.Threshold,
            settings.PeakMergeDistance,
            settings.MinFieldContrast,
            buffer[..maxZones]);
        return buffer[..count].ToArray();
    }

    public static HeatmapSnapshot Snapshot(ReadOnlySpan<NormalizedPoint> points, SessionSettings? settings = null)
    {
        (float[] primary, float[] scratch) = NewBuffers();
        return SnapshotBuilder.Build(points, settings ?? new SessionSettings(), primary, scratch);
    }

    public static double TotalMass(in DensityField field)
    {
        double sum = 0.0;
        float[] values = field.Values;
        for (int i = 0; i < Grid.CellCount; i++)
        {
            sum += values[i];
        }

        return sum;
    }

    public static (int X, int Y) ArgMax(in DensityField field)
    {
        float[] values = field.Values;
        int best = 0;
        for (int i = 1; i < Grid.CellCount; i++)
        {
            if (values[i] > values[best])
            {
                best = i;
            }
        }

        return (best % Grid.Width, best / Grid.Width);
    }

    /// <summary>Ecart entre deux orientations d'axe non oriente, dans [0, pi/2].</summary>
    public static float AngleDifference(float a, float b)
    {
        float delta = MathF.Abs(a - b) % MathF.PI;
        return MathF.Min(delta, MathF.PI - delta);
    }
}
