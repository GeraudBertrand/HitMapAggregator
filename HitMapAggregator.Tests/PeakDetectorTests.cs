using HitMapAggregator.Core.Aggregation;
using HitMapAggregator.Core.Models;

namespace HitMapAggregator.Tests;

public class PeakDetectorTests
{
    [Fact]
    public void Binary_vote_yields_two_peaks()
    {
        Peak[] peaks = AggregationHarness.Peaks(SyntheticData.Generate(Scenario.BinaryVote, 20_000));

        Assert.Equal(2, peaks.Length);
    }

    [Fact]
    public void Consensus_yields_a_single_peak()
    {
        Peak[] peaks = AggregationHarness.Peaks(SyntheticData.Generate(Scenario.Consensus, 20_000));

        Assert.Single(peaks);
    }

    [Fact]
    public void Fragmented_yields_six_peaks()
    {
        Peak[] peaks = AggregationHarness.Peaks(SyntheticData.Generate(Scenario.Fragmented, 20_000));

        Assert.Equal(6, peaks.Length);
    }

    [Fact]
    public void Uniform_noise_yields_no_more_than_one_peak()
    {
        Peak[] peaks = AggregationHarness.Peaks(SyntheticData.UniformNoise(20_000));

        Assert.True(peaks.Length <= 1, $"{peaks.Length} pics detectes sur du bruit uniforme");
    }

    [Fact]
    public void Peaks_come_out_sorted_by_decreasing_intensity()
    {
        Peak[] peaks = AggregationHarness.Peaks(SyntheticData.Generate(Scenario.Fragmented, 20_000));

        for (int i = 1; i < peaks.Length; i++)
        {
            Assert.True(peaks[i - 1].Intensity >= peaks[i].Intensity);
        }
    }

    [Fact]
    public void Ten_equal_zones_are_all_detected()
    {
        // Le cas qui motivait de lever le plafond fixe : dix cibles egales sur une carte.
        // Avant, les trois plus faibles etaient coupees et leurs clics comptes comme
        // disperses — ce qui presentait trois amas serres comme du bruit diffus.
        Peak[] peaks = AggregationHarness.Peaks(SyntheticData.EqualZones(10, 20_000));

        Assert.Equal(10, peaks.Length);
    }

    [Fact]
    public void Max_zones_setting_caps_the_detection()
    {
        NormalizedPoint[] points = SyntheticData.EqualZones(10, 20_000);

        Peak[] peaks = AggregationHarness.Peaks(points, new SessionSettings { MaxZones = 4 });

        Assert.Equal(4, peaks.Length);
    }

    [Fact]
    public void Max_zones_is_clamped_to_the_structural_ceiling()
    {
        NormalizedPoint[] points = SyntheticData.EqualZones(10, 20_000);

        Peak[] peaks = AggregationHarness.Peaks(points, new SessionSettings { MaxZones = 10_000 });

        Assert.True(peaks.Length <= PeakDetector.HardCeiling);
        Assert.Equal(10, peaks.Length);
    }

    [Fact]
    public void Twenty_zones_are_limited_by_the_smoothing_radius_not_by_the_cap()
    {
        NormalizedPoint[] points = SyntheticData.EqualZones(20, 20_000);

        // Avec le lissage par defaut, vingt amas se fondent les uns dans les autres : le
        // champ perd son contraste (2,3 contre un seuil de 3) et la porte le rejette en
        // bloc. Monter MaxZones n'y changerait rien — le detecteur ne peut pas trouver
        // des pics dans un champ que le flou a deja aplani.
        Assert.Empty(AggregationHarness.Peaks(points));

        // Le vrai levier est le rayon de lissage.
        Peak[] mapMode = AggregationHarness.Peaks(points, new SessionSettings
        {
            SmoothingRadius = 2,
            PeakMergeDistance = 0.09f,
            MaxZones = 24,
        });

        Assert.Equal(20, mapMode.Length);
    }

    [Fact]
    public void A_flat_field_is_rejected_by_the_contrast_gate()
    {
        NormalizedPoint[] noise = SyntheticData.UniformNoise(20_000);

        Assert.Empty(AggregationHarness.Peaks(noise));

        // Le garde-fou est bien ce qui rejette : desarme, le champ plat produit des zones.
        Peak[] ungated = AggregationHarness.Peaks(noise, new SessionSettings { MinFieldContrast = 1f });
        Assert.NotEmpty(ungated);
    }

    [Fact]
    public void Empty_field_yields_no_peak()
    {
        Assert.Empty(AggregationHarness.Peaks([]));
    }
}
