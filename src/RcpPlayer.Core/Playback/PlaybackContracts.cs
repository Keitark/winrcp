namespace RcpPlayer.Core.Playback;

public enum MidiMessageKind
{
    Short,
    SysEx
}

public sealed class MidiEventPacket
{
    public required MidiMessageKind Kind { get; init; }
    public required uint ShortMessage { get; init; }
    public required byte[]? SysExData { get; init; }
}

public sealed class ScheduledMidiEvent
{
    public required long Tick { get; init; }
    public required MidiEventPacket Packet { get; init; }
    public int SourceTrackId { get; init; } = -1;
    public int SourceEventIndex { get; init; } = -1;
    public byte SourceCommand { get; init; }
}

public sealed class TempoEvent
{
    public required long Tick { get; init; }
    public required double Bpm { get; init; }
}

public sealed class MidiEndpointInfo
{
    public required string Id { get; init; }
    public required string Name { get; init; }
}

public sealed class PlaybackRunDiagnostics
{
    public int TotalEvents { get; set; }
    public int ShortEventsSent { get; set; }
    public int SysExEventsSent { get; set; }
    public int FilteredEvents { get; set; }
    public int LateEventsOver2Ms { get; set; }
    public int LateEventsOver5Ms { get; set; }
    public int LateEventsOver10Ms { get; set; }
    public double MaxLateByMs { get; set; }
    public int MaxEventsPerTick { get; set; }
    public int SendCallsOver1Ms { get; set; }
    public int SendCallsOver2Ms { get; set; }
    public int SendCallsOver5Ms { get; set; }
    public double MaxSendCallMs { get; set; }
}

public interface IMidiOutput : IAsyncDisposable
{
    Task<IReadOnlyList<MidiEndpointInfo>> GetEndpointsAsync(CancellationToken cancellationToken = default);
    Task OpenAsync(string endpointId, CancellationToken cancellationToken = default);
    Task SendShortAsync(uint shortMessage, CancellationToken cancellationToken = default);
    Task SendSysExAsync(byte[] data, CancellationToken cancellationToken = default);
    Task CloseAsync();
}
