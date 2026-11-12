using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using HitMapAggregator.Core.Models;
using HitMapAggregator.Core.Session;
using HitMapAggregator.Testing;

namespace HitMapAggregator.Traffic.Bench;

/// <summary>
/// Une passe : un canal neuf, un script de clics, l'echantillonnage chaque seconde et les
/// verifications de fin.
/// </summary>
internal sealed class BenchPass(BenchOptions options, HttpClient http, HttpClient stream, LiveHub hub)
{
    private const int Workers = 64;

    /// <summary>
    /// La fenetre glissante du serveur garde 256 ticks, soit ~51 s : lue avant cette borne,
    /// elle couvre encore toute la phase active depuis le debut de la passe.
    /// </summary>
    private const double WindowSeconds = 50;

    private readonly Lock _latencyGate = new();
    private List<double> _latencyBucket = [];
    private readonly List<double> _allLatencies = [];

    private long _accepted;
    private long _conflicts;
    private long _failed;

    // Boite volatile : un JsonElement nu pourrait etre lu a moitie ecrit par le lecteur SSE.
    private volatile StrongBox<JsonElement>? _last;
    private volatile bool _stopped;
    private Check? _frozenCheck;
    private long _stoppedAtMs;
    private int _publishedWhileStopped;

    private readonly Stopwatch _clock = new();

    private long Elapsed => _clock.ElapsedMilliseconds;

    private JsonElement? LastSnapshot => _last?.Value;

    public async Task RunAsync(BenchConfig config, Distribution distribution, PassResult result, int seed, bool record)
    {
        string channelId = result.Id;
        ClickPlan plan = config.CreatePlan(distribution, options.Duration, seed);
        long durationMs = (long)options.Duration.TotalMilliseconds;

        // Un JWT par viewer, signe d'avance et en parallele : 30 000 signatures en serie
        // retarderaient le depart de plusieurs secondes.
        string[] jwts = new string[plan.Clickers];
        Parallel.For(0, jwts.Length, i => jwts[i] = TwitchTokens.Viewer(options.Secret, channelId, $"U{i}"));
        string broadcaster = TwitchTokens.Broadcaster(options.Secret, channelId);

        await ControlAsync(broadcaster, HttpMethod.Post, "/api/control/start");
        string token = await OverlayTokenAsync(broadcaster);

        var lastAccepted = new NormalizedPoint?[plan.Clickers];

        using var streamStop = new CancellationTokenSource();
        Task reader = ReadStreamAsync(channelId, token, result, record, streamStop.Token);

        _clock.Start();

        using var clicksStop = new CancellationTokenSource();
        Task clicks = RunClicksAsync(plan, jwts, lastAccepted, durationMs, clicksStop.Token);
        Task drift = DriftAsync(plan, durationMs);
        Task control = ControlTimelineAsync(plan, broadcaster, result, lastAccepted);
        Task<ChannelWindow> sampler = SampleAsync(channelId, result, durationMs + 1_000, record);

        await clicks;
        await drift;
        await control;

        // Deux ticks au moins pour que le dernier clic accepte soit publie.
        await Task.Delay(800);
        ChannelWindow window = await sampler;

        await ControlAsync(broadcaster, HttpMethod.Post, "/api/control/stop");
        await streamStop.CancelAsync();
        await reader;

        if (!record)
        {
            return;
        }

        int votes = Votes(LastSnapshot);
        double[] sorted;
        lock (_latencyGate)
        {
            sorted = [.. _allLatencies.Order()];
        }

        var summary = new PassSummary(
            Sent: _accepted + _conflicts + _failed,
            Accepted: _accepted,
            Conflicts: _conflicts,
            Failed: _failed,
            Votes: votes,
            Zones: ZoneCount(LastSnapshot),
            PeakClicksPerSecond: result.Samples.Count == 0 ? 0 : result.Samples.Max(s => s.ClicksPerSecond),
            TickP50Ms: window.TickP50,
            TickP99Ms: window.TickP99,
            TickMaxMs: result.Samples.Count == 0 ? 0 : result.Samples.Max(s => s.TickMaxMs),
            ClickAgeP50Ms: window.AgeP50,
            ClickAgeP99Ms: window.AgeP99,
            PostP50Ms: Percentile(sorted, 0.50),
            PostP99Ms: Percentile(sorted, 0.99));

        var checks = new List<Check>();
        AddChecks(plan, result, checks, lastAccepted, votes);
        hub.Update(() =>
        {
            result.Summary = summary;
            result.Checks.AddRange(checks);
        });
    }

    private void AddChecks(ClickPlan plan, PassResult result, List<Check> checks, NormalizedPoint?[] lastAccepted, int votes)
    {
        if (plan.ExpectedVotes is { } expectedVotes)
        {
            checks.Add(new Check("Votes comptés", votes == expectedVotes, $"{votes:N0} pour {expectedVotes:N0} attendus"));
        }

        if (plan.ExpectedAccepted is { } expectedAccepted)
        {
            checks.Add(new Check("Clics acceptés", _accepted == expectedAccepted, $"{_accepted:N0} pour {expectedAccepted:N0} envoyés"));
        }

        checks.Add(new Check("Aucun échec HTTP", _failed == 0, $"{_failed:N0} échec(s)"));
        checks.Add(CompareWithReference("Identique au calcul local", LastSnapshot, lastAccepted));

        if (plan.StopAtMs is null)
        {
            checks.Add(new Check("Aucun refus 409", _conflicts == 0, $"{_conflicts:N0} refus"));
            return;
        }

        checks.Add(new Check("Clics refusés pendant l'arrêt (409)", _conflicts > 0, $"{_conflicts:N0} refus"));
        checks.Add(new Check(
            "Carte gelée pendant l'arrêt",
            _publishedWhileStopped == 0,
            $"{_publishedWhileStopped} instantané(s) publié(s) pendant l'arrêt"));
        checks.Add(_frozenCheck!);

        // La cible B est loin de la cible A : la zone majeure doit avoir franchement bouge.
        (float X, float Y)? before = MajorCenter(result.FrozenSnapshot);
        (float X, float Y)? after = MajorCenter(LastSnapshot);
        double moved = before is { } b && after is { } a ? Math.Sqrt(Square(a.X - b.X) + Square(a.Y - b.Y)) : 0;
        checks.Add(new Check("Nouvelle cible après reprise", moved > 0.1, $"zone majeure déplacée de {moved:F2} (fraction d'écran)"));
    }

    /// <summary>
    /// Rejoue en local, avec le <c>Core</c>, exactement les clics que le serveur a acceptes.
    /// </summary>
    /// <remarks>
    /// C'est la verification « resultat connu » qui vaut pour les deux distributions : ce
    /// qui arrive dans l'overlay doit etre ce que donne l'algorithme sur ces positions-la.
    /// Elle couvre toute la chaine — ingestion, dedoublonnage, tick, serialisation, SSE.
    /// Tolerance faible mais non nulle : l'ordre de parcours du dictionnaire change l'ordre
    /// des sommes flottantes, et le JSON arrondit.
    /// </remarks>
    private static Check CompareWithReference(string label, JsonElement? received, NormalizedPoint?[] lastAccepted)
    {
        var session = new ChannelSession("reference");
        session.Start();
        for (int i = 0; i < lastAccepted.Length; i++)
        {
            if (lastAccepted[i] is { } point)
            {
                session.Upsert((ulong)i + 1, point);
            }
        }

        HeatmapSnapshot expected = session.BuildSnapshot();

        if (received is not { } snapshot)
        {
            return new Check(label, false, "aucun instantané reçu");
        }

        JsonElement zones = snapshot.GetProperty("zones");
        int votes = snapshot.GetProperty("totalVotes").GetInt32();
        if (votes != expected.TotalVotes || zones.GetArrayLength() != expected.Zones.Count)
        {
            return new Check(label, false,
                $"serveur {votes} votes / {zones.GetArrayLength()} zones, local {expected.TotalVotes} / {expected.Zones.Count}");
        }

        double centerGap = 0;
        double pctGap = 0;
        int index = 0;
        foreach (JsonElement zone in zones.EnumerateArray())
        {
            Zone reference = expected.Zones[index++];
            centerGap = Math.Max(centerGap, Math.Abs(zone.GetProperty("cx").GetDouble() - reference.Center.X));
            centerGap = Math.Max(centerGap, Math.Abs(zone.GetProperty("cy").GetDouble() - reference.Center.Y));
            pctGap = Math.Max(pctGap, Math.Abs(zone.GetProperty("pct").GetDouble() - reference.Percentage));
        }

        return new Check(
            label,
            centerGap <= 0.01 && pctGap <= 0.5,
            $"{expected.Zones.Count} zone(s), écart max centre {centerGap:F4}, part {pctGap:F2} pt");
    }

    private async Task RunClicksAsync(
        ClickPlan plan,
        string[] jwts,
        NormalizedPoint?[] lastAccepted,
        long durationMs,
        CancellationToken cancellationToken)
    {
        // Le clic k+1 d'un viewer n'est planifie qu'une fois le clic k termine : comme dans
        // l'extension, les clics d'un meme viewer partent dans l'ordre, et le dernier envoye
        // est bien le dernier recu — sinon la reference locale divergerait du serveur.
        var schedule = new PriorityQueue<(int Viewer, int Index), long>(plan.Clickers);
        for (int viewer = 0; viewer < plan.Clickers; viewer++)
        {
            schedule.Enqueue((viewer, 0), plan.StartMs[viewer]);
        }

        Channel<(int Viewer, int Index)> due = Channel.CreateUnbounded<(int, int)>();

        async Task WorkAsync()
        {
            await foreach ((int viewer, int index) in due.Reader.ReadAllAsync(CancellationToken.None))
            {
                long sentAt = Elapsed;
                NormalizedPoint point = plan.SourceAt(sentAt).Next(viewer);
                await SendAsync(jwts[viewer], point, viewer, lastAccepted);

                if (index + 1 < plan.Clicks[viewer])
                {
                    long next = Math.Max(sentAt + plan.IntervalMs, Elapsed);
                    lock (schedule)
                    {
                        schedule.Enqueue((viewer, index + 1), next);
                    }
                }
            }
        }

        Task[] workers = [.. Enumerable.Range(0, Workers).Select(_ => Task.Run(WorkAsync))];

        while (Elapsed < durationMs && !cancellationToken.IsCancellationRequested)
        {
            long now = Elapsed;
            lock (schedule)
            {
                while (schedule.TryPeek(out (int, int) item, out long at) && at <= now)
                {
                    schedule.Dequeue();
                    due.Writer.TryWrite(item);
                }
            }

            await Task.Delay(5, CancellationToken.None);
        }

        // On laisse finir les requetes en vol au lieu de les annuler : un clic annule cote
        // client a pu etre accepte cote serveur, et la reference ne le saurait pas.
        due.Writer.TryComplete();
        await Task.WhenAll(workers);
    }

    private async Task SendAsync(string jwt, NormalizedPoint point, int viewer, NormalizedPoint?[] lastAccepted)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/clicks")
        {
            Content = JsonContent.Create(new ClickRequest(point.X, point.Y)),
        };
        request.Headers.Authorization = new("Bearer", jwt);

        long start = Stopwatch.GetTimestamp();
        try
        {
            using HttpResponseMessage response = await http.SendAsync(request);
            double milliseconds = Stopwatch.GetElapsedTime(start).TotalMilliseconds;

            lock (_latencyGate)
            {
                _latencyBucket.Add(milliseconds);
                _allLatencies.Add(milliseconds);
            }

            switch (response.StatusCode)
            {
                case HttpStatusCode.NoContent:
                    lastAccepted[viewer] = point;
                    Interlocked.Increment(ref _accepted);
                    break;
                case HttpStatusCode.Conflict:
                    Interlocked.Increment(ref _conflicts);
                    break;
                default:
                    Interlocked.Increment(ref _failed);
                    break;
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            Interlocked.Increment(ref _failed);
        }
    }

    private async Task DriftAsync(ClickPlan plan, long durationMs)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
        while (Elapsed < durationMs && await timer.WaitForNextTickAsync())
        {
            plan.Before.Drift();
            plan.After?.Drift();
        }
    }

    private async Task ControlTimelineAsync(ClickPlan plan, string broadcaster, PassResult result, NormalizedPoint?[] lastAccepted)
    {
        if (plan.StopAtMs is not { } stopAt || plan.RestartAtMs is not { } restartAt)
        {
            return;
        }

        await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(0, stopAt - Elapsed)));
        await ControlAsync(broadcaster, HttpMethod.Post, "/api/control/stop");
        _stoppedAtMs = Elapsed;
        _stopped = true;

        await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(0, restartAt - Elapsed)));

        // Capturee juste avant la reprise : c'est la carte qui est restee affichee. Pendant
        // l'arret tout clic est refuse, donc les clics acceptes n'ont pas bouge depuis.
        JsonElement? frozen = LastSnapshot;
        hub.Update(() => result.FrozenSnapshot = frozen);
        _frozenCheck = CompareWithReference("Carte gelée = tous les clics acceptés avant l'arrêt", frozen, lastAccepted);

        _stopped = false;
        await ControlAsync(broadcaster, HttpMethod.Post, "/api/control/start");
    }

    private async Task<ChannelWindow> SampleAsync(string channelId, PassResult result, long untilMs, bool record)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        long previousAccepted = 0;
        long previousStamp = Stopwatch.GetTimestamp();
        ChannelWindow window = default;

        while (Elapsed < untilMs && await timer.WaitForNextTickAsync())
        {
            JsonElement? channel = await ChannelMetricsAsync(channelId);

            List<double> bucket;
            lock (_latencyGate)
            {
                bucket = _latencyBucket;
                _latencyBucket = [];
            }

            bucket.Sort();
            long accepted = Interlocked.Read(ref _accepted);
            long stamp = Stopwatch.GetTimestamp();
            double rate = (accepted - previousAccepted) / Stopwatch.GetElapsedTime(previousStamp, stamp).TotalSeconds;
            previousAccepted = accepted;
            previousStamp = stamp;

            double t = Elapsed / 1000.0;
            JsonElement? snapshot = LastSnapshot;

            var sample = new Sample(
                T: Math.Round(t, 2),
                Running: !_stopped,
                ClicksPerSecond: Math.Round(rate),
                PostP50Ms: Percentile(bucket, 0.50),
                PostP99Ms: Percentile(bucket, 0.99),
                TickMaxMs: Read(channel, "recentTickMaxMs"),
                ClickAgeMaxMs: Read(channel, "recentClickAgeMaxMs"),
                Votes: Votes(snapshot),
                Zones: ZoneCount(snapshot),
                TopPct: TopPct(snapshot));

            if (channel is not null && t <= WindowSeconds)
            {
                window = new ChannelWindow(
                    Read(channel, "tickP50Ms"),
                    Read(channel, "tickP99Ms"),
                    Read(channel, "clickAgeP50Ms"),
                    Read(channel, "clickAgeP99Ms"));
            }

            if (record)
            {
                hub.AddSample(result, sample);
            }
        }

        return window;
    }

    private async Task<JsonElement?> ChannelMetricsAsync(string channelId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/metrics");
        request.Headers.Add("X-Metrics-Key", options.MetricsKey);

        using HttpResponseMessage response = await http.SendAsync(request);
        response.EnsureSuccessStatusCode();

        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync());
        foreach (JsonElement channel in document.RootElement.GetProperty("channels").EnumerateArray())
        {
            if (channel.GetProperty("channelId").GetString() == channelId)
            {
                return channel.Clone();
            }
        }

        return null;
    }

    private async Task ReadStreamAsync(string channelId, string token, PassResult result, bool record, CancellationToken cancellationToken)
    {
        // L'URL porte le jeton overlay : elle n'est ni affichee ni journalisee.
        string path = $"/api/stream/heatmap?channelId={Uri.EscapeDataString(channelId)}&token={Uri.EscapeDataString(token)}";

        try
        {
            using HttpResponseMessage response = await stream.GetAsync(path, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(cancellationToken));
            string? eventType = null;
            var data = new StringBuilder();

            while (await reader.ReadLineAsync(cancellationToken) is { } line)
            {
                if (line.Length == 0)
                {
                    if (eventType == "snapshot" && data.Length > 0)
                    {
                        OnSnapshot(data.ToString(), result, record);
                    }

                    eventType = null;
                    data.Clear();
                }
                else if (line.StartsWith("event:", StringComparison.Ordinal))
                {
                    eventType = line[6..].Trim();
                }
                else if (line.StartsWith("data:", StringComparison.Ordinal))
                {
                    data.Append(line.AsSpan(5).TrimStart());
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Fin de passe.
        }
    }

    private void OnSnapshot(string json, PassResult result, bool record)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement snapshot = document.RootElement.Clone();
        _last = new StrongBox<JsonElement>(snapshot);

        // Marge de 300 ms : un tick deja en cours au moment de l'arret peut encore publier.
        if (_stopped && Elapsed - _stoppedAtMs > 300)
        {
            Interlocked.Increment(ref _publishedWhileStopped);
        }

        if (record)
        {
            hub.SetSnapshot(result, snapshot);
        }
    }

    private async Task ControlAsync(string jwt, HttpMethod method, string path)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new("Bearer", jwt);
        using HttpResponseMessage response = await http.SendAsync(request);
        response.EnsureSuccessStatusCode();
    }

    private async Task<string> OverlayTokenAsync(string jwt)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/control/overlay-token");
        request.Headers.Authorization = new("Bearer", jwt);
        using HttpResponseMessage response = await http.SendAsync(request);
        response.EnsureSuccessStatusCode();

        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync());
        return document.RootElement.GetProperty("token").GetString()!;
    }

    private static int Votes(JsonElement? snapshot) => snapshot?.GetProperty("totalVotes").GetInt32() ?? 0;

    private static int ZoneCount(JsonElement? snapshot) => snapshot?.GetProperty("zones").GetArrayLength() ?? 0;

    private static double TopPct(JsonElement? snapshot) =>
        snapshot is { } s && s.GetProperty("zones").GetArrayLength() > 0
            ? s.GetProperty("zones")[0].GetProperty("pct").GetDouble()
            : 0;

    private static (float X, float Y)? MajorCenter(JsonElement? snapshot) =>
        snapshot is { } s && s.GetProperty("zones").GetArrayLength() > 0
            ? ((float)s.GetProperty("zones")[0].GetProperty("cx").GetDouble(), (float)s.GetProperty("zones")[0].GetProperty("cy").GetDouble())
            : null;

    private static double Read(JsonElement? channel, string property) =>
        channel is { } c ? c.GetProperty(property).GetDouble() : 0;

    private static double Percentile(IReadOnlyList<double> sorted, double quantile) =>
        sorted.Count == 0 ? 0 : Math.Round(sorted[Math.Clamp((int)(sorted.Count * quantile), 0, sorted.Count - 1)], 2);

    private static double Square(double value) => value * value;

    private readonly record struct ChannelWindow(double TickP50, double TickP99, double AgeP50, double AgeP99);
}
