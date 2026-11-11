namespace HitMapAggregator.Core.Aggregation;

/// <summary>
/// Un maximum local du champ de densite : le germe d'une zone, avant que
/// <see cref="ZoneBuilder"/> ne lui affecte des clics.
/// </summary>
/// <param name="CellX">Colonne de grille.</param>
/// <param name="CellY">Ligne de grille.</param>
/// <param name="X">Abscisse normalisee du centre de la cellule.</param>
/// <param name="Y">Ordonnee normalisee du centre de la cellule.</param>
/// <param name="Intensity">Densite lissee au sommet.</param>
public readonly record struct Peak(int CellX, int CellY, float X, float Y, float Intensity);
