using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

namespace HitMapAggregator.Core.Session;

/// <summary>
/// Les sessions vivantes, indexees par identifiant de canal.
/// </summary>
public sealed class ChannelSessionRegistry
{
    private readonly ConcurrentDictionary<string, ChannelSession> _sessions = new(StringComparer.Ordinal);

    public int Count => _sessions.Count;

    public IEnumerable<ChannelSession> Sessions => _sessions.Values;

    public ChannelSession GetOrCreate(string channelId)
    {
        ArgumentException.ThrowIfNullOrEmpty(channelId);
        return _sessions.GetOrAdd(channelId, static id => new ChannelSession(id));
    }

    public bool TryGet(string channelId, [NotNullWhen(true)] out ChannelSession? session) =>
        _sessions.TryGetValue(channelId, out session);

    public bool Remove(string channelId) => _sessions.TryRemove(channelId, out _);
}
