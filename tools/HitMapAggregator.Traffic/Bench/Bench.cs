using System.Globalization;
using System.Net.ServerSentEvents;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using HitMapAggregator.Testing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace HitMapAggregator.Traffic.Bench;

/// <summary>
/// Banc de mesure : les quatre configs, chacune sur un jeu connu puis sur le monde
/// aleatoire, avec une page en direct et un rapport HTML autonome a la fin.
/// </summary>
internal static class Bench
{
    public static async Task<int> RunAsync(string[] args)
    {
        BenchOptions options;
        try
        {
            options = BenchOptions.Parse(args);
        }
        catch (ArgumentException exception)
        {
            Console.Error.WriteLine(exception.Message);
            Console.Error.WriteLine(BenchOptions.Usage);
            return 1;
        }

        Console.OutputEncoding = Encoding.UTF8;

        var run = new BenchRun
        {
            StartedAt = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
            Machine = $"{Environment.ProcessorCount} cœurs logiques · {RuntimeInformation.OSDescription} · " +
                      $"{RuntimeInformation.FrameworkDescription} · client et serveur sur la même machine",
            PassSeconds = options.Duration.TotalSeconds,
        };

        var hub = new LiveHub(run);
        string page = Page.Build();

        WebApplication live = LiveServer(options.Port, hub, page);
        await live.StartAsync();
        Console.WriteLine($"page en direct : http://localhost:{options.Port}/");

        using var http = new HttpClient(new SocketsHttpHandler { MaxConnectionsPerServer = 128 })
        {
            BaseAddress = new Uri(options.Url),
            Timeout = TimeSpan.FromSeconds(10),
        };
        using var stream = new HttpClient { BaseAddress = new Uri(options.Url), Timeout = Timeout.InfiniteTimeSpan };

        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);

        Console.WriteLine("échauffement (non compté)...");
        await new BenchPass(options with { Duration = TimeSpan.FromSeconds(15) }, http, stream, hub)
            .RunAsync(BenchConfig.WarmUp, Distribution.Random, NewPass($"bench-{stamp}-warmup", BenchConfig.WarmUp, Distribution.Random, null), 7, record: false);

        hub.SetStatus("running");
        int seed = 1729;

        foreach (BenchConfig config in BenchConfig.All.Where(c => options.Only is null || c.Id == options.Only))
        {
            foreach (Distribution distribution in (Distribution[])[Distribution.Known, Distribution.Random])
            {
                ClickPlan preview = config.CreatePlan(distribution, options.Duration, seed);
                PassResult result = NewPass($"bench-{stamp}-{config.Id}-{distribution.ToString().ToLowerInvariant()}", config, distribution, preview);

                Console.WriteLine($"{config.Title} · {result.Distribution}...");
                hub.StartPass(result);

                await new BenchPass(options, http, stream, hub).RunAsync(config, distribution, result, seed++, record: true);

                hub.FinishPass(result);
                PrintResult(result);
            }
        }

        hub.SetStatus("done");
        string report = WriteReport(options.Output, stamp, hub, page);
        Console.WriteLine($"rapport : {report}");

        hub.Complete();
        await live.StopAsync();

        return run.Passes.All(p => p.Checks.All(c => c.Ok)) ? 0 : 2;
    }

    private static PassResult NewPass(string id, BenchConfig config, Distribution distribution, ClickPlan? plan) => new()
    {
        Id = id,
        Config = config.Id,
        Title = config.Title,
        Description = config.Description,
        Distribution = distribution == Distribution.Known ? $"Jeu connu · {config.KnownScenario}" : "Monde aléatoire dérivant",
        Audience = plan?.Audience ?? 0,
        Clickers = plan?.Clickers ?? 0,
        StopAtS = plan?.StopAtMs / 1000.0,
        RestartAtS = plan?.RestartAtMs / 1000.0,
    };

    private static void PrintResult(PassResult result)
    {
        if (result.Summary is { } s)
        {
            Console.WriteLine(
                $"  {s.Votes:N0} votes  {s.Accepted:N0} acceptes  {s.Conflicts:N0} 409  " +
                $"tick p99 {s.TickP99Ms:F2} ms  clic→publication p99 {s.ClickAgeP99Ms:F0} ms  POST p99 {s.PostP99Ms:F1} ms");
        }

        foreach (Check check in result.Checks)
        {
            Console.WriteLine($"  [{(check.Ok ? "ok" : "ECHEC")}] {check.Label} : {check.Detail}");
        }
    }

    private static string WriteReport(string directory, string stamp, LiveHub hub, string page)
    {
        Directory.CreateDirectory(directory);
        string json = hub.Serialize();

        string html = Path.GetFullPath(Path.Combine(directory, $"bench-{stamp}.html"));
        File.WriteAllText(html, page.Replace("/*@DATA@*/null", json, StringComparison.Ordinal));
        File.WriteAllText(Path.ChangeExtension(html, ".json"), json);
        return html;
    }

    private static WebApplication LiveServer(int port, LiveHub hub, string page)
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.WebHost.UseUrls($"http://localhost:{port}");

        WebApplication app = builder.Build();
        app.MapGet("/", () => Results.Content(page, "text/html; charset=utf-8"));
        app.MapGet("/events", (CancellationToken cancellationToken) => TypedResults.ServerSentEvents(hub.Subscribe(cancellationToken)));
        return app;
    }
}

/// <summary>
/// Tient le modele du banc et le pousse a la page en direct.
/// </summary>
/// <remarks>
/// Un seul verrou pour les mutations et la serialisation : les passes ecrivent depuis
/// plusieurs taches pendant qu'une page peut se connecter et demander l'etat complet.
/// </remarks>
internal sealed class LiveHub(BenchRun run)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly Lock _gate = new();
    private readonly List<Channel<SseItem<string>>> _subscribers = [];
    private readonly Dictionary<string, long> _lastSnapshotPush = [];

    public void SetStatus(string status) => Mutate("status", () => run.Status = status, () => status);

    public void StartPass(PassResult pass) => Mutate("pass", () => run.Passes.Add(pass), () => pass);

    public void FinishPass(PassResult pass) => Mutate("pass", () => pass.Status = "done", () => pass);

    public void AddSample(PassResult pass, Sample sample) =>
        Mutate("sample", () => pass.Samples.Add(sample), () => new { id = pass.Id, sample });

    public void SetSnapshot(PassResult pass, JsonElement snapshot)
    {
        lock (_gate)
        {
            pass.Snapshot = snapshot;

            // Cinq instantanes par seconde, c'est trop pour redessiner trois canevas en
            // continu ; deux suffisent a l'oeil.
            long now = Environment.TickCount64;
            if (_lastSnapshotPush.TryGetValue(pass.Id, out long last) && now - last < 500)
            {
                return;
            }

            _lastSnapshotPush[pass.Id] = now;
            Broadcast("snapshot", new { id = pass.Id, snapshot });
        }
    }

    /// <summary>Modifie le modele sous le verrou, sans rien pousser.</summary>
    public void Update(Action mutation)
    {
        lock (_gate)
        {
            mutation();
        }
    }

    public string Serialize()
    {
        lock (_gate)
        {
            return JsonSerializer.Serialize(run, Json);
        }
    }

    public async IAsyncEnumerable<SseItem<string>> Subscribe([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Channel<SseItem<string>> channel = Channel.CreateUnbounded<SseItem<string>>();
        lock (_gate)
        {
            channel.Writer.TryWrite(new SseItem<string>(JsonSerializer.Serialize(run, Json), "state"));
            _subscribers.Add(channel);
        }

        try
        {
            await foreach (SseItem<string> item in channel.Reader.ReadAllAsync(cancellationToken))
            {
                yield return item;
            }
        }
        finally
        {
            lock (_gate)
            {
                _subscribers.Remove(channel);
            }
        }
    }

    public void Complete()
    {
        lock (_gate)
        {
            foreach (Channel<SseItem<string>> subscriber in _subscribers)
            {
                subscriber.Writer.TryComplete();
            }
        }
    }

    private void Mutate(string eventType, Action mutation, Func<object> payload)
    {
        lock (_gate)
        {
            mutation();
            Broadcast(eventType, payload());
        }
    }

    private void Broadcast(string eventType, object payload)
    {
        string json = JsonSerializer.Serialize(payload, Json);
        foreach (Channel<SseItem<string>> subscriber in _subscribers)
        {
            subscriber.Writer.TryWrite(new SseItem<string>(json, eventType));
        }
    }
}

/// <summary>La page du banc, avec les rendus de l'overlay incorpores.</summary>
internal static class Page
{
    private static readonly string[] RendererOrder = ["labels.js", "ellipse.js", "contour.js", "index.js"];

    public static string Build() =>
        Resource("bench.html").Replace("/*@RENDERERS@*/", Renderers(), StringComparison.Ordinal);

    /// <summary>
    /// Assemble les modules ES de l'overlay en un script classique.
    /// </summary>
    /// <remarks>
    /// Un rapport ouvert en <c>file://</c> ne peut pas charger de module : on retire donc
    /// les <c>import</c> et les <c>export</c>, dans l'ordre des dependances, au lieu de
    /// maintenir une copie des rendus qui finirait par diverger de l'overlay.
    /// </remarks>
    private static string Renderers()
    {
        var bundle = new StringBuilder("(() => {\n");
        foreach (string file in RendererOrder)
        {
            foreach (string line in Resource($"renderers/{file}").Split('\n'))
            {
                if (line.StartsWith("import ", StringComparison.Ordinal))
                {
                    continue;
                }

                if (line.Contains("export ", StringComparison.Ordinal) && !line.StartsWith("export function ", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException($"forme d'export non geree dans {file} : {line.Trim()}");
                }

                bundle.Append(line.StartsWith("export ", StringComparison.Ordinal) ? line[7..] : line).Append('\n');
            }
        }

        return bundle.Append("window.renderOverlay = render;\n})();").ToString();
    }

    private static string Resource(string name)
    {
        using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"ressource absente : {name}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}

internal sealed record BenchOptions(
    string Url,
    TimeSpan Duration,
    int Port,
    string Output,
    string? Only,
    string Secret,
    string MetricsKey)
{
    public const string Usage = """
        Usage : HitMapAggregator.Traffic bench [options]
          --url <http://localhost:5080>   racine de l'API (lancee a part, en Release)
          --duration <60>                 duree d'une passe en secondes (minimum 20)
          --port <5090>                   port de la page en direct
          --out <artifacts/bench>         dossier du rapport
          --only <10k|1k50|50k|400>       une seule config
          --secret <base64>               secret d'extension (defaut : developpement)
          --metrics-key <cle>             cle de /api/metrics (defaut : developpement)
        """;

    public static BenchOptions Parse(string[] args)
    {
        string url = "http://localhost:5080";
        int duration = 60;
        int port = 5090;
        string output = Path.Combine("artifacts", "bench");
        string? only = null;
        string secret = TwitchTokens.DevelopmentSecret;
        string metricsKey = "dev-metrics-key-local-only";

        for (int i = 0; i < args.Length; i++)
        {
            string key = args[i];
            string Value() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"valeur manquante pour {key}");

            switch (key)
            {
                case "--url": url = Value(); break;
                case "--duration": duration = int.Parse(Value(), CultureInfo.InvariantCulture); break;
                case "--port": port = int.Parse(Value(), CultureInfo.InvariantCulture); break;
                case "--out": output = Value(); break;
                case "--only": only = Value(); break;
                case "--secret": secret = Value(); break;
                case "--metrics-key": metricsKey = Value(); break;
                default: throw new ArgumentException($"option inconnue : {key}");
            }
        }

        // En deca, la config 400 n'a plus la place pour ses 10 s d'arret.
        if (duration < 20)
        {
            throw new ArgumentException("--duration doit valoir au moins 20 s");
        }

        if (only is not null && BenchConfig.All.All(c => c.Id != only))
        {
            throw new ArgumentException($"config inconnue : {only}");
        }

        return new BenchOptions(url, TimeSpan.FromSeconds(duration), port, output, only, secret, metricsKey);
    }
}
