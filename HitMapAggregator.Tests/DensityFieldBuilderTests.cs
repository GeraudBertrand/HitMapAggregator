using System.Diagnostics;
using HitMapAggregator.Core;
using HitMapAggregator.Core.Aggregation;
using HitMapAggregator.Core.Models;

namespace HitMapAggregator.Tests;

public class DensityFieldBuilderTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(12)]
    [InlineData(32)]
    public void Blur_preserves_total_mass(int radius)
    {
        NormalizedPoint[] points = SyntheticData.Generate(Scenario.BinaryVote, 10_000);

        DensityField field = AggregationHarness.Field(points, radius);

        Assert.Equal(points.Length, AggregationHarness.TotalMass(field), points.Length * 1e-3);
    }

    [Fact]
    public void Blur_preserves_total_mass_even_against_the_borders()
    {
        // Les coins sont le cas ou une normalisation naive ferait fuir ou creer de la masse.
        NormalizedPoint[] points =
        [
            new(0f, 0f, 0L),
            new(0.9999f, 0f, 0L),
            new(0f, 0.9999f, 0L),
            new(0.9999f, 0.9999f, 0L),
        ];

        DensityField field = AggregationHarness.Field(points, smoothingRadius: 32);

        Assert.Equal(points.Length, AggregationHarness.TotalMass(field), 1e-3);
    }

    [Fact]
    public void Isolated_point_produces_a_blob_centred_on_it()
    {
        var point = new NormalizedPoint(0.40f, 0.60f, 0L);

        DensityField field = AggregationHarness.Field([point], smoothingRadius: 5);

        (int x, int y) = AggregationHarness.ArgMax(field);
        Assert.Equal((int)(point.X * Grid.Width), x);
        Assert.Equal((int)(point.Y * Grid.Height), y);
    }

    [Fact]
    public void Empty_input_produces_an_empty_field()
    {
        DensityField field = AggregationHarness.Field([]);

        Assert.Equal(0f, field.Max);
        Assert.Equal(0.0, AggregationHarness.TotalMass(field), 1e-9);
    }

    [Fact]
    [Trait("Category", "Performance")]
    public void Blur_cost_does_not_grow_with_radius()
    {
        NormalizedPoint[] points = SyntheticData.Generate(Scenario.Fragmented, 10_000);
        (float[] primary, float[] scratch) = AggregationHarness.NewBuffers();

        double small = MedianMilliseconds(points, 1, primary, scratch);
        double large = MedianMilliseconds(points, DensityFieldBuilder.MaxSmoothingRadius, primary, scratch);

        // La somme glissante fait deux operations par cellule quel que soit le rayon :
        // un facteur 32 sur le rayon ne doit pas se voir. Le seuil est large pour
        // absorber le bruit de mesure d'une machine chargee.
        Assert.True(
            large < small * 3.0,
            $"rayon 1 : {small:F3} ms, rayon {DensityFieldBuilder.MaxSmoothingRadius} : {large:F3} ms");
    }

    private static double MedianMilliseconds(NormalizedPoint[] points, int radius, float[] primary, float[] scratch)
    {
        const int Warmup = 50;
        const int Iterations = 201;

        for (int i = 0; i < Warmup; i++)
        {
            DensityFieldBuilder.Build(points, radius, primary, scratch);
        }

        var timings = new double[Iterations];
        for (int i = 0; i < Iterations; i++)
        {
            long start = Stopwatch.GetTimestamp();
            DensityFieldBuilder.Build(points, radius, primary, scratch);
            timings[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        }

        Array.Sort(timings);
        return timings[Iterations / 2];
    }
}
