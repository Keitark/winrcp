using System.Buffers.Binary;
using System.Text;
using RcpPlayer.Core.Model;

namespace RcpPlayer.Core.Parsing;

public sealed class RcpParser
{
    private static readonly Encoding ShiftJis;

    static RcpParser()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        ShiftJis = Encoding.GetEncoding(932);
    }

    public RcpSong Parse(byte[] fileData, RcpParserOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(fileData);
        options ??= new RcpParserOptions();

        var data = new ReadOnlySpan<byte>(fileData);
        return IsG36(data) ? ParseG36(data, options) : ParseRcpV2(data, options);
    }

    private static bool IsG36(ReadOnlySpan<byte> data)
    {
        const string g36Head = "COME ON MUSIC RECOMPOSER RCP3.0";
        if (data.Length < g36Head.Length)
        {
            return false;
        }

        return data[..g36Head.Length].SequenceEqual(Encoding.ASCII.GetBytes(g36Head));
    }

    private static RcpSong ParseRcpV2(ReadOnlySpan<byte> data, RcpParserOptions options)
    {
        const int minimumHeaderLength = 0x586;
        if (data.Length < minimumHeaderLength)
        {
            throw new RcpParseException("RCP data is shorter than expected header.");
        }

        const int titleOffset = 0x20;
        const int titleLength = 0x28;
        const int extendedTitleOffset = 0x48;
        const int extendedTitleLength = 0x18;
        const int commentOffset = 0x60;
        const int commentLength = 0x150;
        const int cm6Offset = 0x1C6;
        const int gsdOffset = 0x1D6;
        const int trackCountOffset = 0x1E6;
        const int userSysExOffset = 0x406;
        const int trackDataOffset = 0x586;

        var title = DecodeText(data[titleOffset..(titleOffset + titleLength)]);
        if (options.EnableExtendedTitle24)
        {
            var extensionBytes = data[extendedTitleOffset..(extendedTitleOffset + extendedTitleLength)];
            if (LooksLikeExtendedTitleBytes(extensionBytes))
            {
                var extension = DecodeTextPreserveLeading(extensionBytes);
                if (HasMeaningfulText(extension))
                {
                    title += extension;
                }
            }
        }

        var comment = DecodeMultiline(data[commentOffset..(commentOffset + commentLength)], 0x1C);
        var timeBase = data[0x1C0] + (data[0x1E7] << 8);
        var tempo = data[0x1C1];
        var beatN = data[0x1C2];
        var beatD = data[0x1C3];
        var cm6 = DecodeText(data[cm6Offset..(cm6Offset + 0x10)]);
        var gsd = DecodeText(data[gsdOffset..(gsdOffset + 0x10)]);
        var declaredTrackCount = data[trackCountOffset];
        var userEx = ParseUserExclusive(data[userSysExOffset..(userSysExOffset + 0x30 * 8)]);
        var tracks = ParseTracksRcp(data[trackDataOffset..], declaredTrackCount, options);

        return new RcpSong
        {
            Format = RcpFormat.RcpV2,
            Title = title,
            Comment = comment,
            TimeBase = Math.Max(timeBase, 1),
            TempoBpm = Math.Max((int)tempo, 1),
            BeatNumerator = Math.Max((int)beatN, 1),
            BeatDenominator = Math.Max((int)beatD, 1),
            Cm6FileName = string.IsNullOrWhiteSpace(cm6) ? null : cm6,
            GsdAFileName = string.IsNullOrWhiteSpace(gsd) ? null : gsd,
            GsdBFileName = null,
            UserExclusives = userEx,
            Tracks = tracks
        };
    }

    private static RcpSong ParseG36(ReadOnlySpan<byte> data, RcpParserOptions options)
    {
        const int minimumHeaderLength = 0xC98;
        if (data.Length < minimumHeaderLength)
        {
            throw new RcpParseException("G36 data is shorter than expected header.");
        }

        const int titleOffset = 0x20;
        const int titleLength = 0x80;
        const int commentOffset = 0xA0;
        const int commentLength = 0x168;
        const int trackCountOffset = 0x208;
        const int timeBaseOffset = 0x20A;
        const int tempoOffset = 0x20C;
        const int beatNOffset = 0x20E;
        const int beatDOffset = 0x20F;
        const int gsdAOffset = 0x298;
        const int gsdBOffset = 0x2A8;
        const int cm6Offset = 0x2B8;
        const int userSysExOffset = 0xB18;
        const int trackDataOffset = 0xC98;

        var title = DecodeText(data[titleOffset..(titleOffset + titleLength)]);
        var comment = DecodeMultiline(data[commentOffset..(commentOffset + commentLength)], 0x1E);
        var declaredTrackCount = ReadUInt16(data, trackCountOffset);
        var timeBase = ReadUInt16(data, timeBaseOffset);
        var tempo = ReadUInt16(data, tempoOffset);
        var beatN = data[beatNOffset];
        var beatD = data[beatDOffset];
        var gsdA = DecodeText(data[gsdAOffset..(gsdAOffset + 0x10)]);
        var gsdB = DecodeText(data[gsdBOffset..(gsdBOffset + 0x10)]);
        var cm6 = DecodeText(data[cm6Offset..(cm6Offset + 0x10)]);
        var userEx = ParseUserExclusive(data[userSysExOffset..(userSysExOffset + 0x30 * 8)]);
        var tracks = ParseTracksG36(data[trackDataOffset..], declaredTrackCount, options);

        return new RcpSong
        {
            Format = RcpFormat.G36,
            Title = title,
            Comment = comment,
            TimeBase = Math.Max((int)timeBase, 1),
            TempoBpm = Math.Max((int)tempo, 1),
            BeatNumerator = Math.Max((int)beatN, 1),
            BeatDenominator = Math.Max((int)beatD, 1),
            Cm6FileName = string.IsNullOrWhiteSpace(cm6) ? null : cm6,
            GsdAFileName = string.IsNullOrWhiteSpace(gsdA) ? null : gsdA,
            GsdBFileName = string.IsNullOrWhiteSpace(gsdB) ? null : gsdB,
            UserExclusives = userEx,
            Tracks = tracks
        };
    }

    private static IReadOnlyList<RcpUserExclusive> ParseUserExclusive(ReadOnlySpan<byte> data)
    {
        var result = new List<RcpUserExclusive>(8);
        for (var i = 0; i < 8; i++)
        {
            var chunk = data.Slice(i * 0x30, 0x30);
            var name = DecodeText(chunk[..0x18]);
            var sysExBody = TrimSysExPadding(chunk.Slice(0x18, 0x18).ToArray());
            result.Add(new RcpUserExclusive
            {
                Name = string.IsNullOrWhiteSpace(name) ? $"UserEx{i + 1}" : name,
                DataWithoutLeadingF0 = sysExBody
            });
        }

        return result;
    }

    private static IReadOnlyList<RcpTrack> ParseTracksRcp(ReadOnlySpan<byte> trackData, int declaredTrackCount, RcpParserOptions options)
    {
        var tracks = new List<RcpTrack>();
        var offset = 0;
        var indexLimit = declaredTrackCount <= 0 ? 0x24 : declaredTrackCount;

        while (offset + 0x2C <= trackData.Length && tracks.Count < indexLimit)
        {
            var length = ReadUInt16(trackData, offset);
            if (length <= 0x2C || offset + length > trackData.Length)
            {
                break;
            }

            var trackSpan = trackData.Slice(offset, length);
            var trackId = trackSpan[2];
            var channel = NormalizeChannel(trackSpan[4]);
            var mute = trackSpan[7] == 0x01;
            var trackName = DecodeText(trackSpan.Slice(8, 0x24));
            var events = new List<RcpEvent>();
            var eventArea = trackSpan[0x2C..];

            for (var cursor = 0; cursor + 4 <= eventArea.Length; cursor += 4)
            {
                if (events.Count >= options.MaxTrackEvents)
                {
                    throw new RcpParseException("Track event count exceeded parser limit.");
                }

                var e = new RcpEvent
                {
                    Index = events.Count,
                    CommandOrNote = eventArea[cursor],
                    DelayTicks = eventArea[cursor + 1],
                    Param1 = eventArea[cursor + 2],
                    Param2 = eventArea[cursor + 3],
                    RawLength = 4
                };
                events.Add(e);
                if (e.CommandOrNote == 0xFE)
                {
                    break;
                }
            }

            tracks.Add(new RcpTrack
            {
                TrackId = trackId == 0 ? tracks.Count + 1 : trackId,
                Name = string.IsNullOrWhiteSpace(trackName) ? $"Track {tracks.Count + 1}" : trackName,
                DefaultChannel = channel,
                IsMuted = mute,
                Events = events
            });

            offset += length;
        }

        return tracks;
    }

    private static IReadOnlyList<RcpTrack> ParseTracksG36(ReadOnlySpan<byte> trackData, int declaredTrackCount, RcpParserOptions options)
    {
        var tracks = new List<RcpTrack>();
        var offset = 0;
        var indexLimit = declaredTrackCount <= 0 ? 0x24 : declaredTrackCount;

        while (offset + 0x2E <= trackData.Length && tracks.Count < indexLimit)
        {
            var length = ReadInt32(trackData, offset);
            if (length <= 0x2E || offset + length > trackData.Length)
            {
                break;
            }

            var trackSpan = trackData.Slice(offset, length);
            var trackId = trackSpan[4];
            var channel = NormalizeChannel(trackSpan[6]);
            var mute = trackSpan[9] == 0x01;
            var trackName = DecodeText(trackSpan.Slice(10, 0x24));
            var events = new List<RcpEvent>();
            var eventArea = trackSpan[0x2E..];

            for (var cursor = 0; cursor + 6 <= eventArea.Length; cursor += 6)
            {
                if (events.Count >= options.MaxTrackEvents)
                {
                    throw new RcpParseException("Track event count exceeded parser limit.");
                }

                var e = new RcpEvent
                {
                    Index = events.Count,
                    CommandOrNote = eventArea[cursor],
                    Param2 = eventArea[cursor + 1],
                    DelayTicks = ReadUInt16(eventArea, cursor + 2),
                    Param1 = ReadUInt16(eventArea, cursor + 4),
                    RawLength = 6
                };
                events.Add(e);
                if (e.CommandOrNote == 0xFE)
                {
                    break;
                }
            }

            tracks.Add(new RcpTrack
            {
                TrackId = trackId == 0 ? tracks.Count + 1 : trackId,
                Name = string.IsNullOrWhiteSpace(trackName) ? $"Track {tracks.Count + 1}" : trackName,
                DefaultChannel = channel,
                IsMuted = mute,
                Events = events
            });

            offset += length;
        }

        return tracks;
    }

    private static string DecodeMultiline(ReadOnlySpan<byte> data, int lineWidth)
    {
        var lines = new List<string>();
        for (var offset = 0; offset + lineWidth <= data.Length; offset += lineWidth)
        {
            var text = DecodeText(data.Slice(offset, lineWidth));
            if (!string.IsNullOrWhiteSpace(text))
            {
                lines.Add(text);
            }
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string DecodeText(ReadOnlySpan<byte> bytes)
    {
        var value = ShiftJis.GetString(bytes);
        var terminator = value.IndexOf('\0');
        if (terminator >= 0)
        {
            value = value[..terminator];
        }

        return value.Trim();
    }

    private static string DecodeTextPreserveLeading(ReadOnlySpan<byte> bytes)
    {
        var value = ShiftJis.GetString(bytes);
        var terminator = value.IndexOf('\0');
        if (terminator >= 0)
        {
            value = value[..terminator];
        }

        return value.TrimEnd();
    }

    private static bool LooksLikeExtendedTitleBytes(ReadOnlySpan<byte> bytes)
    {
        var hasText = false;
        for (var i = 0; i < bytes.Length; i++)
        {
            var b = bytes[i];
            if (b == 0x00)
            {
                continue;
            }

            if (b is >= 0x20 and <= 0x7E || b is >= 0xA1 and <= 0xDF)
            {
                hasText = true;
                continue;
            }

            if (!IsShiftJisLeadByte(b))
            {
                return false;
            }

            if (i + 1 >= bytes.Length)
            {
                return false;
            }

            var trail = bytes[++i];
            if (!IsShiftJisTrailByte(trail))
            {
                return false;
            }

            hasText = true;
        }

        return hasText;
    }

    private static bool IsShiftJisLeadByte(byte value)
    {
        return value is >= 0x81 and <= 0x9F or >= 0xE0 and <= 0xFC;
    }

    private static bool IsShiftJisTrailByte(byte value)
    {
        return value is >= 0x40 and <= 0x7E or >= 0x80 and <= 0xFC;
    }

    private static bool HasMeaningfulText(string value)
    {
        foreach (var c in value)
        {
            if (char.IsLetterOrDigit(c))
            {
                return true;
            }
        }

        return false;
    }

    private static byte[] TrimSysExPadding(byte[] bytes)
    {
        var index = Array.IndexOf(bytes, (byte)0xF7);
        return index >= 0 ? bytes.Take(index + 1).ToArray() : bytes;
    }

    private static int NormalizeChannel(byte raw)
    {
        if (raw == 0xFF || raw == 0x00)
        {
            return 0;
        }

        // Track header channel encoding:
        // 0x00..0x0F = port A ch 0..15
        // 0x10..0x1F = port B ch 0..15
        if (raw <= 0x0F)
        {
            return raw;
        }

        if (raw is >= 0x10 and <= 0x1F)
        {
            return raw - 0x10;
        }

        return raw & 0x0F;
    }

    private static ushort ReadUInt16(ReadOnlySpan<byte> data, int offset)
    {
        return BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(offset, 2));
    }

    private static int ReadInt32(ReadOnlySpan<byte> data, int offset)
    {
        return BinaryPrimitives.ReadInt32LittleEndian(data.Slice(offset, 4));
    }
}
