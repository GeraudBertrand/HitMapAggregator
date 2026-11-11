namespace HitMapAggregator.Core.Models;

/// <summary>
/// Reglages d'agregation d'un canal. Immuable : on remplace l'instance avec <c>with</c>,
/// ce qui evite tout verrou entre les commandes du streamer et la boucle de tick.
/// </summary>
public sealed record SessionSettings
{
    /// <summary>Rayon du flou, en cellules de grille. 0 desactive le lissage.</summary>
    public int SmoothingRadius { get; init; } = 4;

    /// <summary>Seuil de detection de pic, en fraction du maximum du champ.</summary>
    public float Threshold { get; init; } = 0.13f;

    /// <summary>Nombre de bandes de niveau. Transporte pour le rendu contour, le serveur ne s'en sert pas.</summary>
    public int BandCount { get; init; } = 6;

    public RenderMode RenderMode { get; init; } = RenderMode.Both;

    /// <summary>
    /// Prise en compte des viewers non connectes. Applique a l'agregation, jamais a
    /// l'ingestion : basculer ce reglage doit rester reversible sans perte de donnees.
    /// </summary>
    public bool AllowAnonymous { get; init; } = true;

    /// <summary>
    /// Nombre maximal de zones affichees. Borne de lisibilite, pas de calcul : le
    /// critere qui decide vraiment quelles zones existent est
    /// <see cref="MinZonePercentage"/>. Ce plafond ne mord que lorsque le vote est si
    /// fragmente que l'overlay deviendrait illisible.
    /// </summary>
    /// <remarks>
    /// Ecrete a <see cref="Aggregation.PeakDetector.HardCeiling"/>, qui est une borne
    /// structurelle : les accumulateurs de covariance vivent sur la pile et doivent
    /// etre dimensionnes a la compilation.
    /// </remarks>
    public int MaxZones { get; init; } = 12;

    /// <summary>
    /// L'overlay doit-il s'afficher ? Masquer n'interrompt ni la collecte ni les ticks.
    /// </summary>
    /// <remarks>
    /// Drapeau transporte, comme <see cref="RenderMode"/> : le serveur ne branche jamais
    /// dessus, c'est l'overlay qui obeit.
    /// </remarks>
    public bool Visible { get; init; } = true;

    /// <summary>
    /// Nombre de zones a mettre en avant a l'affichage, parmi celles retenues.
    /// </summary>
    /// <remarks>
    /// Purement decoratif : les zones sortent deja triees par effectif decroissant, donc
    /// « les N meilleures » sont les N premieres. Comme <see cref="BandCount"/> et
    /// <see cref="RenderMode"/>, la valeur est transportee jusqu'au rendu et le serveur
    /// ne branche jamais dessus.
    /// </remarks>
    public int MajorZones { get; init; } = 3;

    /// <summary>
    /// Contraste minimal exige du champ (maximum sur moyenne) pour qu'il y ait des zones.
    /// </summary>
    /// <remarks>
    /// Rejette les champs plats, ou des clics uniformement repartis produiraient des
    /// zones fantomes. ATTENTION : un seuil fixe est un compromis, pas une garantie. Le
    /// contraste chute mecaniquement quand le nombre de zones monte (20 zones a un rayon
    /// de 4 tombent a 2,3) et monte quand les clics sont peu nombreux (du bruit a 500
    /// clics et rayon 1 atteint 5,2). Pour un usage a nombreuses zones, baisser
    /// conjointement <see cref="SmoothingRadius"/> et ce seuil.
    /// </remarks>
    public float MinFieldContrast { get; init; } = 3f;

    /// <summary>Sous ce pourcentage, une zone est abandonnee et ses clics repartent au compte des disperses.</summary>
    public float MinZonePercentage { get; init; } = 3f;

    /// <summary>Duree de vie d'une position avant purge.</summary>
    public TimeSpan PositionTtl { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Deux pics plus proches que cette distance fusionnent (espace corrige, fraction de largeur).</summary>
    public float PeakMergeDistance { get; init; } = 0.13f;

    /// <summary>
    /// Rayon d'affectation d'un clic au pic le plus proche. <c>null</c> reprend
    /// <see cref="PeakMergeDistance"/>, ce qui est coherent par construction : deux pics
    /// distincts sont au moins a cette distance, donc leurs disques ne se recouvrent pas.
    /// La valeur reste surchargeable pour les cas ou ce defaut tronque trop les zones
    /// allongees.
    /// </summary>
    public float? AssignmentRadius { get; init; }

    public float EffectiveAssignmentRadius => AssignmentRadius ?? PeakMergeDistance;
}
