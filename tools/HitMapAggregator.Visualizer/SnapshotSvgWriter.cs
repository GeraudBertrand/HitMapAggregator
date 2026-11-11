using System.Globalization;
using System.Text;
using HitMapAggregator.Core;
using HitMapAggregator.Core.Models;

namespace HitMapAggregator.Visualizer;

/// <summary>
/// Rend un instantane en SVG, pour verifier a l'oeil que la detection est juste.
/// </summary>
/// <remarks>
/// Le SVG evite toute dependance de rendu, et l'espace corrige de l'aspect s'y projette
/// par une homothetie unique : une cellule de grille fait 10x10 px, et les DEUX sigmas
/// se multiplient par la largeur. C'est ce qui permet de dessiner une ellipse pivotee
/// sans la deformer.
/// </remarks>
internal static class SnapshotSvgWriter
{
    private const int CanvasWidth = 1280;
    private const int CanvasHeight = 720;
    private const int Cell = CanvasWidth / Grid.Width;

    /// <summary>Facteur applique aux ecarts-types pour tracer l'ellipse.</summary>
    private const float SigmaScale = 2f;

    private const int MaxPlottedPoints = 3_000;

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string Render(string title, ReadOnlySpan<NormalizedPoint> points, HeatmapSnapshot snapshot)
    {
        var svg = new StringBuilder(1 << 20);

        svg.Append(Inv, $"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 {CanvasWidth} {CanvasHeight}\" width=\"{CanvasWidth}\" height=\"{CanvasHeight}\">");
        svg.Append(Inv, $"<rect width=\"{CanvasWidth}\" height=\"{CanvasHeight}\" fill=\"#0d1117\"/>");

        AppendDensity(svg, snapshot.Density);
        AppendPoints(svg, points);
        AppendZones(svg, snapshot);
        AppendCaption(svg, title, snapshot);

        svg.Append("</svg>");
        return svg.ToString();
    }

    private static void AppendDensity(StringBuilder svg, in DensityField field)
    {
        if (field.Max <= 0f)
        {
            return;
        }

        svg.Append("<g>");
        for (int y = 0; y < field.Height; y++)
        {
            for (int x = 0; x < field.Width; x++)
            {
                float t = field.Values[y * field.Width + x] / field.Max;
                if (t < 0.02f)
                {
                    continue;
                }

                (int r, int g, int b) = DensityPalette.Sample(t);
                svg.Append(Inv, $"<rect x=\"{x * Cell}\" y=\"{y * Cell}\" width=\"{Cell}\" height=\"{Cell}\" fill=\"rgb({r},{g},{b})\" fill-opacity=\"{0.15f + 0.75f * t:F3}\"/>");
            }
        }

        svg.Append("</g>");
    }

    private static void AppendPoints(StringBuilder svg, ReadOnlySpan<NormalizedPoint> points)
    {
        if (points.Length == 0)
        {
            return;
        }

        int step = Math.Max(1, points.Length / MaxPlottedPoints);
        svg.Append("<g fill=\"#ffffff\" fill-opacity=\"0.30\">");
        for (int i = 0; i < points.Length; i += step)
        {
            svg.Append(Inv, $"<circle cx=\"{points[i].X * CanvasWidth:F1}\" cy=\"{points[i].Y * CanvasHeight:F1}\" r=\"1.2\"/>");
        }

        svg.Append("</g>");
    }

    private static void AppendZones(StringBuilder svg, HeatmapSnapshot snapshot)
    {
        for (int i = 0; i < snapshot.Zones.Count; i++)
        {
            Zone zone = snapshot.Zones[i];

            float cx = zone.Center.X * CanvasWidth;
            float cy = zone.Center.Y * CanvasHeight;

            // Les deux rayons passent par la LARGEUR : les sigmas sont dans la meme
            // echelle isotrope. Multiplier SigmaB par la hauteur deformerait l'ellipse.
            float rx = MathF.Max(3f, zone.SigmaA * SigmaScale * CanvasWidth);
            float ry = MathF.Max(3f, zone.SigmaB * SigmaScale * CanvasWidth);

            // L'espace corrige a deja Y vers le bas, comme le SVG : l'angle se reporte tel quel.
            float degrees = zone.Angle * 180f / MathF.PI;

            svg.Append(Inv, $"<ellipse cx=\"{cx:F1}\" cy=\"{cy:F1}\" rx=\"{rx:F1}\" ry=\"{ry:F1}\" transform=\"rotate({degrees:F2} {cx:F1} {cy:F1})\" fill=\"none\" stroke=\"#ffffff\" stroke-width=\"2.5\"/>");
            svg.Append(Inv, $"<circle cx=\"{cx:F1}\" cy=\"{cy:F1}\" r=\"3\" fill=\"#ffffff\"/>");
            svg.Append(Inv, $"<text x=\"{cx:F1}\" y=\"{cy - ry - 10f:F1}\" fill=\"#ffffff\" stroke=\"#0d1117\" stroke-width=\"3\" paint-order=\"stroke\" font-family=\"monospace\" font-size=\"22\" text-anchor=\"middle\">{zone.Percentage:F1} %</text>");
        }
    }

    private static void AppendCaption(StringBuilder svg, string title, HeatmapSnapshot snapshot)
    {
        svg.Append(Inv, $"<text x=\"16\" y=\"32\" fill=\"#ffffff\" font-family=\"monospace\" font-size=\"22\">{title}</text>");
        svg.Append(Inv, $"<text x=\"16\" y=\"{CanvasHeight - 40}\" fill=\"#c9d1d9\" font-family=\"monospace\" font-size=\"18\">{snapshot.TotalVotes} clics, {snapshot.Zones.Count} zone(s), disperses {snapshot.ScatteredPercentage:F1} %</text>");
        svg.Append(Inv, $"<text x=\"16\" y=\"{CanvasHeight - 16}\" fill=\"#8b949e\" font-family=\"monospace\" font-size=\"15\">ellipses a k={SigmaScale:F0} ecarts-types, densite {Grid.Width}x{Grid.Height}, max {snapshot.Density.Max:F2}</text>");
    }

}
