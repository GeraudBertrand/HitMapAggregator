using HitMapAggregator.Core;
using HitMapAggregator.Core.Models;

namespace HitMapAggregator.Tests;

public class SnapshotBuilderTests
{
    [Fact]
    public void Ten_runs_produce_strictly_identical_snapshots()
    {
        NormalizedPoint[] points = SyntheticData.Generate(Scenario.Fragmented, 20_000);
        var settings = new SessionSettings();

        HeatmapSnapshot reference = AggregationHarness.Snapshot(points, settings);

        for (int run = 0; run < 9; run++)
        {
            HeatmapSnapshot other = AggregationHarness.Snapshot(points, settings);

            Assert.Equal(reference.TotalVotes, other.TotalVotes);
            Assert.Equal(reference.ScatteredPercentage, other.ScatteredPercentage);
            Assert.Equal(reference.RenderMode, other.RenderMode);
            Assert.Equal(reference.Zones, other.Zones);
            Assert.Equal(reference.Density.Max, other.Density.Max);
            Assert.True(
                reference.Density.Values.AsSpan(0, Grid.CellCount)
                    .SequenceEqual(other.Density.Values.AsSpan(0, Grid.CellCount)),
                $"le champ de densite differe a l'execution {run + 2}");
        }
    }

    [Fact]
    public void No_point_produces_an_empty_snapshot()
    {
        HeatmapSnapshot snapshot = AggregationHarness.Snapshot([]);

        Assert.Equal(0, snapshot.TotalVotes);
        Assert.Empty(snapshot.Zones);
        Assert.Equal(0f, snapshot.ScatteredPercentage);
        Assert.Equal(0f, snapshot.Density.Max);
    }

    [Fact]
    public void A_single_point_produces_one_zone_holding_the_whole_vote()
    {
        var point = new NormalizedPoint(0.33f, 0.66f, 0L);

        HeatmapSnapshot snapshot = AggregationHarness.Snapshot([point]);

        Assert.Equal(1, snapshot.TotalVotes);
        Zone zone = Assert.Single(snapshot.Zones);
        Assert.Equal(100f, zone.Percentage, 1e-3f);
        Assert.Equal(0f, snapshot.ScatteredPercentage, 1e-3f);
        Assert.Equal(point.X, zone.Center.X, 1e-3f);
        Assert.Equal(point.Y, zone.Center.Y, 1e-3f);
    }

    [Fact]
    public void Fifty_thousand_points_aggregate_cleanly()
    {
        NormalizedPoint[] points = SyntheticData.Generate(Scenario.BinaryVote, 50_000);

        HeatmapSnapshot snapshot = AggregationHarness.Snapshot(points);

        Assert.Equal(50_000, snapshot.TotalVotes);
        Assert.Equal(2, snapshot.Zones.Count);

        double total = snapshot.ScatteredPercentage + snapshot.Zones.Sum(z => (double)z.Percentage);
        Assert.Equal(100.0, total, 0.1);
    }

    [Theory]
    [InlineData(RenderMode.Ellipse)]
    [InlineData(RenderMode.Contour)]
    [InlineData(RenderMode.Both)]
    public void Render_mode_is_carried_through_without_touching_the_computation(RenderMode mode)
    {
        // Regle d'architecture : les deux rendus sont toujours calcules, le mode est un
        // drapeau transporte. Changer de mode ne doit rien changer aux nombres.
        NormalizedPoint[] points = SyntheticData.Generate(Scenario.BinaryVote, 5_000);

        HeatmapSnapshot reference = AggregationHarness.Snapshot(points, new SessionSettings { RenderMode = RenderMode.Both });
        HeatmapSnapshot snapshot = AggregationHarness.Snapshot(points, new SessionSettings { RenderMode = mode });

        Assert.Equal(mode, snapshot.RenderMode);
        Assert.Equal(reference.Zones, snapshot.Zones);
        Assert.Equal(reference.ScatteredPercentage, snapshot.ScatteredPercentage);
        Assert.Equal(reference.Density.Max, snapshot.Density.Max);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(7)]
    public void Major_zone_count_is_carried_through_without_touching_the_computation(int major)
    {
        // Comme RenderMode : un drapeau d'affichage, transporte mais jamais interprete.
        NormalizedPoint[] points = SyntheticData.Generate(Scenario.Fragmented, 5_000);

        HeatmapSnapshot reference = AggregationHarness.Snapshot(points, new SessionSettings());
        HeatmapSnapshot snapshot = AggregationHarness.Snapshot(points, new SessionSettings { MajorZones = major });

        Assert.Equal(reference.Zones, snapshot.Zones);
        Assert.Equal(reference.ScatteredPercentage, snapshot.ScatteredPercentage);
    }

    [Fact]
    public void Changing_the_smoothing_is_retroactive_on_the_same_positions()
    {
        // Corollaire du stockage des positions brutes : un reglage modifie relit tout
        // l'historique, il ne s'applique pas seulement aux clics a venir.
        NormalizedPoint[] points = SyntheticData.Generate(Scenario.Fragmented, 20_000);

        HeatmapSnapshot sharp = AggregationHarness.Snapshot(points, new SessionSettings { SmoothingRadius = 2 });
        HeatmapSnapshot blurred = AggregationHarness.Snapshot(points, new SessionSettings { SmoothingRadius = 20 });

        Assert.Equal(sharp.TotalVotes, blurred.TotalVotes);
        Assert.True(blurred.Density.Max < sharp.Density.Max);
    }
}
