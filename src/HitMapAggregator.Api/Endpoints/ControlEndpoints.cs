using System.Security.Claims;
using HitMapAggregator.Api.Hosting;
using HitMapAggregator.Api.Realtime;
using HitMapAggregator.Api.Security;
using HitMapAggregator.Core.Aggregation;
using HitMapAggregator.Core.Models;
using HitMapAggregator.Core.Session;

namespace HitMapAggregator.Api.Endpoints;

/// <summary>
/// Commandes du streamer.
/// </summary>
/// <remarks>
/// Trois actions et un patch de reglages, plutot qu'une route par bouton. Onze reglages
/// feraient onze fois le meme code, et chaque reglage futur en ajouterait un douzieme.
///
/// Toutes les routes exigent le role diffuseur, et le canal pilote est celui du JWT : il
/// n'existe aucun parametre par lequel viser le canal d'un autre.
/// </remarks>
public static class ControlEndpoints
{
    public static void MapControl(this IEndpointRouteBuilder routes)
    {
        RouteGroupBuilder control = routes.MapGroup("/api/control")
            .RequireAuthorization(Policies.Broadcaster);

        control.MapGet("/state", (ClaimsPrincipal user, ChannelSessionRegistry registry) =>
        {
            string channelId = TwitchClaims.ChannelOf(user);

            // La session nait ici, arretee, si besoin : la page de configuration regle la
            // hitmap hors live, avant tout premier demarrage. Elle n'accepte pas de clic
            // pour autant, une session nait toujours collecte fermee.
            ChannelSession session = registry.GetOrCreate(channelId);
            return Results.Ok(new { channelId, running = session.IsRunning, settings = session.Settings });
        });

        control.MapPost("/start", (ClaimsPrincipal user, ChannelSupervisor supervisor) =>
        {
            string channelId = TwitchClaims.ChannelOf(user);
            ChannelSession session = supervisor.Start(channelId);
            return Results.Ok(new { channelId, running = session.IsRunning });
        });

        control.MapPost("/stop", (ClaimsPrincipal user, ChannelSupervisor supervisor) =>
        {
            string channelId = TwitchClaims.ChannelOf(user);
            supervisor.Stop(channelId);
            return Results.Ok(new { channelId, running = false });
        });

        control.MapPost("/reset", (
            ClaimsPrincipal user,
            ChannelSessionRegistry registry,
            SnapshotBroadcaster broadcaster) =>
        {
            string channelId = TwitchClaims.ChannelOf(user);
            if (!registry.TryGet(channelId, out ChannelSession? session))
            {
                return Results.NotFound(new { error = "canal inconnu" });
            }

            session.Reset();

            // L'instantane retenu decrit un etat qui n'existe plus : le garder ferait
            // reapparaitre les anciennes zones au prochain overlay qui se connecte.
            broadcaster.Forget(channelId);

            return Results.Ok(new { channelId, positions = session.PositionCount });
        });

        control.MapPost("/settings", (
            ClaimsPrincipal user,
            SettingsPatch patch,
            ChannelSessionRegistry registry) =>
        {
            if (Validate(patch) is { } error)
            {
                return Results.BadRequest(new { error });
            }

            ChannelSession session = registry.GetOrCreate(TwitchClaims.ChannelOf(user));

            // Remplacement atomique d'une instance immuable : aucun verrou n'est
            // necessaire entre cette requete et la boucle de tick.
            session.Settings = Apply(session.Settings, patch);
            return Results.Ok(session.Settings);
        });

        // Le jeton seul, pas l'URL : le serveur ne connait pas l'adresse publique sous
        // laquelle on l'atteint, le panneau d'extension si.
        control.MapGet("/overlay-token", (ClaimsPrincipal user, OverlayTokens tokens) =>
        {
            string channelId = TwitchClaims.ChannelOf(user);
            return Results.Ok(new { channelId, token = tokens.Current(channelId) });
        });

        control.MapPost("/overlay-token/rotate", (
            ClaimsPrincipal user,
            OverlayTokens tokens,
            SnapshotBroadcaster broadcaster) =>
        {
            string channelId = TwitchClaims.ChannelOf(user);
            string token = tokens.Rotate(channelId);

            // Revoquer, c'est aussi couper les flux deja ouverts : une URL qui a fuite ne
            // doit pas continuer a recevoir tant qu'elle reste connectee.
            // shortcut: un flux valide juste avant la rotation mais abonne juste apres
            // survit (fenetre de quelques ms), a fermer si la revocation doit etre stricte.
            broadcaster.Disconnect(channelId);

            return Results.Ok(new { channelId, token });
        });
    }

    private static string? Validate(SettingsPatch patch)
    {
        if (patch.SmoothingRadius is { } radius && (radius < 0 || radius > DensityFieldBuilder.MaxSmoothingRadius))
        {
            return $"smoothingRadius doit etre entre 0 et {DensityFieldBuilder.MaxSmoothingRadius}";
        }

        if (patch.Threshold is { } threshold && (threshold < 0f || threshold > 1f))
        {
            return "threshold doit etre entre 0 et 1";
        }

        if (patch.MinFieldContrast is { } contrast && (contrast < 1f || contrast > 20f))
        {
            return "minFieldContrast doit etre entre 1 et 20";
        }

        if (patch.PeakMergeDistance is { } merge && (merge <= 0f || merge > 1f))
        {
            return "peakMergeDistance doit etre superieur a 0 et au plus 1";
        }

        if (patch.AssignmentRadius is { } assignment && (assignment <= 0f || assignment > 2f))
        {
            return "assignmentRadius doit etre superieur a 0 et au plus 2";
        }

        if (patch.MinZonePercentage is { } minimum && (minimum < 0f || minimum > 100f))
        {
            return "minZonePercentage doit etre entre 0 et 100";
        }

        if (patch.MaxZones is { } maxZones && (maxZones < 1 || maxZones > PeakDetector.HardCeiling))
        {
            return $"maxZones doit etre entre 1 et {PeakDetector.HardCeiling}";
        }

        if (patch.MajorZones is { } majorZones && majorZones < 1)
        {
            return "majorZones doit valoir au moins 1";
        }

        if (patch.BandCount is { } bands && (bands < 1 || bands > 32))
        {
            return "bandCount doit etre entre 1 et 32";
        }

        if (patch.PositionTtlSeconds is { } ttl && (ttl < 1 || ttl > 3600))
        {
            return "positionTtlSeconds doit etre entre 1 et 3600";
        }

        if (patch.RenderMode is { } mode && !Enum.TryParse<RenderMode>(mode, ignoreCase: true, out _))
        {
            return "renderMode doit valoir ellipse, contour ou both";
        }

        return null;
    }

    private static SessionSettings Apply(SessionSettings current, SettingsPatch patch) => current with
    {
        SmoothingRadius = patch.SmoothingRadius ?? current.SmoothingRadius,
        Threshold = patch.Threshold ?? current.Threshold,
        MinFieldContrast = patch.MinFieldContrast ?? current.MinFieldContrast,
        PeakMergeDistance = patch.PeakMergeDistance ?? current.PeakMergeDistance,
        AssignmentRadius = patch.AssignmentRadius ?? current.AssignmentRadius,
        MinZonePercentage = patch.MinZonePercentage ?? current.MinZonePercentage,
        MaxZones = patch.MaxZones ?? current.MaxZones,
        MajorZones = patch.MajorZones ?? current.MajorZones,
        BandCount = patch.BandCount ?? current.BandCount,
        AllowAnonymous = patch.AllowAnonymous ?? current.AllowAnonymous,
        Visible = patch.Visible ?? current.Visible,
        RenderMode = patch.RenderMode is { } mode
            ? Enum.Parse<RenderMode>(mode, ignoreCase: true)
            : current.RenderMode,
        PositionTtl = patch.PositionTtlSeconds is { } ttl
            ? TimeSpan.FromSeconds(ttl)
            : current.PositionTtl,
    };
}

/// <summary>
/// Patch partiel : seuls les champs fournis sont appliques.
/// </summary>
/// <remarks>
/// Tout est nullable pour distinguer « absent » de « remis a zero » — sans quoi un client
/// qui n'envoie qu'un reglage remettrait par accident tous les autres a leur defaut.
/// </remarks>
public sealed record SettingsPatch(
    int? SmoothingRadius = null,
    float? Threshold = null,
    float? MinFieldContrast = null,
    float? PeakMergeDistance = null,
    float? AssignmentRadius = null,
    float? MinZonePercentage = null,
    int? MaxZones = null,
    int? MajorZones = null,
    int? BandCount = null,
    bool? AllowAnonymous = null,
    bool? Visible = null,
    string? RenderMode = null,
    int? PositionTtlSeconds = null);
