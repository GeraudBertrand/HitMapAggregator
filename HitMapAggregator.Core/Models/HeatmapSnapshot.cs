namespace HitMapAggregator.Core.Models;

/// <summary>
/// Etat agrege d'un canal a un instant donne. Produit par tick, serialise, puis jete.
/// </summary>
/// <remarks>
/// L'instantane est concu pour un consommateur <b>sans memoire</b>. L'overlay recoit une
/// liste deja classee et n'a rien a retenir d'un tick a l'autre : la n-ieme zone est la
/// n-ieme, un point c'est tout. Il ne peut de toute facon pas faire mieux, puisqu'il
/// faudrait apparier les zones entre deux instantanes pour leur donner une identite.
///
/// Corollaire assume : les zones ne portent <b>aucun identifiant stable</b>. Si le
/// classement s'inverse entre deux ticks, l'indice 0 designe une autre zone, et c'est le
/// comportement voulu — on transporte un classement, pas des individus.
/// </remarks>
/// <param name="Density">Champ de densite lisse. Adosse a un tampon reutilise : ne pas conserver.</param>
/// <param name="Zones">
/// Zones retenues, <b>triees par effectif decroissant</b> : l'indice 0 est la zone
/// majeure. C'est le contrat, pas un detail d'implementation.
/// </param>
/// <param name="ScatteredPercentage">Part des clics n'appartenant a aucune zone retenue.</param>
/// <param name="TotalVotes">Nombre de clics pris en compte dans l'agregation.</param>
/// <param name="RenderMode">Rendu demande. Transporte tel quel, le serveur ne branche pas dessus.</param>
public sealed record HeatmapSnapshot(
    DensityField Density,
    IReadOnlyList<Zone> Zones,
    float ScatteredPercentage,
    int TotalVotes,
    RenderMode RenderMode);
