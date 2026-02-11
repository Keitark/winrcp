namespace RcpPlayer.Core.Playback;

internal static class MidiEventOrdering
{
    public static IReadOnlyList<ScheduledMidiEvent> OrderByTimeline(IEnumerable<ScheduledMidiEvent> events)
    {
        return events
            .Select((e, i) => new IndexedEvent(e, i))
            .OrderBy(x => x.Event.Tick)
            .ThenBy(x => x.Index)
            .Select(x => x.Event)
            .ToList();
    }

    private readonly record struct IndexedEvent(ScheduledMidiEvent Event, int Index);
}
