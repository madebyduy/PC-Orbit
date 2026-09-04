namespace PcOrbit.Core.Events;

/// <param name="SourceId">Which source could not be read.</param>
/// <param name="Problem">Why. Shown to the user, not swallowed.</param>
public sealed record UnavailableSource(string SourceId, string Problem);

/// <param name="Events">Newest first.</param>
/// <param name="Unavailable">
/// Sources that could not be read. A timeline with a hole in it has to say where the hole is.
/// </param>
public sealed record Timeline(
    DateTimeOffset Since,
    IReadOnlyList<ChangeEvent> Events,
    IReadOnlyList<UnavailableSource> Unavailable)
{
    public bool IsComplete => Unavailable.Count == 0;
}

/// <summary>
/// Merges what PC Orbit did with what happened to the machine anyway.
/// </summary>
/// <remarks>
/// Pure, and deliberately so: the ordering rule below is the only thing standing between a useful
/// timeline and a list where a Windows update and the crash it caused appear in whichever order
/// two clocks happened to round to.
/// </remarks>
public static class TimelineBuilder
{
    public static Timeline Build(
        IEnumerable<ChangeEvent> own,
        IEnumerable<ChangeSourceResult> external,
        DateTimeOffset since,
        int limit)
    {
        ArgumentNullException.ThrowIfNull(own);
        ArgumentNullException.ThrowIfNull(external);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        List<ChangeSourceResult> results = [.. external];

        IEnumerable<ChangeEvent> all = own.Concat(results.SelectMany(r => r.Events))
            .Where(e => e.Timestamp >= since);

        return new Timeline(
            since,

            // Ties broken by id, not by arrival: two events stamped to the same second must come
            // back in the same order every run, or "what happened just before" changes each time
            // you ask.
            [.. all.OrderByDescending(e => e.Timestamp)
                   .ThenBy(e => e.Id, StringComparer.Ordinal)
                   .Take(limit)],
            [.. results.Where(r => r.Failed)
                       .Select(r => new UnavailableSource(r.SourceId, r.Problem!))
                       .OrderBy(u => u.SourceId, StringComparer.Ordinal)]);
    }

    /// <summary>
    /// What happened in the window before a moment — the Update Regression Guard question.
    /// </summary>
    /// <remarks>
    /// Correlation, and it is labelled as correlation everywhere it surfaces. Something changing
    /// twenty minutes before a machine started crashing is a lead worth following, and the research
    /// is right that stating it as a cause is how a diagnostic tool starts inventing culprits
    /// (§7.3: only say "overview" or "likely" until there is enough evidence for cause).
    /// </remarks>
    public static IReadOnlyList<ChangeEvent> Preceding(Timeline timeline, DateTimeOffset moment, TimeSpan window)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(window.Ticks);

        DateTimeOffset from = moment - window;

        return [.. timeline.Events.Where(e => e.Timestamp >= from && e.Timestamp <= moment)];
    }
}
