using System.Collections.Concurrent;
using System.Diagnostics;
using HitMapAggregator.Api.Diagnostics;
using HitMapAggregator.Api.Realtime;
using HitMapAggregator.Core.Models;
using HitMapAggregator.Core.Session;

namespace HitMapAggregator.Api.Hosting;

/// <summary>
/// Tient une boucle d'agregation par canal actif.
/// </summary>
/// <remarks>
/// Une boucle <b>async</b> par canal, pas un thread : a 5 000 canaux, autant de threads
/// couteraient des gigaoctets de pile pour du code qui passe son temps a attendre un
/// timer. Corollaire imperatif : aucun appel bloquant dans ces boucles — pas de
/// <c>.Result</c>, pas de <c>.Wait()</c>, pas de <c>Thread.Sleep</c>. Un seul suffirait a
/// affamer le pool.
/// </remarks>
public sealed class ChannelSupervisor(
    ChannelSessionRegistry registry,
    SnapshotBroadcaster broadcaster,
    SnapshotSerializer serializer,
    ChannelMetrics metrics,
    ILogger<ChannelSupervisor> logger) : IHostedService
{
    public static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(200);

    /// <summary>Au-dela, on cesse de relancer : la panne est structurelle, pas passagere.</summary>
    private const int MaxRestarts = 5;

    private readonly ConcurrentDictionary<string, Loop> _loops = new(StringComparer.Ordinal);

    public int LiveChannels => _loops.Count;

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Ouvre la collecte et demarre la boucle du canal.</summary>
    public ChannelSession Start(string channelId)
    {
        ChannelSession session = registry.GetOrCreate(channelId);
        session.Start();

        _loops.GetOrAdd(channelId, id => Launch(session, id));
        return session;
    }

    /// <summary>
    /// Ferme la collecte et arrete la boucle.
    /// </summary>
    /// <remarks>
    /// Rien ne bouge plus une fois arrete : continuer a recalculer cinq fois par seconde
    /// serait du gaspillage pur, multiplie par le nombre de canaux inactifs. Le dernier
    /// instantane reste servi aux nouveaux abonnes, pour que l'overlay ne se vide pas.
    /// </remarks>
    public void Stop(string channelId)
    {
        if (registry.TryGet(channelId, out ChannelSession? session))
        {
            session.Stop();
        }

        if (_loops.TryRemove(channelId, out Loop? loop))
        {
            loop.FinalTick = true;
            loop.Cancel();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Loop[] loops = [.. _loops.Values];
        _loops.Clear();

        foreach (Loop loop in loops)
        {
            loop.Cancel();
        }

        // On attend, mais pas indefiniment : un arret propre ne doit pas retenir l'hote.
        Task drained = Task.WhenAll(loops.Select(static l => l.Task));
        Task timeout = Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);

        if (await Task.WhenAny(drained, timeout).ConfigureAwait(false) == timeout)
        {
            logger.LogWarning("Arret : {Count} boucle(s) n'ont pas rendu la main a temps", loops.Length);
        }
    }

    private Loop Launch(ChannelSession session, string channelId)
    {
        var cancellation = new CancellationTokenSource();
        var loop = new Loop(session, cancellation);
        loop.Task = RunAsync(loop, channelId, cancellation.Token);

        // Si la boucle meurt malgre le try/catch interne, on veut le savoir et repartir :
        // une boucle morte en silence, c'est un canal qui n'affiche plus rien sans qu'aucune
        // erreur ne soit visible nulle part.
        _ = loop.Task.ContinueWith(
            task => OnLoopEnded(task, loop, channelId),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        return loop;
    }

    private void OnLoopEnded(Task task, Loop loop, string channelId)
    {
        loop.Dispose();

        if (task.IsCanceled || !_loops.TryGetValue(channelId, out Loop? current) || current != loop)
        {
            return;
        }

        logger.LogError(
            task.Exception,
            "Boucle du canal {Channel} terminee de facon inattendue (relance {Restart}/{Max})",
            channelId,
            loop.Restarts + 1,
            MaxRestarts);

        if (loop.Restarts + 1 > MaxRestarts)
        {
            logger.LogCritical("Canal {Channel} abandonne apres {Max} relances", channelId, MaxRestarts);
            _loops.TryRemove(channelId, out _);
            return;
        }

        Loop replacement = Launch(loop.Session, channelId);
        replacement.Restarts = loop.Restarts + 1;
        _loops.TryUpdate(channelId, replacement, loop);
    }

    private async Task RunAsync(Loop loop, string channelId, CancellationToken cancellationToken)
    {
        // Rend la main tout de suite : le demarrage ne doit pas retenir la requete HTTP.
        await Task.Yield();

        using var timer = new PeriodicTimer(TickInterval);

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                // Le try est DANS la boucle : un tick en echec ne doit jamais emporter le canal.
                try
                {
                    Tick(loop.Session, channelId);
                }
                catch (Exception exception)
                {
                    logger.LogError(exception, "Tick en echec sur le canal {Channel}", channelId);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Arret du diffuseur : un dernier tick publie les clics acceptes depuis le
            // precedent, sinon la carte gelee les ignorerait jusqu'a la reprise. Joue par
            // la boucle elle-meme, seule autorisee a appeler BuildSnapshot.
            // shortcut: une reprise dans la foulee lance une nouvelle boucle sans attendre
            // celle-ci ; sans danger tant que ce tick (quelques ms) finit avant le premier
            // tick de la nouvelle (200 ms). A revoir si un tick depasse cet ordre de grandeur.
            if (loop.FinalTick)
            {
                try
                {
                    Tick(loop.Session, channelId);
                }
                catch (Exception exception)
                {
                    logger.LogError(exception, "Tick final en echec sur le canal {Channel}", channelId);
                }
            }
        }
    }

    private void Tick(ChannelSession session, string channelId)
    {
        session.PurgeExpired(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        long start = Stopwatch.GetTimestamp();
        HeatmapSnapshot snapshot = session.BuildSnapshot();
        string payload = serializer.Serialize(snapshot, session.Settings);
        double elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;

        broadcaster.Publish(channelId, payload);

        // Mesure apres la publication : c'est l'instant ou l'overlay peut voir le clic.
        long oldestClick = session.LastSnapshotOldestClickStamp;
        double? clickAgeMs = oldestClick == 0 ? null : Stopwatch.GetElapsedTime(oldestClick).TotalMilliseconds;

        metrics.RecordTick(channelId, elapsed, payload.Length, snapshot.TotalVotes, clickAgeMs);
    }

    private sealed class Loop(ChannelSession session, CancellationTokenSource cancellation)
    {
        public ChannelSession Session { get; } = session;

        public CancellationTokenSource Cancellation { get; } = cancellation;

        public Task Task { get; set; } = Task.CompletedTask;

        public int Restarts { get; set; }

        /// <summary>Arret du diffuseur, par opposition a l'arret de l'hote : publier une derniere fois.</summary>
        public volatile bool FinalTick;

        /// <summary>
        /// Annule sans exiger de gagner la course contre la fin de la boucle.
        /// </summary>
        /// <remarks>
        /// La boucle libere sa source quand elle se termine ; un arret concurrent peut
        /// donc tomber sur une source deja liberee. C'est benin — la boucle est finie,
        /// il n'y a plus rien a annuler — mais l'exception faisait echouer l'arret de
        /// l'hote tout entier.
        /// </remarks>
        public void Cancel()
        {
            try
            {
                Cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        public void Dispose() => Cancellation.Dispose();
    }
}
