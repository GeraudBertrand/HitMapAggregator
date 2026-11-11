using HitMapAggregator.Api.Diagnostics;
using HitMapAggregator.Api.Hosting;
using HitMapAggregator.Api.Realtime;
using HitMapAggregator.Api.Security;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace HitMapAggregator.Api.Endpoints;

/// <summary>
/// Flux SSE vers l'overlay, et compteurs de fonctionnement.
/// </summary>
/// <remarks>
/// SSE et non WebSocket : le flux est a sens unique et n'a qu'un destinataire par canal.
/// Protege par un jeton de lecture seule en query string, puisqu'une browser source OBS ne
/// sait pas porter d'en-tete. L'URL ne doit donc jamais apparaitre dans les journaux.
/// </remarks>
public static class OverlayEndpoints
{
    public static void MapOverlayStream(this IEndpointRouteBuilder routes)
    {
        routes.MapGet("/api/stream/heatmap", IResult (
            string channelId,
            string? token,
            OverlayTokens tokens,
            SnapshotBroadcaster broadcaster,
            CancellationToken cancellationToken) =>
            tokens.IsValid(channelId, token)
                ? TypedResults.ServerSentEvents(
                    broadcaster.Subscribe(channelId, cancellationToken),
                    eventType: "snapshot")
                : TypedResults.Unauthorized());
    }

    public static void MapMetrics(this IEndpointRouteBuilder routes)
    {
        // Protege parce que le rapport liste les canaux actifs et leur activite.
        routes.MapGet("/api/metrics", (
            [FromHeader(Name = "X-Metrics-Key")] string? key,
            IOptions<HitMapSecrets> secrets,
            ChannelMetrics metrics,
            ChannelSupervisor supervisor,
            SnapshotBroadcaster broadcaster) =>
            IsMetricsKey(key, secrets.Value.MetricsKey)
                ? Results.Ok(metrics.Report(supervisor.LiveChannels, broadcaster.TotalSubscribers))
                : Results.Unauthorized());
    }

    private static bool IsMetricsKey(string? provided, string expected) =>
        provided is not null
        && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(provided), Encoding.UTF8.GetBytes(expected));
}
