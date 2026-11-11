using HitMapAggregator.Core.Aggregation;
using HitMapAggregator.Core.Models;

namespace HitMapAggregator.Tests;

public class ZoneBuilderTests
{
    [Theory]
    [InlineData(Scenario.BinaryVote)]
    [InlineData(Scenario.Consensus)]
    [InlineData(Scenario.Fragmented)]
    [InlineData(Scenario.Elongated)]
    [InlineData(Scenario.LShape)]
    [InlineData(Scenario.Concentric)]
    public void Percentages_and_scattered_add_up_to_one_hundred(Scenario scenario)
    {
        HeatmapSnapshot snapshot = AggregationHarness.Snapshot(SyntheticData.Generate(scenario, 20_000));

        double total = snapshot.ScatteredPercentage;
        foreach (Zone zone in snapshot.Zones)
        {
            total += zone.Percentage;
        }

        Assert.Equal(100.0, total, 0.1);
    }

    [Fact]
    public void Elongated_cluster_yields_an_elongated_zone()
    {
        HeatmapSnapshot snapshot = AggregationHarness.Snapshot(SyntheticData.Generate(Scenario.Elongated, 20_000));

        float ratio = snapshot.Zones.Max(z => z.SigmaA / z.SigmaB);

        Assert.True(ratio > 3f, $"rapport sigmaA/sigmaB maximal : {ratio:F2}");
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(30f)]
    [InlineData(60f)]
    [InlineData(90f)]
    [InlineData(135f)]
    public void Recovers_the_injected_orientation(float degrees)
    {
        float radians = degrees * MathF.PI / 180f;
        NormalizedPoint[] points = SyntheticData.SingleCluster(0.5f, 0.5f, 0.060f, 0.015f, radians, 20_000);

        // Pic pose a la main au centre, rayon d'affectation large : on isole la
        // restitution de forme, sans que la troncature du disque n'interfere.
        ReadOnlySpan<Peak> peaks = [new Peak(64, 36, 0.5f, 0.5f, 1f)];
        Span<Zone> zones = stackalloc Zone[1];

        int count = ZoneBuilder.Build(points, peaks, assignmentRadius: 2f, minZonePercentage: 0f, zones, out _);

        Assert.Equal(1, count);
        float error = AggregationHarness.AngleDifference(zones[0].Angle, radians) * 180f / MathF.PI;
        Assert.True(error < 5f, $"orientation injectee {degrees}deg, restituee a {error:F2}deg pres");
    }

    [Fact]
    public void Angle_always_lands_in_the_canonical_half_turn()
    {
        foreach (Scenario scenario in SyntheticData.All)
        {
            HeatmapSnapshot snapshot = AggregationHarness.Snapshot(SyntheticData.Generate(scenario, 20_000));
            foreach (Zone zone in snapshot.Zones)
            {
                Assert.InRange(zone.Angle, 0f, MathF.PI);
            }
        }
    }

    [Fact]
    public void Zones_below_the_minimum_share_hand_their_points_to_the_scattered_count()
    {
        NormalizedPoint[] points = SyntheticData.Generate(Scenario.Fragmented, 20_000);

        HeatmapSnapshot permissive = AggregationHarness.Snapshot(points, new SessionSettings { MinZonePercentage = 0f });
        HeatmapSnapshot strict = AggregationHarness.Snapshot(points, new SessionSettings { MinZonePercentage = 16f });

        Assert.True(strict.Zones.Count < permissive.Zones.Count);
        Assert.True(strict.ScatteredPercentage > permissive.ScatteredPercentage);

        double total = strict.ScatteredPercentage + strict.Zones.Sum(z => (double)z.Percentage);
        Assert.Equal(100.0, total, 0.1);
    }

    [Fact]
    public void No_point_produces_no_zone()
    {
        ReadOnlySpan<Peak> peaks = [new Peak(64, 36, 0.5f, 0.5f, 1f)];
        Span<Zone> zones = stackalloc Zone[1];

        int count = ZoneBuilder.Build([], peaks, 0.13f, 0f, zones, out int scattered);

        Assert.Equal(0, count);
        Assert.Equal(0, scattered);
    }

    [Fact]
    public void No_peak_sends_every_point_to_the_scattered_count()
    {
        NormalizedPoint[] points = SyntheticData.Generate(Scenario.BinaryVote, 500);
        Span<Zone> zones = stackalloc Zone[PeakDetector.HardCeiling];

        int count = ZoneBuilder.Build(points, [], 0.13f, 0f, zones, out int scattered);

        Assert.Equal(0, count);
        Assert.Equal(points.Length, scattered);
    }

    [Fact]
    public void A_single_point_yields_a_degenerate_but_valid_zone()
    {
        ReadOnlySpan<NormalizedPoint> points = [new NormalizedPoint(0.5f, 0.5f, 0L)];
        ReadOnlySpan<Peak> peaks = [new Peak(64, 36, 0.5f, 0.5f, 1f)];
        Span<Zone> zones = stackalloc Zone[1];

        int count = ZoneBuilder.Build(points, peaks, 0.13f, 0f, zones, out int scattered);

        Assert.Equal(1, count);
        Assert.Equal(0, scattered);
        Assert.Equal(1, zones[0].PointCount);
        Assert.Equal(100f, zones[0].Percentage, 1e-3f);
        Assert.True(float.IsFinite(zones[0].SigmaA) && zones[0].SigmaA >= 0f);
        Assert.True(float.IsFinite(zones[0].SigmaB) && zones[0].SigmaB >= 0f);
        Assert.True(float.IsFinite(zones[0].Angle));
    }

    [Fact]
    public void Identical_points_yield_a_degenerate_but_valid_zone()
    {
        var points = new NormalizedPoint[500];
        Array.Fill(points, new NormalizedPoint(0.5f, 0.5f, 0L));

        ReadOnlySpan<Peak> peaks = [new Peak(64, 36, 0.5f, 0.5f, 1f)];
        Span<Zone> zones = stackalloc Zone[1];

        int count = ZoneBuilder.Build(points, peaks, 0.13f, 0f, zones, out _);

        Assert.Equal(1, count);
        Assert.Equal(500, zones[0].PointCount);
        Assert.True(float.IsFinite(zones[0].SigmaA));
        Assert.True(float.IsFinite(zones[0].SigmaB));
        Assert.True(float.IsFinite(zones[0].Angle));
        Assert.True(zones[0].SigmaA >= zones[0].SigmaB);
    }

    [Fact]
    public void Collinear_points_yield_a_valid_axis_aligned_zone()
    {
        // Variance nulle sur un axe : c'est la branche ou atan2 serait indeterminee.
        var points = new NormalizedPoint[500];
        for (int i = 0; i < points.Length; i++)
        {
            points[i] = new NormalizedPoint(0.40f + i * 0.0002f, 0.50f, 0L);
        }

        ReadOnlySpan<Peak> peaks = [new Peak(64, 36, 0.45f, 0.5f, 1f)];
        Span<Zone> zones = stackalloc Zone[1];

        int count = ZoneBuilder.Build(points, peaks, 2f, 0f, zones, out _);

        Assert.Equal(1, count);
        Assert.True(float.IsFinite(zones[0].Angle));
        Assert.Equal(0f, zones[0].Angle, 1e-3f);
        Assert.True(zones[0].SigmaA > zones[0].SigmaB);
    }

    [Theory]
    [InlineData(Scenario.BinaryVote)]
    [InlineData(Scenario.Consensus)]
    [InlineData(Scenario.Fragmented)]
    [InlineData(Scenario.Elongated)]
    [InlineData(Scenario.LShape)]
    [InlineData(Scenario.Concentric)]
    public void Zones_come_out_sorted_so_a_dumb_consumer_can_just_read_the_first(Scenario scenario)
    {
        // Contrat envers l'overlay : il n'a rien a retenir ni a recalculer. L'indice 0
        // est la zone majeure, la tranche [0, MajorZones) est le groupe de tete.
        HeatmapSnapshot snapshot = AggregationHarness.Snapshot(SyntheticData.Generate(scenario, 20_000));

        for (int i = 1; i < snapshot.Zones.Count; i++)
        {
            Assert.True(
                snapshot.Zones[i - 1].PointCount >= snapshot.Zones[i].PointCount,
                $"zone {i - 1} ({snapshot.Zones[i - 1].PointCount}) devrait preceder la zone {i} ({snapshot.Zones[i].PointCount})");

            Assert.True(
                snapshot.Zones[i - 1].Percentage >= snapshot.Zones[i].Percentage,
                "l'ordre des pourcentages doit suivre celui des effectifs");
        }

        if (snapshot.Zones.Count > 0)
        {
            Assert.Equal(snapshot.Zones.Max(z => z.Percentage), snapshot.Zones[0].Percentage);
        }
    }
}
