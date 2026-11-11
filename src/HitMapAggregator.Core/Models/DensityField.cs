namespace HitMapAggregator.Core.Models;

/// <summary>
/// Champ de densite lisse, en ligne par ligne (index = y * Width + x).
/// </summary>
/// <remarks>
/// C'est une vue sur un tampon <b>appartenant a l'appelant</b> et reutilise a chaque
/// tick : le champ n'est valide que jusqu'au tick suivant. Le type est un struct pour
/// que fabriquer un champ ne coute aucune allocation.
/// </remarks>
public readonly record struct DensityField(float[] Values, int Width, int Height, float Max)
{
    /// <summary>
    /// Echantillonne le champ en coordonnees normalisees [0,1]x[0,1], par interpolation
    /// bilineaire entre les centres de cellules, avec prolongement des bords.
    /// </summary>
    public float SampleBilinear(float u, float v)
    {
        // Le centre de la cellule i est en (i + 0,5) / N : on retranche le demi-pixel
        // pour retomber sur une coordonnee de grille ou les entiers sont des centres.
        float gx = u * Width - 0.5f;
        float gy = v * Height - 0.5f;

        int x0 = (int)MathF.Floor(gx);
        int y0 = (int)MathF.Floor(gy);
        float fx = gx - x0;
        float fy = gy - y0;

        int xa = Math.Clamp(x0, 0, Width - 1);
        int xb = Math.Clamp(x0 + 1, 0, Width - 1);
        int ya = Math.Clamp(y0, 0, Height - 1);
        int yb = Math.Clamp(y0 + 1, 0, Height - 1);

        float[] values = Values;
        int rowA = ya * Width;
        int rowB = yb * Width;

        float top = values[rowA + xa] + (values[rowA + xb] - values[rowA + xa]) * fx;
        float bottom = values[rowB + xa] + (values[rowB + xb] - values[rowB + xa]) * fx;
        return top + (bottom - top) * fy;
    }
}
