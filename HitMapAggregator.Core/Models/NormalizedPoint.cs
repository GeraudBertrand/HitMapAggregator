namespace HitMapAggregator.Core.Models;

/// <summary>
/// Une position de clic, normalisee dans [0,1]x[0,1] par rapport a la zone video.
/// </summary>
/// <remarks>
/// C'est la seule chose qu'on stocke : jamais la cellule de grille calculee. Un
/// changement de reglage (lissage, seuil) est ainsi retroactif sur tout l'historique.
/// </remarks>
public readonly record struct NormalizedPoint(float X, float Y, long TimestampMs);
