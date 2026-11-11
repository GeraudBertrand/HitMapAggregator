using System.Buffers;
using System.IO.Hashing;
using System.Text;

namespace HitMapAggregator.Core.Session;

/// <summary>
/// Convention de codage de la cle d'un viewer.
/// </summary>
/// <remarks>
/// L'<c>opaque_user_id</c> est hache en <see cref="ulong"/> des l'ingestion : on ne
/// veut ni stocker de chaines, ni un <c>Dictionary&lt;string, _&gt;</c> a 50 000 entrees.
/// Mais l'agregation doit pouvoir ecarter les viewers non connectes a la demande, sans
/// les avoir filtres a l'entree — sinon rebasculer le reglage perdrait les donnees.
///
/// L'information tient donc dans le bit de poids fort de la cle elle-meme : filtrer
/// coute un ET binaire et ne demande aucune structure annexe. Le hachage garde 63 bits
/// utiles, ce qui reste tres au-dela du besoin.
/// </remarks>
public static class UserKey
{
    public const ulong AnonymousFlag = 1UL << 63;

    /// <summary>
    /// Un identifiant Twitch opaque commencant par 'A' designe un viewer non connecte.
    /// </summary>
    /// <remarks>
    /// Ces identifiants sont instables : ils changent a chaque rechargement de page. D'ou
    /// le TTL, sans lequel chaque F5 laisserait une entree fantome qui continue de voter.
    /// </remarks>
    public const char AnonymousPrefix = 'A';

    /// <summary>Hache un <c>opaque_user_id</c> en cle, drapeau d'anonymat compris.</summary>
    public static ulong From(ReadOnlySpan<char> opaqueUserId)
    {
        int capacity = Encoding.UTF8.GetMaxByteCount(opaqueUserId.Length);

        byte[]? rented = capacity > 256 ? ArrayPool<byte>.Shared.Rent(capacity) : null;
        Span<byte> utf8 = rented ?? stackalloc byte[256];
        try
        {
            int written = Encoding.UTF8.GetBytes(opaqueUserId, utf8);
            ulong hash = XxHash64.HashToUInt64(utf8[..written]);

            bool anonymous = !opaqueUserId.IsEmpty && opaqueUserId[0] == AnonymousPrefix;
            return WithAnonymousFlag(hash, anonymous);
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }
    }

    public static bool IsAnonymous(ulong key) => (key & AnonymousFlag) != 0UL;

    public static ulong WithAnonymousFlag(ulong hash, bool isAnonymous) =>
        isAnonymous ? hash | AnonymousFlag : hash & ~AnonymousFlag;
}
