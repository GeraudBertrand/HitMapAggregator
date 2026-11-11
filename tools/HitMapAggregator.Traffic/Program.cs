namespace HitMapAggregator.Traffic;

/// <summary>
/// Trafic synthetique vers l'API : <c>simulate</c> pour alimenter un overlay a la main,
/// <c>load</c> pour la montee en charge NBomber et le verdict sur la duree du tick.
/// </summary>
internal static class Program
{
    private const string Usage = """
        Usage : HitMapAggregator.Traffic <simulate|load> [options]
          simulate   clics a debit fixe selon un scenario (--help pour les options)
          load       montee en charge NBomber, puis verdict sur le p99 du tick
        """;

    private static Task<int> Main(string[] args) => args.FirstOrDefault() switch
    {
        "simulate" => Simulate.RunAsync(args[1..]),
        "load" => Load.RunAsync(args[1..]),
        _ => Fail(),
    };

    private static Task<int> Fail()
    {
        Console.Error.WriteLine(Usage);
        return Task.FromResult(1);
    }
}
