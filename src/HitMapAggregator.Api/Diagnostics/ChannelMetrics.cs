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

    /// <param name="clickAgeMs">
    /// Age, a la publication, du plus vieux clic integre par ce tick ; <c>null</c> si le
    /// tick n'integrait aucun clic nouveau.
    /// </param>
    public void RecordTick(string channelId, double milliseconds, int payloadBytes, int positions, double? clickAgeMs = null)
    {
        ChannelStats stats = _channels.GetOrAdd(channelId, static _ => new ChannelStats());
        stats.Record(milliseconds, payloadBytes, positions, clickAgeMs);
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
        private readonly double[] _clickAges = new double[Window];
        private int _count;
        private int _index;
        private int _ageCount;
        private int _ageIndex;

        // Maximums depuis la lecture precedente, consommes comme le debit : ils donnent
        // les pics seconde par seconde, que la fenetre glissante de ~51 s lisserait.
        private double _recentTickMax;
        private double _recentClickAgeMax;
        private long _ticks;
        private int _payloadBytes;
        private int _positions;

        public void Record(double milliseconds, int payloadBytes, int positions, double? clickAgeMs)
        {
            lock (_gate)
            {
                if (clickAgeMs is { } age)
                {
                    _clickAges[_ageIndex] = age;
                    _ageIndex = (_ageIndex + 1) % Window;
                    _ageCount = Math.Min(_ageCount + 1, Window);
                    _recentClickAgeMax = Math.Max(_recentClickAgeMax, age);
                }

                _recentTickMax = Math.Max(_recentTickMax, milliseconds);
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
            double[] ages;
            long ticks;
            int payloadBytes;
            int positions;
            double recentTickMax;
            double recentClickAgeMax;

            lock (_gate)
            {
                sorted = new double[_count];
                Array.Copy(_durations, sorted, _count);
                ages = new double[_ageCount];
                Array.Copy(_clickAges, ages, _ageCount);
                ticks = _ticks;
                payloadBytes = _payloadBytes;
                positions = _positions;
                recentTickMax = _recentTickMax;
                recentClickAgeMax = _recentClickAgeMax;
                _recentTickMax = 0.0;
                _recentClickAgeMax = 0.0;
            }

            Array.Sort(sorted);
            Array.Sort(ages);

            return new ChannelReport(
                channelId,
                Ticks: ticks,
                TickP50Ms: Percentile(sorted, 0.50),
                TickP99Ms: Percentile(sorted, 0.99),
                TickMaxMs: sorted.Length == 0 ? 0.0 : Math.Round(sorted[^1], 3),
                SnapshotBytes: payloadBytes,
                Positions: positions,
                ClickAgeP50Ms: Percentile(ages, 0.50),
                ClickAgeP99Ms: Percentile(ages, 0.99),
                RecentTickMaxMs: Math.Round(recentTickMax, 3),
                RecentClickAgeMaxMs: Math.Round(recentClickAgeMax, 3));
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
    int Positions,
    double ClickAgeP50Ms,
    double ClickAgeP99Ms,
    double RecentTickMaxMs,
    double RecentClickAgeMaxMs);
