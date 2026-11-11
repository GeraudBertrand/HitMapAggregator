namespace HitMapAggregator.Api.Security;

/// <summary>
/// Les deux cles symetriques du serveur, en base64.
/// </summary>
/// <remarks>
/// <see cref="TwitchExtension"/> est le secret de l'extension donne par la console
/// developpeur Twitch : il verifie les JWT des viewers. <see cref="OverlayToken"/> est a
/// nous seuls : il signe les URL de l'overlay. Deux cles distinctes, pour qu'une fuite de
/// l'une ne permette pas de forger les jetons de l'autre.
/// </remarks>
public sealed class HitMapSecrets
{
    public const string Section = "Secrets";

    public string TwitchExtension { get; set; } = "";

    public string OverlayToken { get; set; } = "";

    /// <summary>Cle d'acces a <c>/api/metrics</c>, attendue dans l'en-tete <c>X-Metrics-Key</c>.</summary>
    public string MetricsKey { get; set; } = "";

    /// <summary>Une cle HMAC-SHA256 de moins de 32 octets affaiblit la signature.</summary>
    public static bool IsUsableKey(string? base64)
    {
        if (string.IsNullOrWhiteSpace(base64))
        {
            return false;
        }

        Span<byte> buffer = stackalloc byte[256];
        return Convert.TryFromBase64String(base64, buffer, out int written) && written >= 32;
    }
}
