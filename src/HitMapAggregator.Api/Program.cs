using System.Text.Json.Serialization;
using HitMapAggregator.Api.Diagnostics;
using HitMapAggregator.Api.Endpoints;
using HitMapAggregator.Api.Hosting;
using HitMapAggregator.Api.Realtime;
using HitMapAggregator.Api.Security;
using HitMapAggregator.Core.Session;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// Sans ca, RenderMode ressort en entier dans les reponses de controle : illisible pour
// le client, et incoherent avec le flux SSE qui l'envoie deja en texte.
builder.Services.ConfigureHttpJsonOptions(static options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

builder.Services.AddSingleton<ChannelSessionRegistry>();
builder.Services.AddSingleton<SnapshotBroadcaster>();
builder.Services.AddSingleton<SnapshotSerializer>();
builder.Services.AddSingleton<ChannelMetrics>();

// Le superviseur est resolu par les endpoints ET pilote par l'hote : une seule instance,
// enregistree deux fois.
builder.Services.AddSingleton<ChannelSupervisor>();
builder.Services.AddHostedService(static provider => provider.GetRequiredService<ChannelSupervisor>());

builder.Services.AddTwitchAuthentication();
builder.Services.AddSingleton(static provider =>
{
    IConfiguration configuration = provider.GetRequiredService<IConfiguration>();
    string path = Path.Combine(
        provider.GetRequiredService<IHostEnvironment>().ContentRootPath,
        configuration["Overlay:GenerationsPath"] ?? "data/overlay-generations.json");

    return new OverlayTokens(provider.GetRequiredService<IOptions<HitMapSecrets>>(), path);
});

bool isDevelopment = builder.Environment.IsDevelopment();
builder.Services.AddCors(options => options.AddDefaultPolicy(policy => policy
    .SetIsOriginAllowed(origin => TwitchAuthentication.IsAllowedOrigin(origin, isDevelopment))
    .WithHeaders("Authorization", "Content-Type")
    .WithMethods("GET", "POST")));

WebApplication app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

// Local Test Twitch : la « Testing Base URI » doit etre en HTTPS. Servir l'extension depuis
// l'EBS la place sur la meme origine, donc sans CORS. Jamais hors Development : en hosted
// test et en production, Twitch heberge ces fichiers sur son CDN.
string extensionRoot = Path.GetFullPath(Path.Combine(app.Environment.ContentRootPath, "..", "..", "extension"));
if (isDevelopment && Directory.Exists(extensionRoot))
{
    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = new PhysicalFileProvider(extensionRoot),
        RequestPath = "/extension",
        // Fichiers edites pendant le Local Test : sans ceci Chrome garde l'ancienne version.
        OnPrepareResponse = context => context.Context.Response.Headers.CacheControl = "no-cache",
    });
}

app.UseCors();
app.UseAuthentication();
app.UseAuthorization();

app.MapClicks();
app.MapControl();
app.MapOverlayStream();
app.MapMetrics();

app.Run();

/// <summary>Point d'ancrage pour les tests d'integration.</summary>
public partial class Program;
