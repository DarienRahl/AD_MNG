namespace AdMng.Watchdog.Service.Hosting;

/// <summary>Bootstrap <c>Watchdog:Development</c>: the identity of the Development authentication scheme (spec 17.3).</summary>
public sealed record DevelopmentOptions
{
    public string[] Sids { get; init; } = [];

    public string Account { get; init; } = @"LAB\developer";
}
