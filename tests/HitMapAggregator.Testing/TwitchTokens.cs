using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace HitMapAggregator.Testing;

/// <summary>
/// Fabrique des JWT d'extension au format Twitch, pour les tests, le simulateur et les
/// tests de charge.
/// </summary>
public static class TwitchTokens
{
    /// <summary>
    /// Meme valeur que <c>Secrets:TwitchExtension</c> dans appsettings.Development.json.
    /// Secret de developpement, sans valeur hors de cette machine.
    /// </summary>
    public const string DevelopmentSecret = "epAM2kopKve5HJCbGrKmBKpvEghiOF6Hfg/E+FGDfAY=";

    private static readonly JsonWebTokenHandler Handler = new();

    public static string Viewer(string secret, string channelId, string opaqueUserId) =>
        Create(secret, channelId, opaqueUserId, "viewer", DateTime.UtcNow.AddHours(1));

    public static string Broadcaster(string secret, string channelId) =>
        Create(secret, channelId, $"U{channelId}", "broadcaster", DateTime.UtcNow.AddHours(1));

    public static string Create(string secret, string channelId, string opaqueUserId, string role, DateTime expires)
    {
        var descriptor = new SecurityTokenDescriptor
        {
            Claims = new Dictionary<string, object>
            {
                ["channel_id"] = channelId,
                ["opaque_user_id"] = opaqueUserId,
                ["role"] = role,
            },

            // nbf bien avant exp, pour pouvoir fabriquer aussi un jeton deja expire.
            NotBefore = expires.AddHours(-2),
            IssuedAt = expires.AddHours(-2),
            Expires = expires,
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(Convert.FromBase64String(secret)),
                SecurityAlgorithms.HmacSha256),
        };

        return Handler.CreateToken(descriptor);
    }
}
