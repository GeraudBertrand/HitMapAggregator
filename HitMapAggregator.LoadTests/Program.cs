using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using NBomber.Contracts;
using NBomber.Contracts.Stats;
using NBomber.CSharp;

namespace HitMapAggregator.LoadTests;

/// <summary>
/// Montee en charge sur /api/clicks, jusqu'a 20 000 clients virtuels.
/// </summary>
/// <remarks>
/// Le chiffre qui compte n'est pas le debit encaisse mais la <b>duree du tick</b> : c'est
/// elle qui dira s'il faut un jour paralleliser les canaux. Le seuil d'alerte est un p99
/// de 30 ms, pour un budget de 200 ms par tick.
///
/// Les clients virtuels partagent un pool de connexions. Sans keep-alive, 20 000 sockets
/// simultanees vers localhost epuiseraient la plage de ports ephemeres de Windows
/// (16 384 par defaut) et on mesurerait la pile TCP, pas le serveur.
/// </remarks>
internal static class Program
{
    private const double TickBudgetMs = 30.0;

    private static async Task<int> Main(string[] args)
    {
        string url = Argument(args, "--url") ?? "http://localhost:5080";
        string channel = Argument(args, "--channel") ?? "load";
        int clients = int.Parse(Argument(args, "--clients") ?? "20000", CultureInfo.InvariantCulture);
        int rampSeconds = int.Parse(Argument(args, "--ramp") ?? "30", CultureInfo.InvariantCulture);
        int holdSeconds = int.Parse(Argument(args, "--hold") ?? "60", CultureInfo.InvariantCulture);
        int viewers = int.Parse(Argument(args, "--viewers") ?? "20000", CultureInfo.InvariantCulture);
        int connections = int.Parse(Argument(args, "--connections") ?? "512", CultureInfo.InvariantCulture);

        using var client = new HttpClient(new SocketsHttpHandler
        {
            MaxConnectionsPerServer = connections,
            PooledConnectionLifetime = TimeSpan.FromMinutes(30),
            EnableMultipleHttp2Connections = true,
        })
        {
            BaseAddress = new Uri(url),
            Timeout = TimeSpan.FromSeconds(20),
        };

        Console.WriteLine(
            $"cible {url}  canal={channel}  {clients:N0} clients virtuels  " +
            $"{viewers:N0} viewers  pool de {connections:N0} connexions");

        string secret = Argument(args, "--secret") ?? HitMapAggregator.Tests.TwitchTokens.DevelopmentSecret;

        // Signes avant la charge : signer pendant fausserait la mesure cote client.
        string[] jwts = new string[viewers];
        for (int viewer = 0; viewer < viewers; viewer++)
        {
            jwts[viewer] = HitMapAggregator.Tests.TwitchTokens.Viewer(
                secret, channel, viewer % 5 == 0 ? $"A{viewer}" : $"U{viewer}");
        }

        using var startRequest = new HttpRequestMessage(HttpMethod.Post, "/api/control/start");
        startRequest.Headers.Authorization = new("Bearer", HitMapAggregator.Tests.TwitchTokens.Broadcaster(secret, channel));

        using (HttpResponseMessage start = await client.SendAsync(startRequest))
        {
            if (!start.IsSuccessStatusCode)
            {
                Console.Error.WriteLine($"impossible de demarrer le canal : {(int)start.StatusCode}");
                Console.Error.WriteLine("l'API tourne-t-elle sur cette adresse ?");
                return 1;
            }
        }

        ScenarioProps scenario = Scenario.Create("clicks", async _ =>
            {
                // Chaque requete se fait passer pour un viewer tire au hasard : c'est ce
                // qui remplit le dictionnaire jusqu'a la taille visee.
                using var request = new HttpRequestMessage(HttpMethod.Post, "/api/clicks")
                {
                    Content = JsonContent.Create(new { x = Random.Shared.NextSingle(), y = Random.Shared.NextSingle() }),
                };
                request.Headers.Authorization = new("Bearer", jwts[Random.Shared.Next(viewers)]);

                using HttpResponseMessage response = await client.SendAsync(request);
                return response.IsSuccessStatusCode ? Response.Ok() : Response.Fail(statusCode: ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture));
            })
            .WithoutWarmUp()
            .WithLoadSimulations(
                Simulation.RampingConstant(copies: clients, during: TimeSpan.FromSeconds(rampSeconds)),
                Simulation.KeepConstant(copies: clients, during: TimeSpan.FromSeconds(holdSeconds)));

        NBomberRunner
            .RegisterScenarios(scenario)
            .WithReportFolder("artifacts/loadtests")
            .WithReportFormats(ReportFormat.Txt, ReportFormat.Md)
            .Run();

        // Defaut : la cle de appsettings.Development.json.
        client.DefaultRequestHeaders.Add("X-Metrics-Key", Argument(args, "--metrics-key") ?? "dev-metrics-key-local-only");

        return await ReportTickCost(client, channel);
    }

    /// <summary>
    /// Lit les compteurs du serveur et rend le verdict sur la duree du tick.
    /// </summary>
    private static async Task<int> ReportTickCost(HttpClient client, string channel)
    {
        JsonElement report = await client.GetFromJsonAsync<JsonElement>("/api/metrics");

        Console.WriteLine();
        Console.WriteLine("--- cout du tick, mesure par le serveur ---");
        Console.WriteLine($"clics acceptes  : {report.GetProperty("clicksAccepted").GetInt64():N0}");
        Console.WriteLine($"refuses (arret) : {report.GetProperty("clicksRejectedStopped").GetInt64():N0}");
        Console.WriteLine($"invalides       : {report.GetProperty("clicksRejectedInvalid").GetInt64():N0}");

        double worst = 0.0;

        foreach (JsonElement entry in report.GetProperty("channels").EnumerateArray())
        {
            double p50 = entry.GetProperty("tickP50Ms").GetDouble();
            double p99 = entry.GetProperty("tickP99Ms").GetDouble();
            double max = entry.GetProperty("tickMaxMs").GetDouble();

            Console.WriteLine(
                $"{entry.GetProperty("channelId").GetString(),-12} " +
                $"p50 {p50,7:F2} ms   p99 {p99,7:F2} ms   max {max,7:F2} ms   " +
                $"{entry.GetProperty("positions").GetInt32(),7:N0} positions   " +
                $"{entry.GetProperty("snapshotBytes").GetInt32(),7:N0} octets/tick");

            worst = Math.Max(worst, p99);
        }

        Console.WriteLine();

        if (worst > TickBudgetMs)
        {
            Console.WriteLine($"ALERTE : p99 du tick a {worst:F2} ms, au-dela du seuil de {TickBudgetMs:F0} ms.");
            Console.WriteLine("A signaler avant de passer a l'etape suivante.");
            return 2;
        }

        Console.WriteLine($"p99 du tick a {worst:F2} ms, sous le seuil de {TickBudgetMs:F0} ms.");
        return 0;
    }

    private static string? Argument(string[] args, string name)
    {
        int index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}
