using System.Numerics;

namespace HitMapAggregator.Core.Models;

/// <summary>
/// Une zone d'interet : l'ellipse de covariance des clics qui lui sont affectes.
/// </summary>
/// <param name="Center">Centre en coordonnees normalisees [0,1]x[0,1], pret a etre place a l'ecran.</param>
/// <param name="SigmaA">Ecart-type sur le grand axe, en fraction de la LARGEUR d'ecran.</param>
/// <param name="SigmaB">Ecart-type sur le petit axe, en fraction de la LARGEUR d'ecran.</param>
/// <param name="Angle">Orientation du grand axe en radians, dans [0, pi).</param>
/// <param name="PointCount">Nombre de clics affectes a la zone.</param>
/// <param name="Percentage">Part de ces clics sur le total, en pourcent.</param>
/// <remarks>
/// Les sigmas sont des <b>ecarts-types</b>, pas des demi-axes : le facteur k
/// (2 par convention) est applique a l'affichage, pas ici.
///
/// Attention a l'unite : les deux sigmas sont dans la meme echelle (fraction de
/// largeur), parce qu'ils sont mesures dans l'espace corrige de l'aspect. Un rendu
/// correct multiplie donc les DEUX par la largeur du canevas, jamais SigmaB par la
/// hauteur — on ne peut pas etirer independamment les axes d'une ellipse pivotee.
/// L'angle est mesure dans ce meme espace, avec Y vers le bas comme a l'ecran.
/// </remarks>
public readonly record struct Zone(
    Vector2 Center,
    float SigmaA,
    float SigmaB,
    float Angle,
    int PointCount,
    float Percentage);
