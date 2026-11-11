namespace HitMapAggregator.Visualizer;

/// <summary>
/// Rampe de couleur du champ de densite, partagee par l'export SVG et la fenetre :
/// les deux doivent montrer la meme chose.
/// </summary>
internal static class DensityPalette
{
    public static (int R, int G, int B) Sample(float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return t switch
        {
            < 0.25f => Lerp((30, 60, 160), (0, 170, 200), t / 0.25f),
            < 0.50f => Lerp((0, 170, 200), (40, 190, 90), (t - 0.25f) / 0.25f),
            < 0.75f => Lerp((40, 190, 90), (230, 200, 40), (t - 0.50f) / 0.25f),
            _ => Lerp((230, 200, 40), (220, 50, 40), (t - 0.75f) / 0.25f),
        };
    }

    private static (int R, int G, int B) Lerp((int R, int G, int B) a, (int R, int G, int B) b, float t) =>
        ((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
}
