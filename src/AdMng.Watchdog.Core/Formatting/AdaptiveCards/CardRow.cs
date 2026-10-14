namespace AdMng.Watchdog.Core.Formatting.AdaptiveCards;

/// <summary>One removable row of a <see cref="CardSection"/>; <paramref name="Items"/> = how many events it shows
/// (a lockout roll-up row stands for several).</summary>
public sealed record CardRow(AdaptiveElement Element, int Items = 1);
