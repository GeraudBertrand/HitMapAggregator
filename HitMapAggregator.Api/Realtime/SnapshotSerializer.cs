using System.Buffers;
using System.Text.Json;
using System.Text.Json.Serialization;
using HitMapAggregator.Core;
using HitMapAggregator.Core.Models;

namespace HitMapAggregator.Api.Realtime;

/// <summary>
/// Traduit un instantane en JSON pret a partir sur le flux SSE.
/// </summary>
/// <remarks>
/// La serialisation a lieu <b>une fois par tick</b>, pas une fois par abonne : le meme
/// texte part a tout le monde. Ca borne le cout quel que soit le nombre d'overlays, et ca
/// evacue un piege — le champ de densite est adosse a un tampon reutilise au tick suivant,
/// donc le conserver pour le serialiser plus tard serait une course garantie.
/// </remarks>
public sealed class SnapshotSerializer
{
    public string Serialize(HeatmapSnapshot snapshot, SessionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(settings);

        var zones = new ZoneDto[snapshot.Zones.Count];
        for (int i = 0; i < zones.Length; i++)
        {
            Zone zone = snapshot.Zones[i];
            zones[i] = new ZoneDto(
                Round(zone.Center.X),
                Round(zone.Center.Y),
                MathF.Round(zone.Percentage, 2),
                Round(zone.SigmaA),
                Round(zone.SigmaB),
                MathF.Round(zone.Angle, 4));
        }

        var payload = new SnapshotDto(
            RenderMode: settings.RenderMode.ToString().ToLowerInvariant(),
            MajorZones: settings.MajorZones,
            BandCount: settings.BandCount,
            Visible: settings.Visible,
            TotalVotes: snapshot.TotalVotes,
            ScatteredPct: MathF.Round(snapshot.ScatteredPercentage, 2),
            Grid: new GridDto(
                snapshot.Density.Width,
                snapshot.Density.Height,
                MathF.Round(snapshot.Density.Max, 4),
                Quantize(snapshot.Density)),
            Zones: zones);

        return JsonSerializer.Serialize(payload, SnapshotJsonContext.Default.SnapshotDto);
    }

    /// <summary>
    /// Champ ramene sur 0-255, le maximum voyageant a part.
    /// </summary>
    /// <remarks>
    /// 9 216 flottants feraient 36 Ko par tick ; quantifies puis encodes en base64 ils en
    /// font 12. La perte de precision est sans consequence : la valeur ne sert qu'a
    /// choisir une couleur.
    /// </remarks>
    private static string Quantize(in DensityField field)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(Grid.CellCount);
        try
        {
            Span<byte> cells = buffer.AsSpan(0, Grid.CellCount);

            if (field.Max > 0f)
            {
                float scale = 255f / field.Max;
                float[] values = field.Values;
                for (int i = 0; i < Grid.CellCount; i++)
                {
                    cells[i] = (byte)Math.Clamp((int)(values[i] * scale + 0.5f), 0, 255);
                }
            }
            else
            {
                cells.Clear();
            }

            return Convert.ToBase64String(cells);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>Quatre decimales : bien en deca du pixel, et ca allege le payload.</summary>
    private static float Round(float value) => MathF.Round(value, 4);
}

internal sealed record SnapshotDto(
    string RenderMode,
    int MajorZones,
    int BandCount,
    bool Visible,
    int TotalVotes,
    float ScatteredPct,
    GridDto Grid,
    IReadOnlyList<ZoneDto> Zones);

internal sealed record GridDto(int W, int H, float Max, string Data);

/// <summary>
/// Une zone telle que l'overlay la recoit : aucun identifiant, l'ordre porte le sens.
/// </summary>
internal sealed record ZoneDto(float Cx, float Cy, float Pct, float Sa, float Sb, float Angle);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SnapshotDto))]
internal sealed partial class SnapshotJsonContext : JsonSerializerContext;
