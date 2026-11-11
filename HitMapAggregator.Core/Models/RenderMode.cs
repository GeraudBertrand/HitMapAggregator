namespace HitMapAggregator.Core.Models;

/// <summary>
/// Rendu demande par le streamer. Le serveur calcule toujours les deux
/// representations ; cette valeur est transportee, jamais branchee dessus.
/// </summary>
public enum RenderMode
{
    Ellipse,
    Contour,
    Both,
}
