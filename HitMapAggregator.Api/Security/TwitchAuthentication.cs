using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace HitMapAggregator.Api.Security;

/// <summary>Noms des claims du JWT d'extension Twitch.</summary>
public static class TwitchClaims
{
    public const string ChannelId = "channel_id";
    public const string OpaqueUserId = "opaque_user_id";
    public const string Role = "role";

    public const string BroadcasterRole = "broadcaster";

    public static string ChannelOf(ClaimsPrincipal user) =>
        user.FindFirstValue(ChannelId) ?? throw new InvalidOperationException("JWT sans channel_id malgre la policy");
}

public static class Policies
{
    /// <summary>Tout porteur d'un JWT d'extension rattache a un canal et a un viewer.</summary>
    public const string Viewer = "viewer";

    /// <summary>Le proprietaire du canal, et lui seul.</summary>
    public const string Broadcaster = "broadcaster";
}

public static class TwitchAuthentication
{
    public static IServiceCollection AddTwitchAuthentication(this IServiceCollection services)
    {
        services.AddOptions<HitMapSecrets>()
            .BindConfiguration(HitMapSecrets.Section)
            .Validate(
                static secrets => HitMapSecrets.IsUsableKey(secrets.TwitchExtension)
                    && HitMapSecrets.IsUsableKey(secrets.OverlayToken)
                    && secrets.MetricsKey.Length >= 16,
                "Secrets:TwitchExtension et Secrets:OverlayToken doivent etre des cles base64 d'au moins 32 octets, "
                + "Secrets:MetricsKey une chaine d'au moins 16 caracteres")
            .ValidateOnStart();

        // JwtBearer valide avec JsonWebTokenHandler depuis .NET 8 : l'ancien
        // JwtSecurityTokenHandler n'est jamais charge tant que UseSecurityTokenValidators
        // reste a false (defaut).
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();

        // Configure a la resolution et non au demarrage : les secrets doivent pouvoir venir
        // de n'importe quelle source de configuration, y compris celles ajoutees par les tests.
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IOptions<HitMapSecrets>>(static (options, secrets) =>
            {
                // Sans ca, "role" serait renomme en ClaimTypes.Role et "channel_id" garderait
                // son nom : deux conventions dans le meme principal.
                options.MapInboundClaims = false;

                options.TokenValidationParameters = new TokenValidationParameters
                {
                    IssuerSigningKey = new SymmetricSecurityKey(Convert.FromBase64String(secrets.Value.TwitchExtension)),
                    ValidAlgorithms = [SecurityAlgorithms.HmacSha256],

                    // Twitch ne met ni iss ni aud dans ses JWT d'extension : seule la
                    // signature prouve leur origine.
                    ValidateIssuer = false,
                    ValidateAudience = false,

                    RoleClaimType = TwitchClaims.Role,
                    NameClaimType = TwitchClaims.OpaqueUserId,
                };
            });

        services.AddAuthorizationBuilder()
            .AddPolicy(Policies.Viewer, static policy => policy
                .RequireClaim(TwitchClaims.ChannelId)
                .RequireClaim(TwitchClaims.OpaqueUserId))
            .AddPolicy(Policies.Broadcaster, static policy => policy
                .RequireClaim(TwitchClaims.ChannelId)
                .RequireRole(TwitchClaims.BroadcasterRole));

        return services;
    }

    /// <summary>
    /// Origines autorisees a appeler l'API depuis un navigateur.
    /// </summary>
    /// <remarks>
    /// Les fichiers d'une extension sont servis par Twitch depuis
    /// <c>https://&lt;client-id&gt;.ext-twitch.tv</c>. En developpement, le mode « Local
    /// Test » de Twitch les charge depuis l'URI de test, en general sur localhost.
    /// </remarks>
    public static bool IsAllowedOrigin(string origin, bool isDevelopment)
    {
        if (!Uri.TryCreate(origin, UriKind.Absolute, out Uri? uri))
        {
            return false;
        }

        if (uri.Scheme == Uri.UriSchemeHttps && uri.Host.EndsWith(".ext-twitch.tv", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return isDevelopment && uri.IsLoopback;
    }
}
