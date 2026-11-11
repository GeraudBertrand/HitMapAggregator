using System.Numerics;
using HitMapAggregator.Core.Aggregation;
using HitMapAggregator.Core.Models;

namespace HitMapAggregator.Core.Session;

/// <summary>
/// Etat vivant d'un canal : la derniere position connue de chaque viewer, les reglages,
/// et les tampons de calcul reutilises a chaque tick.
/// </summary>
public sealed class ChannelSession(string channelId)
{
    private readonly Lock _gate = new();
    private readonly Dictionary<ulong, NormalizedPoint> _positions = [];

    // Pre-alloues une fois pour toutes : un tick ne doit rien demander au GC.
    private readonly float[] _primary = new float[Grid.CellCount];
    private readonly float[] _scratch = new float[Grid.CellCount];
    private NormalizedPoint[] _snapshotBuffer = [];

    private volatile bool _running;

    public string ChannelId { get; } = channelId;

    /// <summary>
    /// La collecte est-elle ouverte ? Une session naît arrêtée : c'est le streamer qui
    /// lance la hitmap.
    /// </summary>
    public bool IsRunning => _running;

    /// <summary>
    /// Reglages courants. L'instance est immuable : la remplacer est atomique, donc les
    /// commandes du streamer n'ont pas a se synchroniser avec la boucle de tick.
    /// </summary>
    public SessionSettings Settings { get; set; } = new();

    public int PositionCount
    {
        get
        {
            lock (_gate)
            {
                return _positions.Count;
            }
        }
    }

    /// <summary>Ouvre la collecte. Les positions deja presentes sont conservees.</summary>
    public void Start() => _running = true;

    /// <summary>
    /// Ferme la collecte, sans rien effacer.
    /// </summary>
    /// <remarks>
    /// Arreter et vider sont deux gestes distincts : sinon <see cref="Reset"/> n'aurait
    /// plus de raison d'exister, et un arret momentane detruirait un vote en cours.
    /// </remarks>
    public void Stop() => _running = false;

    /// <summary>
    /// Dernier clic gagnant : on ecrase, on n'incremente jamais.
    /// </summary>
    /// <remarks>
    /// Refuse quand la collecte est fermee. Le refus vit ici et pas dans l'endpoint pour
    /// qu'il n'y ait qu'une seule autorite, sans moyen de la contourner.
    ///
    /// Ce filtrage-la est bien a l'ingestion, contrairement a celui des viewers anonymes :
    /// un arret est delibere, le streamer ne s'attend pas a recuperer ces clics.
    /// </remarks>
    /// <returns><c>false</c> si la collecte est fermee et le clic ignore.</returns>
    public bool Upsert(ulong userKey, NormalizedPoint point)
    {
        lock (_gate)
        {
            if (!_running)
            {
                return false;
            }

            _positions[userKey] = point;
            return true;
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _positions.Clear();
        }
    }

    /// <summary>
    /// Retire les positions plus vieilles que le TTL.
    /// </summary>
    /// <remarks>
    /// Indispensable et pas seulement pour la memoire : l'identifiant d'un viewer
    /// anonyme change a chaque rechargement de page, donc sans purge chaque F5 laisse
    /// une entree fantome qui continue de voter.
    /// </remarks>
    /// <returns>Le nombre d'entrees retirees.</returns>
    public int PurgeExpired(long nowMs)
    {
        long ttlMs = (long)Settings.PositionTtl.TotalMilliseconds;
        int removed = 0;

        lock (_gate)
        {
            // Supprimer pendant l'enumeration d'un Dictionary est supporte depuis
            // .NET Core 3.0, ce qui evite une liste temporaire a chaque purge.
            foreach ((ulong key, NormalizedPoint point) in _positions)
            {
                if (nowMs - point.TimestampMs > ttlMs)
                {
                    _positions.Remove(key);
                    removed++;
                }
            }
        }

        return removed;
    }

    /// <summary>
    /// Produit l'instantane du tick.
    /// </summary>
    /// <remarks>
    /// Le verrou ne couvre que la recopie des positions, pas le calcul : garder le
    /// dictionnaire bloque pendant les quelques millisecondes d'agregation ferait
    /// attendre toutes les requetes d'ingestion en cours.
    /// </remarks>
    public HeatmapSnapshot BuildSnapshot()
    {
        SessionSettings settings = Settings;
        int count = CopyPositions(settings.AllowAnonymous);
        return SnapshotBuilder.Build(_snapshotBuffer.AsSpan(0, count), settings, _primary, _scratch);
    }

    private int CopyPositions(bool allowAnonymous)
    {
        lock (_gate)
        {
            if (_snapshotBuffer.Length < _positions.Count)
            {
                _snapshotBuffer = new NormalizedPoint[BitOperations.RoundUpToPowerOf2((uint)_positions.Count)];
            }

            int written = 0;
            foreach ((ulong key, NormalizedPoint point) in _positions)
            {
                if (!allowAnonymous && UserKey.IsAnonymous(key))
                {
                    continue;
                }

                _snapshotBuffer[written++] = point;
            }

            return written;
        }
    }
}
