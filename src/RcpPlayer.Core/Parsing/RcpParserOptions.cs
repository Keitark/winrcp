namespace RcpPlayer.Core.Parsing;

public sealed class RcpParserOptions
{
    public int MaxTrackEvents { get; init; } = 250_000;
    public bool EnableExtendedTitle24 { get; init; } = true;
}

public sealed class RcpParseException : Exception
{
    public RcpParseException(string message) : base(message)
    {
    }
}
