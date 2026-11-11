using HitMapAggregator.Api.Security;
using Microsoft.Extensions.Options;

namespace HitMapAggregator.Tests;

public sealed class OverlayTokensTests : IDisposable
{
    private static readonly IOptions<HitMapSecrets> Secrets = Options.Create(new HitMapSecrets
    {
        OverlayToken = Convert.ToBase64String(Enumerable.Repeat((byte)0x3C, 32).ToArray()),
    });

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"hitmap-generations-{Guid.NewGuid():N}.json");

    [Fact]
    public void A_rotation_survives_a_restart()
    {
        var before = new OverlayTokens(Secrets, _path);
        string original = before.Current("canal");
        string rotated = before.Rotate("canal");

        // Nouvelle instance sur le meme fichier : c'est ce que voit un serveur redemarre.
        var after = new OverlayTokens(Secrets, _path);

        Assert.Equal(rotated, after.Current("canal"));
        Assert.True(after.IsValid("canal", rotated));
        Assert.False(after.IsValid("canal", original));
    }

    [Fact]
    public void Without_a_file_every_channel_starts_at_generation_zero()
    {
        var first = new OverlayTokens(Secrets, _path);
        var second = new OverlayTokens(Secrets, _path);

        Assert.Equal(first.Current("canal"), second.Current("canal"));
        Assert.False(File.Exists(_path));
    }

    [Fact]
    public void An_unreadable_file_stops_the_start_instead_of_resetting_silently()
    {
        File.WriteAllText(_path, "pas du json");

        Assert.ThrowsAny<Exception>(() => new OverlayTokens(Secrets, _path));
    }

    public void Dispose() => File.Delete(_path);
}
