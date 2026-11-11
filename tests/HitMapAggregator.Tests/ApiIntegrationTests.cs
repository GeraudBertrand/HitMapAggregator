using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using HitMapAggregator.Core;
using HitMapAggregator.Core.Models;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace HitMapAggregator.Tests;

/// <summary>
/// Chaine complete : ingestion HTTP, agregation par le superviseur, serialisation, SSE.
/// </summary>
/// <remarks>
/// Les tests passent par un serveur en memoire, mais par le vrai pipeline : meme
/// superviseur, meme boucle de tick, meme serialiseur, meme validation des JWT. Ce qui est
/// verifie ici est ce que l'overlay recevra.
/// </remarks>
public sealed class ApiIntegrationTests : IClassFixture<ApiFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly ApiFactory _factory;

    public ApiIntegrationTests(ApiFactory factory) => _factory = factory;

    [Fact]
    public async Task A_click_on_an_unknown_channel_is_refused()
    {
        using HttpClient client = _factory.CreateClient();

        HttpResponseMessage response = await Post(client, NewChannel(), "U1", 0.5f, 0.5f);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Theory]
    [InlineData(1.5f, 0.5f)]
    [InlineData(0.5f, -0.2f)]
    [InlineData(-0.0001f, 0.5f)]
    public async Task Coordinates_outside_the_unit_square_are_refused(float x, float y)
    {
        using HttpClient client = _factory.CreateClient();
        string channel = NewChannel();
        await Start(client, channel);

        HttpResponseMessage response = await Post(client, channel, "U1", x, y);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("1e999")]
    [InlineData("\"pas un nombre\"")]
    [InlineData("null")]
    public async Task Coordinates_that_are_not_usable_numbers_are_refused(string rawX)
    {
        // NaN ne peut pas voyager : JSON ne sait pas l'ecrire, et System.Text.Json refuse
        // meme de le serialiser. On envoie donc du JSON brut pour couvrir ce qui peut
        // reellement arriver — debordement, mauvais type, absence de valeur.
        using HttpClient client = _factory.CreateClient();
        string channel = NewChannel();
        await Start(client, channel);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/clicks")
        {
            Content = new StringContent($$"""{"x":{{rawX}},"y":0.5}""", Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = Bearer(ViewerJwt(channel, "U1"));

        using HttpResponseMessage response = await client.SendAsync(request, Token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_click_without_a_jwt_is_refused()
    {
        using HttpClient client = _factory.CreateClient();
        string channel = NewChannel();
        await Start(client, channel);

        using HttpResponseMessage response = await client.PostAsJsonAsync("/api/clicks", new { x = 0.5f, y = 0.5f }, Token);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_jwt_signed_with_another_secret_is_refused()
    {
        using HttpClient client = _factory.CreateClient();
        string channel = NewChannel();
        await Start(client, channel);

        string forged = TwitchTokens.Viewer(TwitchTokens.DevelopmentSecret, channel, "U1");

        Assert.Equal(HttpStatusCode.Unauthorized, (await Post(client, forged, 0.5f, 0.5f)).StatusCode);
    }

    [Fact]
    public async Task An_expired_jwt_is_refused()
    {
        using HttpClient client = _factory.CreateClient();
        string channel = NewChannel();
        await Start(client, channel);

        // Au-dela des 5 minutes de tolerance d'horloge par defaut.
        string expired = TwitchTokens.Create(ApiFactory.ExtensionSecret, channel, "U1", "viewer", DateTime.UtcNow.AddMinutes(-10));

        Assert.Equal(HttpStatusCode.Unauthorized, (await Post(client, expired, 0.5f, 0.5f)).StatusCode);
    }

    [Fact]
    public async Task The_channel_comes_from_the_jwt_and_never_from_the_body()
    {
        using HttpClient client = _factory.CreateClient();
        string open = NewChannel();
        string closed = NewChannel();
        await Start(client, open);

        // JWT du canal ferme, corps qui pretend viser le canal ouvert : le corps est ignore.
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/clicks")
        {
            Content = JsonContent.Create(new { channelId = open, userId = "U1", x = 0.5f, y = 0.5f }),
        };
        request.Headers.Authorization = Bearer(ViewerJwt(closed, "U1"));

        using HttpResponseMessage response = await client.SendAsync(request, Token);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Theory]
    [InlineData("viewer")]
    [InlineData("moderator")]
    public async Task Only_the_broadcaster_may_control_the_channel(string role)
    {
        using HttpClient client = _factory.CreateClient();
        string channel = NewChannel();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/control/start");
        request.Headers.Authorization = Bearer(
            TwitchTokens.Create(ApiFactory.ExtensionSecret, channel, "U1", role, DateTime.UtcNow.AddHours(1)));

        using HttpResponseMessage response = await client.SendAsync(request, Token);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Clicks_are_refused_once_the_collection_is_stopped_and_nothing_is_lost()
    {
        using HttpClient client = _factory.CreateClient();
        string channel = NewChannel();
        await Start(client, channel);

        Assert.Equal(HttpStatusCode.NoContent, (await Post(client, channel, "U1", 0.3f, 0.3f)).StatusCode);

        using (await Control(client, channel, HttpMethod.Post, "stop"))
        {
        }

        Assert.Equal(HttpStatusCode.Conflict, (await Post(client, channel, "U2", 0.5f, 0.5f)).StatusCode);

        // Le vote precedent survit a l'arret : stop gele, il n'efface pas.
        await Start(client, channel);
        Assert.Equal(HttpStatusCode.NoContent, (await Post(client, channel, "U3", 0.31f, 0.31f)).StatusCode);
    }

    [Fact]
    public async Task Stopping_publishes_the_clicks_accepted_since_the_last_tick()
    {
        using HttpClient client = _factory.CreateClient();
        string channel = NewChannel();
        await Start(client, channel);

        // Arret aussitot apres le clic, bien avant le premier tick (200 ms) : sans tick
        // final, aucun instantane n'existerait et la lecture expirerait.
        Assert.Equal(HttpStatusCode.NoContent, (await Post(client, channel, "U1", 0.4f, 0.4f)).StatusCode);
        using (await Control(client, channel, HttpMethod.Post, "stop"))
        {
        }

        JsonElement payload = await ReadSnapshot(client, channel, 1);
        Assert.Equal(1, payload.GetProperty("totalVotes").GetInt32());
    }

    [Fact]
    public async Task Two_click_clusters_reach_the_overlay_as_two_ranked_zones()
    {
        using HttpClient client = _factory.CreateClient();
        string channel = NewChannel();
        await Start(client, channel);

        // Deux amas nettement separes, l'un franchement majoritaire.
        NormalizedPoint[] points = SyntheticData.BinaryVote(3_000);
        for (int i = 0; i < points.Length; i++)
        {
            await Post(client, channel, $"U{i}", points[i].X, points[i].Y);
        }

        JsonElement payload = await ReadSnapshot(client, channel, points.Length);

        Assert.Equal("both", payload.GetProperty("renderMode").GetString());
        Assert.Equal(points.Length, payload.GetProperty("totalVotes").GetInt32());

        JsonElement zones = payload.GetProperty("zones");
        Assert.Equal(2, zones.GetArrayLength());

        // Contrat : liste deja classee, sans identifiant.
        float first = zones[0].GetProperty("pct").GetSingle();
        float second = zones[1].GetProperty("pct").GetSingle();
        Assert.True(first >= second, $"zones mal classees : {first} puis {second}");
        Assert.False(zones[0].TryGetProperty("id", out _), "les zones ne doivent porter aucun identifiant");

        double total = first + second + payload.GetProperty("scatteredPct").GetSingle();
        Assert.Equal(100.0, total, 0.2);
    }

    [Fact]
    public async Task The_quantised_grid_survives_the_round_trip()
    {
        using HttpClient client = _factory.CreateClient();
        string channel = NewChannel();
        await Start(client, channel);

        NormalizedPoint[] points = SyntheticData.Consensus(1_500);
        for (int i = 0; i < points.Length; i++)
        {
            await Post(client, channel, $"U{i}", points[i].X, points[i].Y);
        }

        JsonElement payload = await ReadSnapshot(client, channel, points.Length);
        JsonElement grid = payload.GetProperty("grid");

        Assert.Equal(Grid.Width, grid.GetProperty("w").GetInt32());
        Assert.Equal(Grid.Height, grid.GetProperty("h").GetInt32());
        Assert.True(grid.GetProperty("max").GetSingle() > 0f);

        byte[] cells = Convert.FromBase64String(grid.GetProperty("data").GetString()!);
        Assert.Equal(Grid.CellCount, cells.Length);

        // Le maximum voyage a part : au moins une cellule doit etre saturee a 255.
        Assert.Contains(cells, static cell => cell == 255);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("mauvaise-cle-de-metriques")]
    public async Task Metrics_require_the_key(string? key)
    {
        using HttpClient client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/metrics");
        if (key is not null)
        {
            request.Headers.Add("X-Metrics-Key", key);
        }

        using HttpResponseMessage response = await client.SendAsync(request, Token);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Rotating_the_overlay_token_closes_the_streams_already_open()
    {
        using HttpClient client = _factory.CreateClient();
        string channel = NewChannel();
        await Start(client, channel);
        string token = await OverlayToken(client, channel);

        using HttpResponseMessage stream = await client.GetAsync(
            $"/api/stream/heatmap?channelId={channel}&token={token}",
            HttpCompletionOption.ResponseHeadersRead,
            Token);
        stream.EnsureSuccessStatusCode();

        // L'abonnement ne s'inscrit qu'a la premiere lecture du flux : attendre un premier
        // instantane garantit que la rotation trouvera bien cet abonne.
        await using Stream body = await stream.Content.ReadAsStreamAsync(Token);
        using var reader = new StreamReader(body);
        while (await reader.ReadLineAsync(Token) is { } line && !line.StartsWith("data:", StringComparison.Ordinal))
        {
        }

        using (await Control(client, channel, HttpMethod.Post, "overlay-token/rotate"))
        {
        }

        // Le flux doit se terminer de lui-meme ; s'il restait ouvert, le delai expirerait.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await reader.ReadToEndAsync(timeout.Token);
    }

    [Fact]
    public async Task The_stream_refuses_a_missing_or_foreign_overlay_token()
    {
        using HttpClient client = _factory.CreateClient();
        string channel = NewChannel();
        string other = NewChannel();
        await Start(client, channel);

        string otherToken = await OverlayToken(client, other);

        Assert.Equal(HttpStatusCode.Unauthorized, await StreamStatus(client, channel, null));
        Assert.Equal(HttpStatusCode.Unauthorized, await StreamStatus(client, channel, otherToken));
        Assert.Equal(HttpStatusCode.OK, await StreamStatus(client, channel, await OverlayToken(client, channel)));
    }

    [Fact]
    public async Task Rotating_the_overlay_token_revokes_the_previous_one()
    {
        using HttpClient client = _factory.CreateClient();
        string channel = NewChannel();
        await Start(client, channel);

        string before = await OverlayToken(client, channel);

        using HttpResponseMessage rotated = await Control(client, channel, HttpMethod.Post, "overlay-token/rotate");
        string after = (await rotated.Content.ReadFromJsonAsync<JsonElement>(Token)).GetProperty("token").GetString()!;

        Assert.NotEqual(before, after);
        Assert.Equal(HttpStatusCode.Unauthorized, await StreamStatus(client, channel, before));
        Assert.Equal(HttpStatusCode.OK, await StreamStatus(client, channel, after));
    }

    [Fact]
    public async Task The_overlay_token_never_reaches_the_logs()
    {
        using HttpClient client = _factory.CreateClient();
        string channel = NewChannel();
        await Start(client, channel);
        string token = await OverlayToken(client, channel);

        // Les trois chemins par lesquels l'URL secrete arrive au serveur : la page, le
        // flux accepte, et un flux refuse (un jeton perime peut fuiter dans un message d'erreur).
        using (await client.GetAsync($"/overlay/?channel={channel}&token={token}", Token))
        {
        }

        Assert.Equal(HttpStatusCode.OK, await StreamStatus(client, channel, token));
        Assert.Equal(HttpStatusCode.Unauthorized, await StreamStatus(client, channel, token + "x"));

        // Sans entree du tout, le test passerait sans rien prouver.
        Assert.NotEmpty(_factory.Logs.Messages);
        Assert.DoesNotContain(_factory.Logs.Messages, message => message.Contains(token, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_settings_patch_only_touches_the_fields_it_carries()
    {
        using HttpClient client = _factory.CreateClient();
        string channel = NewChannel();
        await Start(client, channel);

        using HttpResponseMessage response = await Control(
            client, channel, HttpMethod.Post, "settings", new { smoothingRadius = 7, renderMode = "contour" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        JsonElement settings = await response.Content.ReadFromJsonAsync<JsonElement>(Token);
        Assert.Equal(7, settings.GetProperty("smoothingRadius").GetInt32());
        Assert.Equal("Contour", settings.GetProperty("renderMode").GetString());

        // Les reglages absents du patch gardent leur valeur, ils ne repartent pas au defaut.
        Assert.Equal(0.13f, settings.GetProperty("threshold").GetSingle(), 1e-4f);
        Assert.Equal(3, settings.GetProperty("majorZones").GetInt32());
    }

    [Fact]
    public async Task Settings_can_be_set_before_the_first_start_without_opening_the_collection()
    {
        using HttpClient client = _factory.CreateClient();
        string channel = NewChannel();

        using (HttpResponseMessage response = await Control(client, channel, HttpMethod.Post, "settings", new { renderMode = "ellipse" }))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        Assert.Equal(HttpStatusCode.Conflict, (await Post(client, channel, "U1", 0.5f, 0.5f)).StatusCode);

        await Start(client, channel);
        JsonElement state = await State(client, channel);
        Assert.Equal("Ellipse", state.GetProperty("settings").GetProperty("renderMode").GetString());
    }

    [Fact]
    public async Task An_out_of_range_setting_is_refused()
    {
        using HttpClient client = _factory.CreateClient();
        string channel = NewChannel();
        await Start(client, channel);

        using HttpResponseMessage response = await Control(client, channel, HttpMethod.Post, "settings", new { maxZones = 9_999 });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task The_state_reports_whether_the_collection_runs()
    {
        using HttpClient client = _factory.CreateClient();
        string channel = NewChannel();

        // Avant tout demarrage, les reglages existent deja : la configuration se fait hors live.
        JsonElement before = await State(client, channel);
        Assert.False(before.GetProperty("running").GetBoolean());
        Assert.Equal(JsonValueKind.Object, before.GetProperty("settings").ValueKind);

        await Start(client, channel);

        JsonElement after = await State(client, channel);
        Assert.True(after.GetProperty("running").GetBoolean());
        Assert.Equal(channel, after.GetProperty("channelId").GetString());
        Assert.Equal(JsonValueKind.Object, after.GetProperty("settings").ValueKind);
    }

    [Fact]
    public async Task Reset_empties_the_channel()
    {
        using HttpClient client = _factory.CreateClient();
        string channel = NewChannel();
        await Start(client, channel);

        for (int i = 0; i < 50; i++)
        {
            await Post(client, channel, $"U{i}", 0.4f, 0.4f);
        }

        using HttpResponseMessage response = await Control(client, channel, HttpMethod.Post, "reset");
        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>(Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, body.GetProperty("positions").GetInt32());
    }

    [Fact]
    public async Task Metrics_report_the_ingestion_and_the_ticks()
    {
        using HttpClient client = _factory.CreateClient();
        string channel = NewChannel();
        await Start(client, channel);

        for (int i = 0; i < 20; i++)
        {
            await Post(client, channel, $"U{i}", 0.5f, 0.5f);
        }

        // Laisse passer quelques ticks.
        await Task.Delay(700, Token);

        client.DefaultRequestHeaders.Add("X-Metrics-Key", ApiFactory.MetricsKey);
        JsonElement report = await client.GetFromJsonAsync<JsonElement>("/api/metrics", Json, Token);

        Assert.True(report.GetProperty("clicksAccepted").GetInt64() >= 20);
        Assert.True(report.GetProperty("liveChannels").GetInt32() >= 1);

        JsonElement channels = report.GetProperty("channels");
        bool found = false;
        foreach (JsonElement entry in channels.EnumerateArray())
        {
            if (entry.GetProperty("channelId").GetString() == channel)
            {
                Assert.True(entry.GetProperty("ticks").GetInt64() > 0);
                Assert.True(entry.GetProperty("snapshotBytes").GetInt32() > 0);

                // Un clic attend au plus un tick (200 ms) avant publication.
                double clickAge = entry.GetProperty("clickAgeP50Ms").GetDouble();
                Assert.InRange(clickAge, 0.001, 1000);
                found = true;
            }
        }

        Assert.True(found, $"le canal {channel} n'apparait pas dans les metriques");
    }

    /// <summary>Jeton du test courant : une annulation doit couper les appels en vol.</summary>
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static string NewChannel() => $"c{Guid.NewGuid():N}";

    private static AuthenticationHeaderValue Bearer(string jwt) => new("Bearer", jwt);

    private static string ViewerJwt(string channel, string user) =>
        TwitchTokens.Viewer(ApiFactory.ExtensionSecret, channel, user);

    private static async Task<HttpResponseMessage> Control(
        HttpClient client, string channel, HttpMethod method, string action, object? body = null)
    {
        using var request = new HttpRequestMessage(method, $"/api/control/{action}")
        {
            Content = body is null ? null : JsonContent.Create(body),
        };
        request.Headers.Authorization = Bearer(TwitchTokens.Broadcaster(ApiFactory.ExtensionSecret, channel));

        return await client.SendAsync(request, Token);
    }

    private static async Task Start(HttpClient client, string channel)
    {
        using HttpResponseMessage response = await Control(client, channel, HttpMethod.Post, "start");
        response.EnsureSuccessStatusCode();
    }

    private static async Task<JsonElement> State(HttpClient client, string channel)
    {
        using HttpResponseMessage response = await Control(client, channel, HttpMethod.Get, "state");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>(Token);
    }

    private static async Task<string> OverlayToken(HttpClient client, string channel)
    {
        using HttpResponseMessage response = await Control(client, channel, HttpMethod.Get, "overlay-token");
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>(Token)).GetProperty("token").GetString()!;
    }

    private static Task<HttpResponseMessage> Post(HttpClient client, string channel, string user, float x, float y) =>
        Post(client, ViewerJwt(channel, user), x, y);

    private static async Task<HttpResponseMessage> Post(HttpClient client, string jwt, float x, float y)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/clicks")
        {
            Content = JsonContent.Create(new { x, y }),
        };
        request.Headers.Authorization = Bearer(jwt);

        return await client.SendAsync(request, Token);
    }

    /// <summary>Ouvre le flux, lit le statut, referme aussitot.</summary>
    private static async Task<HttpStatusCode> StreamStatus(HttpClient client, string channel, string? overlayToken)
    {
        string query = overlayToken is null ? "" : $"&token={overlayToken}";

        using HttpResponseMessage response = await client.GetAsync(
            $"/api/stream/heatmap?channelId={channel}{query}",
            HttpCompletionOption.ResponseHeadersRead,
            Token);

        return response.StatusCode;
    }

    /// <summary>
    /// Lit sur le flux SSE jusqu'a l'instantane qui totalise <paramref name="expectedVotes"/> clics.
    /// </summary>
    /// <remarks>
    /// Le premier message n'est pas forcement frais : le diffuseur rejoue au nouvel abonne
    /// le dernier instantane connu, qui peut dater d'un tick pris pendant que les clics
    /// arrivaient encore. Attendre le compte attendu est la seule condition d'arret juste.
    /// </remarks>
    private static async Task<JsonElement> ReadSnapshot(HttpClient client, string channel, int expectedVotes)
    {
        string overlayToken = await OverlayToken(client, channel);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        using HttpResponseMessage response = await client.GetAsync(
            $"/api/stream/heatmap?channelId={channel}&token={overlayToken}",
            HttpCompletionOption.ResponseHeadersRead,
            timeout.Token);

        response.EnsureSuccessStatusCode();

        await using Stream stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var reader = new StreamReader(stream);

        while (await reader.ReadLineAsync(timeout.Token) is { } line)
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal))
            {
                continue;
            }

            JsonElement payload = JsonSerializer.Deserialize<JsonElement>(line["data:".Length..].Trim());
            if (payload.GetProperty("totalVotes").GetInt32() == expectedVotes)
            {
                return payload;
            }
        }

        throw new InvalidOperationException($"aucun instantane a {expectedVotes} clics recu sur le flux SSE");
    }
}

/// <summary>
/// Serveur de test avec ses propres secrets, distincts de ceux de developpement.
/// </summary>
/// <remarks>
/// Des secrets distincts permettent de verifier qu'un JWT signe avec le secret de
/// developpement est bien refuse. Les niveaux de journalisation restent ceux de
/// appsettings : c'est la configuration livree que le test de non-fuite controle.
/// </remarks>
public sealed class ApiFactory : WebApplicationFactory<Program>
{
    public static readonly string ExtensionSecret = Convert.ToBase64String(Enumerable.Repeat((byte)0x5A, 32).ToArray());

    public const string MetricsKey = "cle-de-metriques-des-tests";

    private static readonly string OverlaySecret = Convert.ToBase64String(Enumerable.Repeat((byte)0xA5, 32).ToArray());

    // Un fichier par fabrique : les tests ne doivent ni lire ni salir celui du projet.
    private readonly string _generations = Path.Combine(Path.GetTempPath(), $"hitmap-generations-{Guid.NewGuid():N}.json");

    public LogCapture Logs { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration(configuration => configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Secrets:TwitchExtension"] = ExtensionSecret,
            ["Secrets:OverlayToken"] = OverlaySecret,
            ["Secrets:MetricsKey"] = MetricsKey,
            ["Overlay:GenerationsPath"] = _generations,
        }));

        builder.ConfigureLogging(logging => logging.AddProvider(Logs));
    }
}

public sealed class LogCapture : ILoggerProvider
{
    private readonly ConcurrentQueue<string> _messages = new();

    public IReadOnlyCollection<string> Messages => _messages;

    public ILogger CreateLogger(string categoryName) => new Logger(categoryName, _messages);

    public void Dispose()
    {
    }

    private sealed class Logger(string category, ConcurrentQueue<string> messages) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            messages.Enqueue($"{category} {formatter(state, exception)} {exception}");
    }
}
