using System.Globalization;
using HitMapAggregator.Core;
using HitMapAggregator.Core.Aggregation;
using HitMapAggregator.Core.Models;
using HitMapAggregator.Tests;

namespace HitMapAggregator.Visualizer;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // Sans argument, ou avec « gui » : le banc d'essai interactif.
        // Avec un nom de scenario ou « all » : l'export SVG en lot, inchange.
        if (args.Length == 0 || string.Equals(args[0], "gui", StringComparison.OrdinalIgnoreCase))
        {
            return RunExplorer();
        }

        // Capture la fenetre apres quelques ticks. Windows refuse de passer au premier
        // plan une fenetre lancee depuis un terminal, donc une copie d'ecran externe
        // photographie ce qui la recouvre : il faut que l'application se dessine
        // elle-meme.
        if (string.Equals(args[0], "shot", StringComparison.OrdinalIgnoreCase))
        {
            return Screenshot(args.Length > 1 ? args[1] : "explorer.png");
        }

        return ExportSvg(args);
    }

    private static int RunExplorer()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.Run(new ExplorerForm());
        return 0;
    }

    private static int Screenshot(string path)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        using var form = new ExplorerForm();
        form.Show();

        // Laisse tourner quelques ticks pour que le graphique ait un historique.
        for (int i = 0; i < 60; i++)
        {
            Application.DoEvents();
            Thread.Sleep(50);
        }

        using var bitmap = new System.Drawing.Bitmap(form.Width, form.Height);
        form.DrawToBitmap(bitmap, new System.Drawing.Rectangle(0, 0, form.Width, form.Height));
        bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);

        form.Close();
        Console.WriteLine($"capture -> {path}");
        return 0;
    }

    private static int ExportSvg(string[] args)
    {
        string requested = args[0];
        string outputDirectory = args.Length > 1 ? args[1] : Path.Combine(AppContext.BaseDirectory, "svg");
        int count = args.Length > 2 ? int.Parse(args[2], CultureInfo.InvariantCulture) : 20_000;

        Scenario[] scenarios;
        if (string.Equals(requested, "all", StringComparison.OrdinalIgnoreCase))
        {
            scenarios = [.. SyntheticData.All];
        }
        else if (Enum.TryParse(requested, ignoreCase: true, out Scenario single))
        {
            scenarios = [single];
        }
        else
        {
            Console.Error.WriteLine($"Scenario inconnu : {requested}");
            Console.Error.WriteLine($"Attendu : gui, all, {string.Join(", ", SyntheticData.All)}");
            return 1;
        }

        Directory.CreateDirectory(outputDirectory);

        var settings = new SessionSettings();
        float[] primary = new float[Grid.CellCount];
        float[] scratch = new float[Grid.CellCount];

        foreach (Scenario scenario in scenarios)
        {
            NormalizedPoint[] points = SyntheticData.Generate(scenario, count);
            HeatmapSnapshot snapshot = SnapshotBuilder.Build(points, settings, primary, scratch);

            string path = Path.Combine(outputDirectory, $"{scenario}.svg");
            File.WriteAllText(path, SnapshotSvgWriter.Render(scenario.ToString(), points, snapshot));

            string parts = string.Join(
                " ",
                snapshot.Zones.Select(z => z.Percentage.ToString("F1", CultureInfo.InvariantCulture)));

            Console.WriteLine(
                $"{scenario,-12} {snapshot.Zones.Count} zone(s), " +
                $"disperses {snapshot.ScatteredPercentage,5:F1} %, parts [{parts}]");
            Console.WriteLine($"             -> {path}");
        }

        return 0;
    }
}
