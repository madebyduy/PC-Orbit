using PcOrbit.Core.Events;
using PcOrbit.Core.Model;

namespace PcOrbit.Core.Tests;

/// <summary>
/// The root-cause timeline. Its job is to put our changes and everyone else's on one axis without
/// hiding the parts it could not see.
/// </summary>
public sealed class TimelineTests
{
    private static readonly DateTimeOffset Noon = new(2026, 9, 4, 12, 0, 0, TimeSpan.Zero);

    private static ChangeEvent Event(string id, DateTimeOffset at, EventSource source = EventSource.EventLog) => new(
        id,
        at,
        source,
        EventCategory.Update,
        $"component-{id}",
        Before: null,
        After: null,
        Initiator.System,
        Confidence.High);

    [Fact]
    public void OurChangesAndWindowsChangesEndUpOnOneAxisNewestFirst()
    {
        Timeline timeline = TimelineBuilder.Build(
            [Event("own", Noon.AddHours(-1), EventSource.PcOrbit)],
            [new ChangeSourceResult("updates", [Event("update", Noon)])],
            Noon.AddDays(-1),
            limit: 10);

        Assert.Equal(["update", "own"], timeline.Events.Select(e => e.Id));
    }

    [Fact]
    public void EventsBeforeTheWindowAreLeftOut()
    {
        Timeline timeline = TimelineBuilder.Build(
            [Event("old", Noon.AddDays(-30))],
            [new ChangeSourceResult("updates", [Event("recent", Noon)])],
            Noon.AddDays(-7),
            limit: 10);

        Assert.Equal("recent", Assert.Single(timeline.Events).Id);
    }

    /// <summary>
    /// Two events stamped to the same instant must come back in the same order every time, or
    /// "what happened just before this" is a different answer on each run.
    /// </summary>
    [Fact]
    public void EventsAtTheSameInstantAreOrderedStably()
    {
        ChangeSourceResult source = new("log", [Event("bbb", Noon), Event("aaa", Noon)]);

        for (int i = 0; i < 5; i++)
        {
            Timeline timeline = TimelineBuilder.Build([], [source], Noon.AddDays(-1), limit: 10);
            Assert.Equal(["aaa", "bbb"], timeline.Events.Select(e => e.Id));
        }
    }

    /// <summary>
    /// The point of the whole design: a source that could not be read is reported, not silently
    /// treated as a quiet machine.
    /// </summary>
    [Fact]
    public void ASourceThatCouldNotBeReadIsNamedRatherThanSwallowed()
    {
        Timeline timeline = TimelineBuilder.Build(
            [],
            [
                new ChangeSourceResult("updates", [Event("update", Noon)]),
                ChangeSourceResult.Unavailable("restore-points", "needs administrator rights"),
            ],
            Noon.AddDays(-1),
            limit: 10);

        Assert.False(timeline.IsComplete);

        UnavailableSource unavailable = Assert.Single(timeline.Unavailable);
        Assert.Equal("restore-points", unavailable.SourceId);
        Assert.Equal("needs administrator rights", unavailable.Problem);

        // And the readable source still contributed. One blocked source does not lose the rest.
        Assert.Single(timeline.Events);
    }

    [Fact]
    public void ATimelineWithEverySourceReadableSaysItIsComplete()
    {
        Timeline timeline = TimelineBuilder.Build(
            [],
            [new ChangeSourceResult("updates", [])],
            Noon.AddDays(-1),
            limit: 10);

        Assert.True(timeline.IsComplete);
        Assert.Empty(timeline.Events);
    }

    [Fact]
    public void TheLimitKeepsTheNewestEventsRatherThanTheFirstOnesFound()
    {
        ChangeSourceResult source = new(
            "log",
            [.. Enumerable.Range(0, 10).Select(i => Event($"e{i:D2}", Noon.AddMinutes(-i)))]);

        Timeline timeline = TimelineBuilder.Build([], [source], Noon.AddDays(-1), limit: 3);

        Assert.Equal(["e00", "e01", "e02"], timeline.Events.Select(e => e.Id));
    }

    // ---------------------------------------------------------------- regression window

    [Fact]
    public void PrecedingReturnsOnlyWhatHappenedInsideTheWindowBeforeTheMoment()
    {
        Timeline timeline = TimelineBuilder.Build(
            [],
            [
                new ChangeSourceResult("log",
                [
                    Event("long-before", Noon.AddHours(-5)),
                    Event("just-before", Noon.AddMinutes(-20)),
                    Event("after", Noon.AddMinutes(10)),
                ]),
            ],
            Noon.AddDays(-1),
            limit: 50);

        IReadOnlyList<ChangeEvent> preceding = TimelineBuilder.Preceding(timeline, Noon, TimeSpan.FromHours(1));

        Assert.Equal("just-before", Assert.Single(preceding).Id);
    }

    [Fact]
    public void PrecedingIncludesSomethingStampedAtExactlyTheMoment()
    {
        Timeline timeline = TimelineBuilder.Build(
            [],
            [new ChangeSourceResult("log", [Event("simultaneous", Noon)])],
            Noon.AddDays(-1),
            limit: 50);

        Assert.Single(TimelineBuilder.Preceding(timeline, Noon, TimeSpan.FromMinutes(5)));
    }
}
