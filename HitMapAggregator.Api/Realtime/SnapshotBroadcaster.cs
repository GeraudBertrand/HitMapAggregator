using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace HitMapAggregator.Api.Realtime;

/// <summary>
/// Distribue le dernier instantane serialise aux overlays abonnes a un canal.
/// </summary>
/// <remarks>
/// Un <see cref="Channel{T}"/> par abonne, borne a un seul element en
/// <see cref="BoundedChannelFullMode.DropOldest"/> : si un overlay prend du retard, on
/// jette l'instantane perime plutot que de faire attendre la boucle de tick. Un etat
/// depasse n'a aucune valeur, et c'est la boucle qu'il faut proteger.
/// </remarks>
public sealed class SnapshotBroadcaster
{
    private readonly ConcurrentDictionary<string, Feed> _feeds = new(StringComparer.Ordinal);

    public void Publish(string channelId, string payload)
    {
        Feed feed = _feeds.GetOrAdd(channelId, static _ => new Feed());
        feed.Last = payload;

        foreach (Channel<string> subscriber in feed.Subscribers.Values)
        {
            subscriber.Writer.TryWrite(payload);
        }
    }

    public int SubscriberCount(string channelId) =>
        _feeds.TryGetValue(channelId, out Feed? feed) ? feed.Subscribers.Count : 0;

    public int TotalSubscribers
    {
        get
        {
            int total = 0;
            foreach (Feed feed in _feeds.Values)
            {
                total += feed.Subscribers.Count;
            }

            return total;
        }
    }

    /// <summary>Efface l'etat retenu d'un canal, pour que plus personne ne recoive l'ancien.</summary>
    public void Forget(string channelId)
    {
        if (_feeds.TryGetValue(channelId, out Feed? feed))
        {
            feed.Last = null;
        }
    }

    /// <summary>Ferme proprement tous les flux ouverts d'un canal, sans toucher a son etat.</summary>
    /// <remarks>
    /// Completer le canal de l'abonne termine <c>ReadAllAsync</c> sans exception : la
    /// reponse SSE se ferme normalement, la ou une annulation remonterait en erreur.
    /// </remarks>
    public void Disconnect(string channelId)
    {
        if (_feeds.TryGetValue(channelId, out Feed? feed))
        {
            foreach (Channel<string> subscriber in feed.Subscribers.Values)
            {
                subscriber.Writer.TryComplete();
            }
        }
    }

    public async IAsyncEnumerable<string> Subscribe(
        string channelId,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Feed feed = _feeds.GetOrAdd(channelId, static _ => new Feed());

        Channel<string> inbox = Channel.CreateBounded<string>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });

        var subscription = Guid.NewGuid();
        feed.Subscribers[subscription] = inbox;

        try
        {
            // Un canal arrete ne produit plus de tick : sans ce rappel, un overlay qui se
            // connecte apres coup resterait devant un ecran vide.
            if (feed.Last is { } last)
            {
                yield return last;
            }

            await foreach (string payload in inbox.Reader.ReadAllAsync(cancellationToken))
            {
                yield return payload;
            }
        }
        finally
        {
            feed.Subscribers.TryRemove(subscription, out _);
        }
    }

    private sealed class Feed
    {
        public volatile string? Last;

        public ConcurrentDictionary<Guid, Channel<string>> Subscribers { get; } = new();
    }
}
