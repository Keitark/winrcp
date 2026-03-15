namespace RcpPlayer.Core.Model;

public enum RcpFormat
{
    RcpV2,
    G36
}

public sealed class RcpSong
{
    public required RcpFormat Format { get; init; }
    public required string Title { get; init; }
    public required string Comment { get; init; }
    public required int TimeBase { get; init; }
    public required int TempoBpm { get; init; }
    public required int BeatNumerator { get; init; }
    public required int BeatDenominator { get; init; }
    public int GlobalTransposition { get; init; }
    public required string? Cm6FileName { get; init; }
    public required string? GsdAFileName { get; init; }
    public required string? GsdBFileName { get; init; }
    public required IReadOnlyList<RcpUserExclusive> UserExclusives { get; init; }
    public required IReadOnlyList<RcpTrack> Tracks { get; init; }
}

public sealed class RcpTrack
{
    public required int TrackId { get; init; }
    public required string Name { get; init; }
    public required int DefaultChannel { get; init; }
    public required bool IsMuted { get; init; }
    public int RhythmMode { get; init; }
    public int TrackTransposition { get; init; }
    public int StartTick { get; init; }
    public bool IsDummyChannel { get; init; }
    public required IReadOnlyList<RcpEvent> Events { get; init; }
}

public sealed class RcpUserExclusive
{
    public required string Name { get; init; }
    public required byte[] DataWithoutLeadingF0 { get; init; }
}

public sealed class RcpEvent
{
    public required int Index { get; init; }
    public required byte CommandOrNote { get; init; }
    public required int DelayTicks { get; init; }
    public required int Param1 { get; init; }
    public required int Param2 { get; init; }
    public required int RawLength { get; init; }
}
