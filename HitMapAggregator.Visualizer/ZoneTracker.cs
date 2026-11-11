using System.Globalization;
using HitMapAggregator.Core;
using HitMapAggregator.Core.Models;

namespace HitMapAggregator.Visualizer;

/// <summary>
/// Donne une identite stable aux zones d'un tick a l'autre, conserve leur historique de
/// parts, et signale les depassements.
/// </summary>
/// <remarks>
/// L'agregation est sans memoire : chaque tick recalcule des zones anonymes, triees par
/// effectif. Rien n'y dit que la zone n°1 de ce tick est la meme que la n°1 du precedent.
/// Sans appariement, une phrase comme « elle est passee de 30 a 42 % » n'a aucun sens, et
/// un simple echange de rang serait indiscernable d'une variation de part.
///
/// L'appariement se fait par proximite des centres, du plus gros au plus petit. Une zone
/// qui disparait n'est pas oubliee tout de suite : on enregistre 0 % pendant quelques
/// ticks, ce qui rend le clignotement visible au lieu de le masquer.
/// </remarks>
internal sealed class ZoneTracker
{
    /// <summary>Au-dela, deux zones sont considerees distinctes (espace corrige).</summary>
    private const float MatchDistance = 0.10f;

    public const int HistoryLength = 240;
    private const int MissesBeforeDrop = 15;
    private const int HighlightTicks = 12;
    private const int MaxEvents = 200;

    private static readonly Color[] Palette =
    [
        Color.FromArgb(88, 166, 255),
        Color.FromArgb(255, 138, 96),
        Color.FromArgb(126, 231, 135),
        Color.FromArgb(238, 190, 92),
        Color.FromArgb(199, 146, 255),
        Color.FromArgb(96, 224, 222),
        Color.FromArgb(255, 145, 190),
        Color.FromArgb(168, 214, 96),
        Color.FromArgb(240, 120, 120),
        Color.FromArgb(140, 160, 255),
        Color.FromArgb(206, 226, 120),
        Color.FromArgb(120, 200, 180),
    ];

    /// <summary>
    /// Couleur deduite du seul rang, sans identite.
    /// </summary>
    /// <remarks>
    /// C'est tout ce dont dispose un overlay : il recoit une liste classee et colorie par
    /// position. La couleur change donc de zone quand le classement s'inverse — c'est le
    /// comportement reel du produit, pas un defaut.
    /// </remarks>
    public static Color RankColor(int rank) => Palette[Math.Max(0, rank - 1) % Palette.Length];

    private readonly List<Track> _tracks = [];
    private readonly List<string> _events = [];
    private int _nextId = 1;
    private double _clockSeconds;

    public IReadOnlyList<Track> Tracks => _tracks;

    public IReadOnlyList<string> Events => _events;

    /// <summary>Le plus haut pourcentage jamais vu, pour cadrer le graphique sans le faire sauter.</summary>
    public float PeakPercentage { get; private set; }

    public void Reset()
    {
        _tracks.Clear();
        _events.Clear();
        _nextId = 1;
        _clockSeconds = 0.0;
        PeakPercentage = 0f;
    }

    public void Update(IReadOnlyList<Zone> zones, int majorZones, double elapsedSeconds)
    {
        _clockSeconds += elapsedSeconds;

        int[] previousOrder = Ordered().Select(static t => t.Id).ToArray();
        var previousPercentages = _tracks.ToDictionary(static t => t.Id, static t => t.Percentage);

        Match(zones);

        foreach (Track track in _tracks)
        {
            track.Push();
            if (track.HighlightTicks > 0)
            {
                track.HighlightTicks--;
            }

            PeakPercentage = MathF.Max(PeakPercentage, track.Percentage);
        }

        _tracks.RemoveAll(static t => t.MissedTicks > MissesBeforeDrop);

        Track[] order = Ordered().ToArray();
        for (int i = 0; i < order.Length; i++)
        {
            order[i].Rank = i + 1;
        }

        DetectOvertakes(previousOrder, previousPercentages, order, majorZones);
    }

    private IEnumerable<Track> Ordered() => _tracks.OrderByDescending(static t => t.Percentage).ThenBy(static t => t.Id);

    private void Match(IReadOnlyList<Zone> zones)
    {
        foreach (Track track in _tracks)
        {
            track.Matched = false;
        }

        // Du plus gros au plus petit : une grosse zone a la priorite sur l'identite
        // qu'elle revendique, ce qui evite qu'un satellite vole la piste de son voisin.
        foreach (Zone zone in zones.OrderByDescending(static z => z.PointCount))
        {
            Track? best = null;
            float bestDistance = MatchDistance * MatchDistance;

            foreach (Track track in _tracks)
            {
                if (track.Matched)
                {
                    continue;
                }

                float distance = Grid.DistanceSquared(
                    zone.Center.X, zone.Center.Y, track.Zone.Center.X, track.Zone.Center.Y);

                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = track;
                }
            }

            if (best is null)
            {
                best = new Track(_nextId, Palette[(_nextId - 1) % Palette.Length]);
                _nextId++;
                _tracks.Add(best);
            }

            best.Matched = true;
            best.MissedTicks = 0;
            best.Zone = zone;
            best.Percentage = zone.Percentage;
        }

        foreach (Track track in _tracks)
        {
            if (!track.Matched)
            {
                track.MissedTicks++;
                track.Percentage = 0f;
            }
        }
    }

    private void DetectOvertakes(
        int[] previousOrder,
        Dictionary<int, float> previousPercentages,
        Track[] order,
        int majorZones)
    {
        if (previousOrder.Length == 0 || order.Length == 0)
        {
            return;
        }

        int major = Math.Clamp(majorZones, 1, order.Length);

        if (previousOrder[0] != order[0].Id)
        {
            Track leader = order[0];
            Track? dethroned = _tracks.Find(t => t.Id == previousOrder[0]);

            string before = previousPercentages.TryGetValue(leader.Id, out float p)
                ? p.ToString("F1", CultureInfo.CurrentCulture)
                : "-";

            Log($"LEADER z{leader.Id}  {before}>{leader.Percentage:F1}%"
                + (dethroned is null ? string.Empty : $"  devant z{dethroned.Id} {dethroned.Percentage:F1}%"));

            leader.HighlightTicks = HighlightTicks;
            if (dethroned is not null)
            {
                dethroned.HighlightTicks = HighlightTicks;
            }
        }

        // Entrees dans le groupe de tete : un depassement plus bas dans le classement
        // compte aussi, tant qu'il change ce qui est mis en avant a l'ecran.
        var previousMajor = previousOrder.Take(Math.Min(major, previousOrder.Length)).ToHashSet();
        for (int i = 0; i < major; i++)
        {
            Track track = order[i];
            if (previousMajor.Count == 0 || previousMajor.Contains(track.Id) || i == 0)
            {
                continue;
            }

            string before = previousPercentages.TryGetValue(track.Id, out float p)
                ? p.ToString("F1", CultureInfo.CurrentCulture)
                : "-";

            Log($"TOP{major}   z{track.Id}  {before}>{track.Percentage:F1}%  rang {i + 1}");
            track.HighlightTicks = HighlightTicks;
        }
    }

    private void Log(string message)
    {
        _events.Insert(0, $"[{_clockSeconds,6:F1}s] {message}");
        if (_events.Count > MaxEvents)
        {
            _events.RemoveAt(_events.Count - 1);
        }
    }

    internal sealed class Track(int id, Color color)
    {
        private readonly float[] _history = new float[HistoryLength];

        public int Id { get; } = id;

        public Color Color { get; } = color;

        public Zone Zone { get; set; }

        public float Percentage { get; set; }

        public int Rank { get; set; }

        public int MissedTicks { get; set; }

        public int HighlightTicks { get; set; }

        public bool Matched { get; set; }

        public int Length { get; private set; }

        public float this[int index] => _history[index];

        public void Push()
        {
            if (Length < HistoryLength)
            {
                _history[Length++] = Percentage;
                return;
            }

            Array.Copy(_history, 1, _history, 0, HistoryLength - 1);
            _history[HistoryLength - 1] = Percentage;
        }
    }
}
