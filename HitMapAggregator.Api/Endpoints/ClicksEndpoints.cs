using System.Security.Claims;
using HitMapAggregator.Api.Diagnostics;
using HitMapAggregator.Api.Security;
using HitMapAggregator.Core.Models;
using HitMapAggregator.Core.Session;

namespace HitMapAggregator.Api.Endpoints;

/// <summary>
/// Ingestion des clics : sans etat, sans reponse utile, aussi courte que possible.
/// </summary>
/// <remarks>
/// Le corps ne porte que la position. Le canal et le viewer viennent du JWT valide : un
/// client ne peut ni voter sur un autre canal, ni voter sous l'identite d'un autre.
/// </remarks>
public static class ClicksEndpoints
{
    public static void MapClicks(this IEndpointRouteBuilder routes)
    {
        routes.MapPost("/api/clicks", (
            ClickRequest request,
            ClaimsPrincipal user,
            ChannelSessionRegistry registry,
            ChannelMetrics metrics) =>
        {
            if (!IsUnitInterval(request.X) || !IsUnitInterval(request.Y))
            {
                metrics.RecordRejectedInvalid();
                return Results.BadRequest(new { error = "coordonnees invalides" });
            }

            if (!registry.TryGet(TwitchClaims.ChannelOf(user), out ChannelSession? session))
            {
                metrics.RecordRejectedStopped();
                return Results.Conflict(new { error = "canal inactif" });
            }

            ulong key = UserKey.From(user.FindFirstValue(TwitchClaims.OpaqueUserId));
            var point = new NormalizedPoint(request.X, request.Y, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

            // C'est la session qui refuse, pas l'endpoint : une seule autorite, aucun
            // moyen de la contourner par une autre route.
            if (!session.Upsert(key, point))
            {
                metrics.RecordRejectedStopped();
                return Results.Conflict(new { error = "collecte arretee" });
            }

            metrics.RecordAccepted();
            return Results.NoContent();
        })
        .RequireAuthorization(Policies.Viewer);
    }

    /// <summary>
    /// Encadrement par comparaisons positives : la forme rejette aussi NaN, qu'un test
    /// ecrit a l'envers (<c>value &lt; 0 || value &gt; 1</c>) laisserait passer.
    /// </summary>
    /// <remarks>
    /// JSON ne sait pas transporter NaN, donc le cas ne peut pas venir d'un client bien
    /// forme. Le garde ne coute rien et couvre les autres chemins d'entree — un appel
    /// direct, ou un futur format binaire.
    /// </remarks>
    private static bool IsUnitInterval(float value) => value >= 0f && value <= 1f;
}

public sealed record ClickRequest(float X, float Y);
