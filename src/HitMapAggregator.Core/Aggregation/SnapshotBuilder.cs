using HitMapAggregator.Core.Models;

namespace HitMapAggregator.Core.Aggregation;

/// <summary>
/// Orchestration d'un tick : positions + reglages produisent un instantane.
/// </summary>
/// <remarks>
/// Sans etat et deterministe. Les memes entrees donnent strictement la meme sortie,
/// ce qui rend un tick rejouable et testable sans monter de serveur.
/// </remarks>
public static class SnapshotBuilder
{
    public static HeatmapSnapshot Build(
        ReadOnlySpan<NormalizedPoint> points,
        SessionSettings settings,
        float[] primary,
        float[] scratch)
    {
        ArgumentNullException.ThrowIfNull(settings);

        DensityField field = DensityFieldBuilder.Build(points, settings.SmoothingRadius, primary, scratch);

        // Le plafond de zones se traduit par la longueur du tampon offert au detecteur :
        // il s'arrete quand il l'a rempli, apres avoir trie par intensite decroissante.
        int maxZones = Math.Clamp(settings.MaxZones, 1, PeakDetector.HardCeiling);

        Span<Peak> peaks = stackalloc Peak[PeakDetector.HardCeiling];
        int peakCount = PeakDetector.Detect(
            field,
            settings.Threshold,
            settings.PeakMergeDistance,
            settings.MinFieldContrast,
            peaks[..maxZones]);

        Span<Zone> zoneBuffer = stackalloc Zone[PeakDetector.HardCeiling];
        int zoneCount = ZoneBuilder.Build(
            points,
            peaks[..peakCount],
            settings.EffectiveAssignmentRadius,
            settings.MinZonePercentage,
            zoneBuffer,
            out int scattered);

        IReadOnlyList<Zone> zones = zoneCount == 0
            ? Array.Empty<Zone>()
            : zoneBuffer[..zoneCount].ToArray();

        float scatteredPercentage = points.Length == 0
            ? 0f
            : scattered * 100f / points.Length;

        return new HeatmapSnapshot(field, zones, scatteredPercentage, points.Length, settings.RenderMode);
    }
}
