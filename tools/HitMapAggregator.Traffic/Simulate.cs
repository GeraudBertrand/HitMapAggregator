using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Json;
using HitMapAggregator.Core.Models;
using HitMapAggregator.Testing;

namespace HitMapAggregator.Traffic;

/// <summary>
/// Envoie des clics vers /api/clicks pour alimenter un overlay.
/// </summary>
/// <remarks>
/// Il rejoue les six jeux synthetiques — utiles parce que leur resultat attendu est connu
/// — mais aussi un monde aleatoire qui derive dans le temps, seul moyen de voir un
/// classement s'inverser en conditions reelles.
/// </remarks>
internal static class Simulate
{
    public static async Task<int> RunAsync(string[] args)
    {
        Options options;
        try
        {
            options = Options.Parse(args);
        }
        catch (ArgumentException exception)
        {
            Console.Error.WriteLine(exception.Message);
            Console.Error.WriteLine(Options.Usage);
            return 1;
        }

        using var client = new HttpClient(new SocketsHttpHandler
        {
            MaxConnectionsPerServer = Math.Max(16, options.Workers * 2),
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        })
        {
            BaseAddress = new Uri(options.Url),
            Timeout = TimeSpan.FromSeconds(10),
        };

        if (options.Start)
        {
            using var startRequest = new HttpRequestMessage(HttpMethod.Post, "/api/control/start");
            startRequest.Headers.Authorization = new("Bearer", TwitchTokens.Broadcaster(options.Secret, options.ChannelId));
            using HttpResponseMessage response = await client.SendAsync(startRequest);

            Console.WriteLine($"start -> {(int)response.StatusCode} {response.StatusCode}");
        }

        var source = new ClickSource(options);
        var counters = new Counters();

        using var stopping = new CancellationTokenSource(options.Duration);
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            stopping.Cancel();
        };

        Console.WriteLine(
            $"{options.Scenario} -> {options.Url}  canal={options.ChannelId}  " +
            $"{options.Viewers:N0} viewers  {options.Rate:N0} clics/s  {options.Duration.TotalSeconds:N0} s");

        long start = Stopwatch.GetTimestamp();

        // Un seau a jetons plutot qu'un timer par clic : sous Windows la granularite des
        // timers est d'environ 15 ms, donc un PeriodicTimer ne sait pas cadencer a 3 000
        // clics/s. On distribue les jetons par paquets toutes les 20 ms.
        using var permits = new SemaphoreSlim(0, Math.Max(options.Rate, 1));

        Task refill = RefillAsync(permits, options.Rate, stopping.Token);
        Task reporter = ReportAsync(counters, stopping.Token);
        Task[] workers = new Task[options.Workers];
        for (int i = 0; i < workers.Length; i++)
        {
            workers[i] = RunWorkerAsync(client, options, source, counters, permits, stopping.Token);
        }

        await Task.WhenAll(workers);
        await refill;
        await reporter;

        double seconds = Stopwatch.GetElapsedTime(start).TotalSeconds;
        Console.WriteLine();
        Console.WriteLine(
            $"termine : {counters.Accepted:N0} acceptes, {counters.Conflict:N0} refuses (arrete), " +
            $"{counters.Invalid:N0} invalides, {counters.Failed:N0} echecs  " +
            $"en {seconds:F1} s -> {counters.Accepted / seconds:N0} clics/s");

        return 0;
    }

    private static async Task RefillAsync(SemaphoreSlim permits, int rate, CancellationToken cancellationToken)
    {
        const int SlicesPerSecond = 50;
        int perSlice = Math.Max(1, rate / SlicesPerSecond);

        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(1000.0 / SlicesPerSecond));

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                // Le seau est borne : s'il deborde, c'est que les envois ne suivent pas,
                // et accumuler des jetons ne ferait que differer la rafale.
                int room = permits.CurrentCount + perSlice <= rate ? perSlice : rate - permits.CurrentCount;
                if (room > 0)
                {
                    permits.Release(room);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Duree ecoulee.
        }
    }

    private static async Task RunWorkerAsync(
        HttpClient client,
        Options options,
        ClickSource source,
        Counters counters,
        SemaphoreSlim permits,
        CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await permits.WaitAsync(cancellationToken);

                (string jwt, ClickRequest click) = source.Next(options);

                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Post, "/api/clicks")
                    {
                        Content = JsonContent.Create(click),
                    };
                    request.Headers.Authorization = new("Bearer", jwt);

                    using HttpResponseMessage response = await client.SendAsync(request, cancellationToken);

                    counters.Record(response.StatusCode);
                }
                catch (Exception) when (!cancellationToken.IsCancellationRequested)
                {
                    counters.RecordFailure();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Duree ecoulee.
        }
    }

    private static async Task ReportAsync(Counters counters, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        long previous = 0;

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                long accepted = counters.Accepted;
                Console.WriteLine(
                    $"  {accepted,10:N0} acceptes  ({(accepted - previous) / 2.0,7:N0}/s)   " +
                    $"refuses {counters.Conflict,8:N0}   echecs {counters.Failed,6:N0}");
                previous = accepted;
            }
        }
        catch (OperationCanceledException)
        {
            // Duree ecoulee.
        }
    }
}

internal sealed record ClickRequest(float X, float Y);

/// <summary>Fabrique les positions selon le scenario demande.</summary>
internal sealed class ClickSource
{
    private readonly NormalizedPoint[]? _pool;
    private readonly RandomWorld? _world;
    private readonly string[] _jwts;
    private long _tick;

    public ClickSource(Options options)
    {
        // Un JWT par viewer, signe une fois : en signer un par clic couterait plus cher au
        // simulateur que le clic lui-meme au serveur. Le prefixe 'A' marque un viewer non
        // connecte, comme chez Twitch.
        _jwts = new string[options.Viewers];
        for (int viewer = 0; viewer < _jwts.Length; viewer++)
        {
            string userId = viewer % 100 < options.AnonymousPercent ? $"A{viewer}" : $"U{viewer}";
            _jwts[viewer] = TwitchTokens.Viewer(options.Secret, options.ChannelId, userId);
        }

        if (options.Scenario == "random")
        {
            _world = new RandomWorld(Environment.TickCount, options.Clusters, 0.08f);
        }
        else if (options.Scenario == "noise")
        {
            _pool = SyntheticData.UniformNoise(Math.Max(options.Viewers, 5_000));
        }
        else
        {
            Scenario scenario = Enum.Parse<Scenario>(options.Scenario, ignoreCase: true);
            _pool = SyntheticData.Generate(scenario, Math.Max(options.Viewers, 5_000));
        }
    }

    public (string Jwt, ClickRequest Click) Next(Options options)
    {
        NormalizedPoint point;

        if (_world is not null)
        {
            // La derive doit etre lente devant le debit, sinon la carte bouge trop vite
            // pour qu'un classement ait le temps de s'etablir.
            if (Interlocked.Increment(ref _tick) % 200 == 0)
            {
                lock (_world)
                {
                    _world.Drift(options.Volatility);
                }
            }

            lock (_world)
            {
                point = _world.Sample(0L);
            }
        }
        else
        {
            point = _pool![Random.Shared.Next(_pool.Length)];
        }

        return (_jwts[Random.Shared.Next(_jwts.Length)], new ClickRequest(point.X, point.Y));
    }
}

internal sealed class Counters
{
    private long _accepted;
    private long _conflict;
    private long _invalid;
    private long _failed;

    public long Accepted => Interlocked.Read(ref _accepted);

    public long Conflict => Interlocked.Read(ref _conflict);

    public long Invalid => Interlocked.Read(ref _invalid);

    public long Failed => Interlocked.Read(ref _failed);

    public void Record(System.Net.HttpStatusCode status)
    {
        switch ((int)status)
        {
            case 204:
                Interlocked.Increment(ref _accepted);
                break;
            case 409:
                Interlocked.Increment(ref _conflict);
                break;
            case 400:
                Interlocked.Increment(ref _invalid);
                break;
            default:
                Interlocked.Increment(ref _failed);
                break;
        }
    }

    public void RecordFailure() => Interlocked.Increment(ref _failed);
}

internal sealed record Options(
    string Url,
    string ChannelId,
    string Scenario,
    int Viewers,
    int Rate,
    TimeSpan Duration,
    int Workers,
    int Clusters,
    float Volatility,
    int AnonymousPercent,
    bool Start,
    string Secret)
{
    public const string Usage = """
        Usage : HitMapAggregator.Traffic simulate [options]
          --url <http://localhost:5000>   racine de l'API
          --channel <demo>                identifiant de canal
          --scenario <random>             random | noise | binaryvote | consensus |
                                          fragmented | elongated | lshape | concentric
          --viewers <20000>               nombre de viewers distincts
          --rate <1000>                   clics par seconde
          --duration <60>                 duree en secondes
          --workers <8>                   taches d'envoi paralleles
          --clusters <6>                  amas du monde aleatoire
          --volatility <0.012>            derive des parts par pas
          --anonymous <20>                pourcentage de viewers non connectes
          --no-start                      ne pas appeler /api/control/start
          --secret <base64>               secret d'extension qui signe les JWT
                                          (defaut : celui de appsettings.Development.json)
        """;

    public static Options Parse(string[] args)
    {
        string url = "http://localhost:5000";
        string channel = "demo";
        string scenario = "random";
        int viewers = 20_000;
        int rate = 1_000;
        int duration = 60;
        int workers = 8;
        int clusters = 6;
        float volatility = 0.012f;
        int anonymous = 20;
        bool start = true;
        string secret = TwitchTokens.DevelopmentSecret;

        for (int i = 0; i < args.Length; i++)
        {
            string key = args[i];
            string Value() => i + 1 < args.Length
                ? args[++i]
                : throw new ArgumentException($"valeur manquante pour {key}");

            switch (key)
            {
                case "--url": url = Value(); break;
                case "--channel": channel = Value(); break;
                case "--scenario": scenario = Value().ToLowerInvariant(); break;
                case "--viewers": viewers = int.Parse(Value(), CultureInfo.InvariantCulture); break;
                case "--rate": rate = int.Parse(Value(), CultureInfo.InvariantCulture); break;
                case "--duration": duration = int.Parse(Value(), CultureInfo.InvariantCulture); break;
                case "--workers": workers = int.Parse(Value(), CultureInfo.InvariantCulture); break;
                case "--clusters": clusters = int.Parse(Value(), CultureInfo.InvariantCulture); break;
                case "--volatility": volatility = float.Parse(Value(), CultureInfo.InvariantCulture); break;
                case "--anonymous": anonymous = int.Parse(Value(), CultureInfo.InvariantCulture); break;
                case "--no-start": start = false; break;
                case "--secret": secret = Value(); break;
                default: throw new ArgumentException($"option inconnue : {key}");
            }
        }

        return new Options(url, channel, scenario, viewers, rate, TimeSpan.FromSeconds(duration),
            workers, clusters, volatility, anonymous, start, secret);
    }
}
