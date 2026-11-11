using System.Collections.Concurrent;
using System.Diagnostics;

namespace HitMapAggregator.Api.Diagnostics;

/// <summary>
/// Compteurs de fonctionnement : duree des ticks, debit d'ingestion, taille des
/// instantanes, positions en memoire.
/// </summary>
/// <remarks>
/// Ce sont ces chiffres qui decideront s'il faut un jour paralleliser les canaux ou
/// etaler les ticks dans le temps. Tant qu'ils ne le disent pas, une boucle async par
/// canal suffit.
/// </remarks>
public sealed class ChannelMetrics
{
    private readonly ConcurrentDictionary<string, ChannelStats> _channels = new(StringComparer.Ordinal);

    private long _accepted;
    private long _rejectedStopped;
    private long _rejectedInvalid;

    private long _lastReportAccepted;
    private long _lastReportStamp = Stopwatch.GetTimestamp();

    public void RecordTick(string channelId, double milliseconds, int payloadBytes, int positions)
    {
        ChannelStats stats = _channels.GetOrAdd(channelId, static _ => new ChannelStats());
        stats.Record(milliseconds, payloadBytes, positions);
    }

    public void RecordAccepted() => Interlocked.Increment(ref _accepted);

    public void RecordRejectedStopped() => Interlocked.Increment(ref _rejectedStopped);

    public void RecordRejectedInvalid() => Interlocked.Increment(ref _rejectedInvalid);

    public void Forget(string channelId) => _channels.TryRemove(channelId, out _);

    /// <summary>
    /// Etat courant.
    /// </summary>
    /// <remarks>
    /// Le debit est mesure <b>depuis la lecture precedente</b>, ce qui le rend exact mais
    /// consommable une seule fois : deux appels concurrents se voleraient l'intervalle.
    /// Les totaux, eux, sont toujours justes.
    /// </remarks>
    public MetricsReport Report(int liveChannels, int subscribers)
    {
        long accepted = Interlocked.Read(ref _accepted);
        long now = Stopwatch.GetTimestamp();
        double seconds = Stopwatch.GetElapsedTime(_lastReportStamp, now).TotalSeconds;
        double rate = seconds > 0.0 ? (accepted - _lastReportAccepted) / seconds : 0.0;

        _lastReportAccepted = accepted;
        _lastReportStamp = now;

        var channels = new List<ChannelReport>(_channels.Count);
        foreach ((string id, ChannelStats stats) in _channels)
        {
            channels.Add(stats.Report(id));
        }

        channels.Sort(static (a, b) => b.TickP99Ms.CompareTo(a.TickP99Ms));

        return new MetricsReport(
            LiveChannels: liveChannels,
            OverlaySubscribers: subscribers,
            ClicksAccepted: accepted,
            ClicksRejectedStopped: Interlocked.Read(ref _rejectedStopped),
            ClicksRejectedInvalid: Interlocked.Read(ref _rejectedInvalid),
            ClicksPerSecond: Math.Round(rate, 1),
            Channels: channels);
    }

    private sealed class ChannelStats
    {
        private const int Window = 256;

        private readonly Lock _gate = new();
        private readonly double[] _durations = new double[Window];
        private int _count;
        private int _index;
        private long _ticks;
        private int _payloadBytes;
        private int _positions;

        public void Record(double milliseconds, int payloadBytes, int positions)
        {
            lock (_gate)
            {
                _durations[_index] = milliseconds;
                _index = (_index + 1) % Window;
                _count = Math.Min(_count + 1, Window);
                _ticks++;
                _payloadBytes = payloadBytes;
                _positions = positions;
            }
        }

        public ChannelReport Report(string channelId)
        {
            double[] sorted;
            long ticks;
            int payloadBytes;
            int positions;

            lock (_gate)
            {
                sorted = new double[_count];
                Array.Copy(_durations, sorted, _count);
                ticks = _ticks;
                payloadBytes = _payloadBytes;
                positions = _positions;
            }

            Array.Sort(sorted);

            return new ChannelReport(
                channelId,
                Ticks: ticks,
                TickP50Ms: Percentile(sorted, 0.50),
                TickP99Ms: Percentile(sorted, 0.99),
                TickMaxMs: sorted.Length == 0 ? 0.0 : Math.Round(sorted[^1], 3),
                SnapshotBytes: payloadBytes,
                Positions: positions);
        }

        private static double Percentile(double[] sorted, double quantile)
        {
            if (sorted.Length == 0)
            {
                return 0.0;
            }

            int index = Math.Clamp((int)(sorted.Length * quantile), 0, sorted.Length - 1);
            return Math.Round(sorted[index], 3);
        }
    }
}

public sealed record MetricsReport(
    int LiveChannels,
    int OverlaySubscribers,
    long ClicksAccepted,
    long ClicksRejectedStopped,
    long ClicksRejectedInvalid,
    double ClicksPerSecond,
    IReadOnlyList<ChannelReport> Channels);

public sealed record ChannelReport(
    string ChannelId,
    long Ticks,
    double TickP50Ms,
    double TickP99Ms,
    double TickMaxMs,
    int SnapshotBytes,
    int Positions);
