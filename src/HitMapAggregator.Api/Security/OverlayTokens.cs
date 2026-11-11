using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace HitMapAggregator.Api.Security;

/// <summary>
/// Jeton de lecture seule de l'overlay : HMAC du canal et d'un numero de generation.
/// </summary>
/// <remarks>
/// La browser source OBS ne sait pas porter d'en-tete, donc le jeton voyage dans l'URL.
/// Le HMAC evite de stocker des jetons : seul le numero de generation est memorise, et
/// l'incrementer invalide l'URL precedente.
///
/// Les generations sont persistees dans un fichier JSON : sans lui, un redemarrage
/// ramenerait chaque canal a la generation 0, ressusciterait les URL revoquees et
/// casserait les URL en service.
/// </remarks>
public sealed class OverlayTokens
{
    private readonly byte[] _key;
    private readonly string _path;
    private readonly ConcurrentDictionary<string, int> _generations;
    private readonly Lock _rotation = new();

    public OverlayTokens(IOptions<HitMapSecrets> secrets, string generationsPath)
    {
        _key = Convert.FromBase64String(secrets.Value.OverlayToken);
        _path = generationsPath;

        // Un fichier illisible fait echouer le demarrage : repartir a zero en silence
        // reproduirait exactement la resurrection des URL revoquees qu'on veut eviter.
        Dictionary<string, int>? saved = File.Exists(_path)
            ? JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(_path))
            : null;

        _generations = new ConcurrentDictionary<string, int>(saved ?? [], StringComparer.Ordinal);
    }

    public string Current(string channelId) => Compute(channelId, _generations.GetValueOrDefault(channelId));

    public string Rotate(string channelId)
    {
        // Les rotations sont rares : un verrou garantit que le fichier ecrit en dernier
        // contient bien la derniere generation de chaque canal.
        lock (_rotation)
        {
            int generation = _generations.AddOrUpdate(channelId, 1, static (_, current) => current + 1);
            Save();
            return Compute(channelId, generation);
        }
    }

    public bool IsValid(string channelId, string? token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return false;
        }

        // Comparaison a temps constant : un == s'arreterait au premier octet faux et
        // laisserait deviner le jeton octet par octet.
        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(token),
            Encoding.ASCII.GetBytes(Current(channelId)));
    }

    /// <summary>Ecriture dans un fichier temporaire puis renommage : jamais de fichier a moitie ecrit.</summary>
    private void Save()
    {
        string? directory = Path.GetDirectoryName(Path.GetFullPath(_path));
        if (directory is not null)
        {
            Directory.CreateDirectory(directory);
        }

        string temporary = _path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(_generations));
        File.Move(temporary, _path, overwrite: true);
    }

    private string Compute(string channelId, int generation) =>
        Base64Url.EncodeToString(HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes($"{channelId}\n{generation}")));
}
