using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using HitMapAggregator.Core;
using HitMapAggregator.Core.Models;
using HitMapAggregator.Core.Session;

namespace HitMapAggregator.Visualizer;

/// <summary>
/// Banc d'essai interactif : un monde de clics tire au hasard, qui derive dans le temps,
/// agrege en direct par le vrai moteur.
/// </summary>
/// <remarks>
/// La fenetre pilote un vrai <see cref="ChannelSession"/> plutot que d'appeler
/// directement le SnapshotBuilder : ce qu'on observe passe par le meme chemin qu'en
/// production, dictionnaire et recopie des positions compris, et le chronometre mesure
/// donc un tick complet.
/// </remarks>
internal sealed class ExplorerForm : Form
{
    private const float SigmaScale = 2f;
    private const int MaxPlottedPoints = 4_000;
    private const int TimingWindow = 120;
    private const int TickMilliseconds = 200;

    private static readonly Color Background = Color.FromArgb(13, 17, 23);
    private static readonly Color Surface = Color.FromArgb(22, 27, 34);
    private static readonly Color Muted = Color.FromArgb(139, 148, 158);

    private readonly ChannelSession _session = new("explorer");
    private readonly ZoneTracker _tracker = new();
    private readonly Bitmap _fieldImage = new(Grid.Width, Grid.Height, PixelFormat.Format32bppArgb);
    private readonly int[] _pixels = new int[Grid.CellCount];
    private readonly double[] _timings = new double[TimingWindow];
    private readonly System.Windows.Forms.Timer _liveTimer = new() { Interval = TickMilliseconds };

    private readonly Panel _canvas;
    private readonly Panel _chart;
    private readonly Panel _table;
    private readonly ListBox _events;
    private readonly Label _status;
    private readonly Label _diagnostics;

    private readonly CheckBox _live;
    private readonly CheckBox _showDensity;
    private readonly CheckBox _showPoints;
    private readonly CheckBox _showZones;
    private readonly CheckBox _rankColours;

    private readonly TrackBar _clusters;
    private readonly TrackBar _noise;
    private readonly TrackBar _volatility;
    private readonly TrackBar _viewers;
    private readonly TrackBar _churn;
    private readonly TrackBar _smoothing;
    private readonly TrackBar _contrast;
    private readonly TrackBar _threshold;
    private readonly TrackBar _merge;
    private readonly TrackBar _maxZones;
    private readonly TrackBar _majorZones;
    private readonly TrackBar _assignment;
    private readonly TrackBar _minZone;

    private RandomWorld _world = null!;
    private HeatmapSnapshot? _snapshot;
    private NormalizedPoint[] _current = [];
    private PointF[] _plotted = [];
    private int _timingCount;
    private int _timingIndex;
    private int _warmupTicks;
    private int _seed = 20260909;
    private long _clock;
    private readonly bool _loaded;

    public ExplorerForm()
    {
        Text = "HitMap - banc d'essai";

        // On ne peut pas supposer la taille de l'ecran : une fenetre plus grande que le
        // bureau ferait sortir les barres de statut du champ, donc le chronometre avec.
        Rectangle work = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 800);
        ClientSize = new Size(Math.Min(1560, work.Width - 60), Math.Min(940, work.Height - 60));
        MinimumSize = new Size(1000, 640);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Background;

        _canvas = new BufferedPanel { Dock = DockStyle.Fill, BackColor = Background };
        _canvas.Paint += PaintCanvas;

        _chart = new BufferedPanel { Dock = DockStyle.Fill, BackColor = Surface };
        _chart.Paint += PaintChart;

        _events = new ListBox
        {
            Dock = DockStyle.Fill,
            BackColor = Surface,
            ForeColor = Color.Gainsboro,
            BorderStyle = BorderStyle.None,
            Font = new Font(FontFamily.GenericMonospace, 8f),
            IntegralHeight = false,
            HorizontalScrollbar = true,
        };

        _table = new BufferedPanel { Dock = DockStyle.Top, Height = 152, BackColor = Surface };
        _table.Paint += PaintTable;

        // Le tableau d'abord, le journal en dessous : ce que l'overlay recoit compte plus
        // que ce que l'outil sait deduire en plus.
        var side = new Panel { Dock = DockStyle.Right, Width = 430, BackColor = Surface };
        side.Controls.Add(_events);
        side.Controls.Add(_table);

        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 258, BackColor = Surface };
        bottom.Controls.Add(_chart);
        bottom.Controls.Add(side);

        _status = StatusLabel(Color.Gainsboro);
        _diagnostics = StatusLabel(Muted);

        var controls = new FlowLayoutPanel
        {
            Dock = DockStyle.Left,
            Width = 340,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            BackColor = Surface,
            Padding = new Padding(12, 6, 12, 8),
        };

        controls.Controls.Add(Caption("Monde aleatoire"));
        _clusters = AddSlider(controls, "Amas generes", 1, 30, 6, static v => v.ToString(), NewWorld);
        _noise = AddSlider(controls, "Bruit de fond", 0, 40, 8, static v => $"{v} %", NewWorld);
        _volatility = AddSlider(controls, "Volatilite des parts", 0, 40, 12, static v => (v / 1000f).ToString("F3"), null);

        var reroll = new Button
        {
            Text = "Nouveau monde",
            Width = 290,
            Height = 28,
            FlatStyle = FlatStyle.Flat,
            ForeColor = Color.Gainsboro,
        };
        reroll.Click += (_, _) =>
        {
            _seed++;
            NewWorld();
        };
        controls.Controls.Add(reroll);

        controls.Controls.Add(Caption("Flux de clics"));
        _viewers = AddSlider(controls, "Viewers", 1, 100, 40, static v => $"{v * 500:N0}", Refill);
        _churn = AddSlider(controls, "Recliquent par tick", 0, 25, 6, static v => $"{v} %", null);
        _live = AddCheck(controls, "Live (tick 200 ms)", true);

        controls.Controls.Add(Caption("Champ de densite"));
        _smoothing = AddSlider(controls, "Rayon de lissage", 0, 32, 4, static v => v.ToString(), Recompute);
        _contrast = AddSlider(controls, "Contraste minimal", 10, 80, 30, static v => (v / 10f).ToString("F1"), Recompute);

        controls.Controls.Add(Caption("Detection des pics"));
        _threshold = AddSlider(controls, "Seuil (% du max)", 0, 50, 13, static v => $"{v} %", Recompute);
        _merge = AddSlider(controls, "Distance de fusion", 2, 40, 13, static v => (v / 100f).ToString("F2"), Recompute);
        _maxZones = AddSlider(controls, "Zones maximum", 1, 32, 10, static v => v.ToString(), Recompute);
        _majorZones = AddSlider(controls, "Zones majeures", 1, 10, 1, static v => v.ToString(), Recompute);

        controls.Controls.Add(Caption("Construction des zones"));
        _assignment = AddSlider(
            controls, "Rayon d'affectation", 0, 40, 0,
            static v => v == 0 ? "auto (= fusion)" : (v / 100f).ToString("F2"), Recompute);
        _minZone = AddSlider(controls, "Part minimale", 0, 250, 30, static v => $"{v / 10f:F1} %", Recompute);

        controls.Controls.Add(Caption("Affichage"));
        _showDensity = AddCheck(controls, "Champ de densite", true);
        _showPoints = AddCheck(controls, "Clics bruts", true);
        _showZones = AddCheck(controls, "Ellipses et parts", true);
        _rankColours = AddCheck(controls, "Couleurs par rang (vue overlay)", true);

        Controls.Add(_canvas);
        Controls.Add(bottom);
        Controls.Add(_diagnostics);
        Controls.Add(_status);
        Controls.Add(controls);

        // Cable en dernier : poser les valeurs initiales ne doit rien declencher.
        _live.CheckedChanged += (_, _) => _liveTimer.Enabled = _live.Checked;
        _showDensity.CheckedChanged += (_, _) => _canvas.Invalidate();
        _showPoints.CheckedChanged += (_, _) => _canvas.Invalidate();
        _showZones.CheckedChanged += (_, _) => _canvas.Invalidate();
        _rankColours.CheckedChanged += (_, _) => _canvas.Invalidate();
        _liveTimer.Tick += (_, _) => Advance();

        _loaded = true;
        NewWorld();
        _liveTimer.Enabled = _live.Checked;
    }

    private int ViewerCount => _viewers.Value * 500;

    private SessionSettings CurrentSettings() => new()
    {
        SmoothingRadius = _smoothing.Value,
        Threshold = _threshold.Value / 100f,
        MinFieldContrast = _contrast.Value / 10f,
        PeakMergeDistance = _merge.Value / 100f,
        AssignmentRadius = _assignment.Value == 0 ? null : _assignment.Value / 100f,
        MinZonePercentage = _minZone.Value / 10f,
        MaxZones = _maxZones.Value,
        MajorZones = _majorZones.Value,
    };

    /// <summary>Tire une carte entierement neuve et repart de zero.</summary>
    private void NewWorld()
    {
        if (!_loaded)
        {
            return;
        }

        _world = new RandomWorld(_seed, _clusters.Value, _noise.Value / 100f);
        _tracker.Reset();
        _timingCount = 0;
        _timingIndex = 0;
        _warmupTicks = 5;
        _clock = 0;
        _events.Items.Clear();
        Refill();
    }

    /// <summary>Fait recliquer tout le monde, sans changer la carte.</summary>
    private void Refill()
    {
        if (!_loaded)
        {
            return;
        }

        _world.NoiseFraction = _noise.Value / 100f;
        _current = new NormalizedPoint[ViewerCount];

        _session.Reset();
        _session.Start();
        for (int i = 0; i < _current.Length; i++)
        {
            _current[i] = _world.Sample(_clock);
            _session.Upsert((ulong)i, _current[i]);
        }

        Recompute();
    }

    /// <summary>
    /// Un tick de vie : les parts derivent, une fraction des viewers reclique.
    /// </summary>
    /// <remarks>
    /// La derive est ce qui rend l'outil utile. Sans elle, les pourcentages resteraient
    /// colles a leur valeur injectee et aucun classement ne s'inverserait, donc il n'y
    /// aurait jamais rien a detecter.
    /// </remarks>
    private void Advance()
    {
        _clock += TickMilliseconds;
        _world.Drift(_volatility.Value / 1000f);

        int churn = _current.Length * _churn.Value / 100;
        for (int i = 0; i < churn; i++)
        {
            int viewer = Random.Shared.Next(_current.Length);
            _current[viewer] = _world.Sample(_clock);
            _session.Upsert((ulong)viewer, _current[viewer]);
        }

        Recompute();
    }

    private void Recompute()
    {
        if (!_loaded || _current.Length == 0)
        {
            return;
        }

        SessionSettings settings = CurrentSettings();
        _session.Settings = settings;

        long start = Stopwatch.GetTimestamp();
        HeatmapSnapshot snapshot = _session.BuildSnapshot();
        double elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;

        // Les premiers ticks paient la compilation JIT et fausseraient le p99 pendant
        // toute la duree de la fenetre glissante.
        if (_warmupTicks > 0)
        {
            _warmupTicks--;
        }
        else
        {
            _timings[_timingIndex] = elapsed;
            _timingIndex = (_timingIndex + 1) % TimingWindow;
            _timingCount = Math.Min(_timingCount + 1, TimingWindow);
        }

        int eventsBefore = _tracker.Events.Count;
        _tracker.Update(snapshot.Zones, settings.MajorZones, TickMilliseconds / 1000.0);
        PublishEvents(eventsBefore);

        _snapshot = snapshot;
        Rasterize(snapshot.Density);
        Subsample();
        UpdateLabels(snapshot, elapsed, settings);

        _canvas.Invalidate();
        _chart.Invalidate();
        _table.Invalidate();
    }

    private void PublishEvents(int before)
    {
        int added = _tracker.Events.Count - before;
        for (int i = added - 1; i >= 0; i--)
        {
            _events.Items.Insert(0, _tracker.Events[i]);
        }

        while (_events.Items.Count > 200)
        {
            _events.Items.RemoveAt(_events.Items.Count - 1);
        }
    }

    /// <summary>
    /// Copie le champ dans un bitmap des maintenant : son tampon est reutilise au tick
    /// suivant, on ne peut pas se contenter de le lire au moment du rendu.
    /// </summary>
    private void Rasterize(in DensityField field)
    {
        float max = field.Max > 0f ? field.Max : 1f;
        for (int i = 0; i < Grid.CellCount; i++)
        {
            float t = field.Values[i] / max;
            (int r, int g, int b) = DensityPalette.Sample(t);
            int alpha = (int)(Math.Clamp(t * 3.5f, 0f, 1f) * 235f);
            _pixels[i] = alpha << 24 | r << 16 | g << 8 | b;
        }

        BitmapData data = _fieldImage.LockBits(
            new Rectangle(0, 0, Grid.Width, Grid.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            Marshal.Copy(_pixels, 0, data.Scan0, _pixels.Length);
        }
        finally
        {
            _fieldImage.UnlockBits(data);
        }
    }

    private void Subsample()
    {
        int step = Math.Max(1, _current.Length / MaxPlottedPoints);
        int kept = (_current.Length + step - 1) / step;
        if (_plotted.Length != kept)
        {
            _plotted = new PointF[kept];
        }

        int written = 0;
        for (int i = 0; i < _current.Length && written < kept; i += step)
        {
            _plotted[written++] = new PointF(_current[i].X, _current[i].Y);
        }
    }

    private void UpdateLabels(HeatmapSnapshot snapshot, double elapsed, SessionSettings settings)
    {
        var sorted = new double[_timingCount];
        Array.Copy(_timings, sorted, _timingCount);
        Array.Sort(sorted);
        double median = _timingCount == 0 ? 0 : sorted[_timingCount / 2];
        double p99 = _timingCount == 0 ? 0 : sorted[Math.Min(_timingCount - 1, (int)(_timingCount * 0.99))];

        // Verite terrain : la part de chaque amas injecte, bruit deduit. C'est la
        // reference contre laquelle juger si l'agregation « reagit correctement ».
        float clickShare = 1f - _noise.Value / 100f;
        string injected = string.Join(
            " ",
            _world.Weights.OrderByDescending(static w => w).Take(6).Select(w => $"{w * clickShare * 100f,4:F1}"));

        string detected = string.Join(
            " ",
            snapshot.Zones.Take(6).Select(static z => $"{z.Percentage,4:F1}"));

        _status.Text =
            $"injecte {injected}   |   detecte {detected}   |   " +
            $"{snapshot.Zones.Count,2} zone(s) sur {_world.ClusterCount} amas   |   " +
            $"disperses {snapshot.ScatteredPercentage,5:F1} %";

        double sum = 0.0;
        for (int i = 0; i < Grid.CellCount; i++)
        {
            sum += snapshot.Density.Values[i];
        }

        float mean = (float)(sum / Grid.CellCount);
        float contrast = mean > 0f ? snapshot.Density.Max / mean : 0f;
        float gate = settings.MinFieldContrast;
        bool rejected = contrast < gate;

        _diagnostics.Text =
            $"tick {elapsed,6:F2} ms   median {median,6:F2}   p99 {p99,6:F2}   |   " +
            $"{snapshot.TotalVotes,6:N0} clics   |   contraste {contrast,6:F2} / porte {gate:F1}" +
            (rejected ? "   -> CHAMP REJETE, aucune zone" : string.Empty);
        _diagnostics.ForeColor = rejected ? Color.FromArgb(240, 150, 90) : Muted;
    }

    private void PaintCanvas(object? sender, PaintEventArgs e)
    {
        if (_snapshot is not { } snapshot)
        {
            return;
        }

        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        // Cadre 16:9 centre : la correction d'aspect n'a de sens que si l'affichage
        // respecte le ratio pour lequel elle est calculee.
        Rectangle view = Viewport(_canvas.ClientSize);

        if (_showDensity.Checked)
        {
            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(_fieldImage, view);
        }

        if (_showPoints.Checked)
        {
            using var brush = new SolidBrush(Color.FromArgb(60, 255, 255, 255));
            foreach (PointF p in _plotted)
            {
                g.FillRectangle(brush, view.X + p.X * view.Width - 1f, view.Y + p.Y * view.Height - 1f, 2f, 2f);
            }
        }

        using (var frame = new Pen(Color.FromArgb(55, 255, 255, 255)))
        {
            g.DrawRectangle(frame, view);
        }

        if (!_showZones.Checked)
        {
            return;
        }

        int major = _majorZones.Value;
        using var badgeFont = new Font(FontFamily.GenericSansSerif, 10f, FontStyle.Bold);
        using var labelFont = new Font(FontFamily.GenericMonospace, 10f, FontStyle.Bold);
        using var minorFont = new Font(FontFamily.GenericMonospace, 8f);
        using var halo = new SolidBrush(Color.FromArgb(210, 13, 17, 23));

        foreach (ZoneTracker.Track track in _tracker.Tracks.OrderByDescending(t => t.Rank))
        {
            if (track.MissedTicks > 0)
            {
                continue;
            }

            Zone zone = track.Zone;
            bool isMajor = track.Rank <= major;
            bool flash = track.HighlightTicks > 0;

            float cx = view.X + zone.Center.X * view.Width;
            float cy = view.Y + zone.Center.Y * view.Height;

            // Les DEUX rayons passent par la largeur : les sigmas sont dans la meme
            // echelle isotrope. Passer SigmaB par la hauteur deformerait l'ellipse.
            float rx = Math.Max(3f, zone.SigmaA * SigmaScale * view.Width);
            float ry = Math.Max(3f, zone.SigmaB * SigmaScale * view.Width);

            Color identity = _rankColours.Checked ? ZoneTracker.RankColor(track.Rank) : track.Color;
            Color colour = flash ? Color.White : identity;
            float width = flash ? 4f : isMajor ? 2.8f : 1.2f;
            int alpha = isMajor || flash ? 255 : 110;

            using (var pen = new Pen(Color.FromArgb(alpha, colour), width))
            {
                GraphicsState state = g.Save();
                g.TranslateTransform(cx, cy);
                g.RotateTransform(zone.Angle * 180f / MathF.PI);
                g.DrawEllipse(pen, -rx, -ry, rx * 2f, ry * 2f);
                g.Restore(state);
            }

            using var fill = new SolidBrush(Color.FromArgb(alpha, colour));
            g.FillEllipse(fill, cx - 3f, cy - 3f, 6f, 6f);

            string label = $"{zone.Percentage:F1} %";
            Font font = isMajor ? labelFont : minorFont;
            SizeF size = g.MeasureString(label, font);
            var box = new RectangleF(cx - size.Width / 2f, cy - ry - size.Height - 8f, size.Width, size.Height);
            g.FillRectangle(halo, box);
            g.DrawString(label, font, fill, box.Location);

            if (!isMajor)
            {
                continue;
            }

            // Pastille de rang : c'est elle qui « souligne les meilleures ».
            var badge = new RectangleF(cx - ry - 14f, cy - ry - 14f, 24f, 24f);
            g.FillEllipse(fill, badge);
            using var badgeText = new SolidBrush(Background);
            SizeF rankSize = g.MeasureString(track.Rank.ToString(), badgeFont);
            g.DrawString(
                track.Rank.ToString(),
                badgeFont,
                badgeText,
                badge.X + (badge.Width - rankSize.Width) / 2f,
                badge.Y + (badge.Height - rankSize.Height) / 2f);
        }
    }

    /// <summary>
    /// Le tableau tel que l'overlay le recevra : une liste deja classee, sans identifiant.
    /// </summary>
    /// <remarks>
    /// C'est la vue de reference. Le graphique et le journal, eux, s'appuient sur un
    /// appariement des zones entre ticks dont l'overlay est incapable : ce sont des
    /// instruments de diagnostic, pas un apercu du produit.
    /// </remarks>
    private void PaintTable(object? sender, PaintEventArgs e)
    {
        if (_snapshot is not { } snapshot)
        {
            return;
        }

        Graphics g = e.Graphics;
        using var headerFont = new Font(FontFamily.GenericMonospace, 7.5f, FontStyle.Bold);
        using var rowFont = new Font(FontFamily.GenericMonospace, 8f);
        using var muted = new SolidBrush(Muted);
        using var separator = new Pen(Color.FromArgb(50, 255, 255, 255));

        g.DrawString("CE QUE RECOIT L'OVERLAY", headerFont, new SolidBrush(Color.FromArgb(110, 170, 255)), 8f, 4f);
        g.DrawString("  #    pct      cx     cy     sa     sb  angle", rowFont, muted, 8f, 20f);
        g.DrawLine(separator, 8f, 34f, _table.ClientSize.Width - 8f, 34f);

        int major = _majorZones.Value;
        float y = 38f;

        for (int i = 0; i < snapshot.Zones.Count && y < _table.ClientSize.Height - 26f; i++)
        {
            Zone zone = snapshot.Zones[i];
            bool isMajor = i < major;

            using var brush = new SolidBrush(isMajor ? ZoneTracker.RankColor(i + 1) : Muted);
            g.DrawString(
                $"{i + 1,3}  {zone.Percentage,5:F1}  {zone.Center.X,6:F3} {zone.Center.Y,6:F3} " +
                $"{zone.SigmaA,6:F3} {zone.SigmaB,6:F3} {zone.Angle,6:F2}",
                rowFont,
                brush,
                8f,
                y);

            y += 13f;
        }

        g.DrawLine(separator, 8f, _table.ClientSize.Height - 22f, _table.ClientSize.Width - 8f, _table.ClientSize.Height - 22f);
        g.DrawString(
            $"disperses {snapshot.ScatteredPercentage:F1} %   |   majeures = les {major} premieres lignes",
            rowFont,
            muted,
            8f,
            _table.ClientSize.Height - 18f);
    }

    private void PaintChart(object? sender, PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        Rectangle plot = new(44, 10, Math.Max(40, _chart.ClientSize.Width - 110), Math.Max(30, _chart.ClientSize.Height - 30));

        float scale = Math.Max(20f, _tracker.PeakPercentage * 1.15f);
        using var grid = new Pen(Color.FromArgb(45, 255, 255, 255));
        using var axisText = new SolidBrush(Muted);
        using var axisFont = new Font(FontFamily.GenericMonospace, 7.5f);

        for (int step = 0; step <= 4; step++)
        {
            float value = scale * step / 4f;
            float y = plot.Bottom - value / scale * plot.Height;
            g.DrawLine(grid, plot.Left, y, plot.Right, y);
            g.DrawString($"{value,4:F0}%", axisFont, axisText, 4f, y - 7f);
        }

        g.DrawString(
            $"{ZoneTracker.HistoryLength * TickMilliseconds / 1000} s d'historique",
            axisFont,
            axisText,
            plot.Left + 4f,
            plot.Bottom + 6f);

        int major = _majorZones.Value;
        float stepX = plot.Width / (float)(ZoneTracker.HistoryLength - 1);

        using var labelFont = new Font(FontFamily.GenericMonospace, 8f, FontStyle.Bold);

        foreach (ZoneTracker.Track track in _tracker.Tracks.OrderByDescending(t => t.Rank))
        {
            if (track.Length < 2)
            {
                continue;
            }

            bool isMajor = track.Rank <= major;
            var points = new PointF[track.Length];
            for (int i = 0; i < track.Length; i++)
            {
                float x = plot.Right - (track.Length - 1 - i) * stepX;
                float y = plot.Bottom - Math.Clamp(track[i] / scale, 0f, 1f) * plot.Height;
                points[i] = new PointF(x, y);
            }

            using var pen = new Pen(
                Color.FromArgb(isMajor ? 255 : 90, track.HighlightTicks > 0 ? Color.White : track.Color),
                isMajor ? 2.2f : 1.1f);
            g.DrawLines(pen, points);

            if (!isMajor)
            {
                continue;
            }

            using var brush = new SolidBrush(track.Color);
            g.DrawString(
                $"{track.Rank}  {track.Percentage:F1}%",
                labelFont,
                brush,
                plot.Right + 6f,
                points[^1].Y - 7f);
        }
    }

    /// <summary>Le plus grand rectangle 16:9 tenant dans la zone de dessin.</summary>
    private static Rectangle Viewport(Size available)
    {
        const int Margin = 10;
        int width = Math.Max(16, available.Width - Margin * 2);
        int height = Math.Max(9, available.Height - Margin * 2);

        if (width * Grid.Height > height * Grid.Width)
        {
            width = height * Grid.Width / Grid.Height;
        }
        else
        {
            height = width * Grid.Height / Grid.Width;
        }

        return new Rectangle((available.Width - width) / 2, (available.Height - height) / 2, width, height);
    }

    private TrackBar AddSlider(
        Control parent,
        string label,
        int minimum,
        int maximum,
        int value,
        Func<int, string> format,
        Action? onChange)
    {
        var caption = new Label
        {
            Width = 290,
            Height = 17,
            ForeColor = Color.FromArgb(200, 205, 212),
            Font = new Font(FontFamily.GenericSansSerif, 8.25f),
            Margin = new Padding(0, 3, 0, 0),
        };

        var bar = new SteadyTrackBar
        {
            Width = 290,
            Minimum = minimum,
            Maximum = maximum,
            Value = value,
            TickStyle = TickStyle.None,
        };

        caption.Text = $"{label} : {format(value)}";
        bar.ValueChanged += (_, _) =>
        {
            caption.Text = $"{label} : {format(bar.Value)}";
            onChange?.Invoke();
        };

        parent.Controls.Add(caption);
        parent.Controls.Add(bar);
        return bar;
    }

    private static CheckBox AddCheck(Control parent, string text, bool value)
    {
        var box = new CheckBox
        {
            Text = text,
            Checked = value,
            ForeColor = Color.Gainsboro,
            Width = 290,
            Height = 22,
        };
        parent.Controls.Add(box);
        return box;
    }

    private static Label Caption(string text) => new()
    {
        Text = text.ToUpperInvariant(),
        Width = 290,
        Height = 22,
        ForeColor = Color.FromArgb(110, 170, 255),
        Font = new Font(FontFamily.GenericSansSerif, 8f, FontStyle.Bold),
        TextAlign = ContentAlignment.BottomLeft,
        Margin = new Padding(0, 10, 0, 2),
    };

    private static Label StatusLabel(Color foreground) => new()
    {
        Dock = DockStyle.Bottom,
        Height = 24,
        ForeColor = foreground,
        BackColor = Surface,
        Font = new Font(FontFamily.GenericMonospace, 9f),
        TextAlign = ContentAlignment.MiddleLeft,
        Padding = new Padding(10, 0, 0, 0),
    };

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _liveTimer.Dispose();
        _fieldImage.Dispose();
        base.OnFormClosed(e);
    }

    private sealed class BufferedPanel : Panel
    {
        public BufferedPanel() => DoubleBuffered = true;
    }

    /// <summary>
    /// Un curseur qui ignore la molette et fait defiler son panneau a la place.
    /// </summary>
    /// <remarks>
    /// Par defaut, faire defiler la liste des reglages modifie silencieusement le curseur
    /// survole. Sur un banc d'essai c'est intenable : on croit comparer deux reglages
    /// alors qu'un troisieme a bouge.
    /// </remarks>
    private sealed class SteadyTrackBar : TrackBar
    {
        protected override void OnMouseWheel(MouseEventArgs e)
        {
            if (e is HandledMouseEventArgs handled)
            {
                handled.Handled = true;
            }

            if (Parent is ScrollableControl scrollable && scrollable.AutoScroll)
            {
                Point position = scrollable.AutoScrollPosition;
                scrollable.AutoScrollPosition = new Point(-position.X, -position.Y - e.Delta);
            }
        }
    }
}
