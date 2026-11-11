namespace HitMapAggregator.Core;

/// <summary>
/// Geometrie de la grille de densite et conversion entre l'espace normalise
/// et l'espace corrige de l'aspect.
/// </summary>
/// <remarks>
/// Les positions sont normalisees dans [0,1]x[0,1] : une unite en X couvre toute
/// la largeur de l'ecran, une unite en Y toute sa hauteur. Sur un ecran 16:9 ces
/// deux unites n'ont donc pas la meme taille physique, et une distance euclidienne
/// calculee directement sur (x, y) serait anisotrope : deux clics separes de 0,1 en
/// Y paraitraient plus proches que deux clics separes de 0,1 en X.
///
/// L'espace <i>corrige</i> multiplie Y par <see cref="AspectY"/>. Il est isotrope :
/// une distance y vaut partout la meme chose a l'ecran, et elle s'exprime en
/// fractions de la LARGEUR. C'est dans cet espace que se mesurent toutes les
/// distances (fusion de pics, affectation des points) et que se calcule la
/// covariance des zones.
/// </remarks>
public static class Grid
{
    public const int Width = 128;
    public const int Height = 72;
    public const int CellCount = Width * Height;

    /// <summary>Facteur d'echelle applique a Y pour passer en espace corrige (0,5625 en 16:9).</summary>
    public const float AspectY = (float)Height / Width;

    /// <summary>Distance au carre entre deux positions normalisees, mesuree en espace corrige.</summary>
    public static float DistanceSquared(float ax, float ay, float bx, float by)
    {
        float dx = ax - bx;
        float dy = (ay - by) * AspectY;
        return dx * dx + dy * dy;
    }
}
