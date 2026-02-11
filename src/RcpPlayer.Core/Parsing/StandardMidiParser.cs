using System.Buffers.Binary;
using System.Text;
using RcpPlayer.Core.Playback;

namespace RcpPlayer.Core.Parsing;

public sealed class StandardMidiTrackInfo
{
    public required int TrackId { get; init; }
    public required string Name { get; init; }
    public required int DefaultChannel { get; init; }
    public required bool IsMuted { get; init; }
}

public sealed class StandardMidiSong
{
    public required string Title { get; init; }
    public required string Comment { get; init; }
    public required int TimeBase { get; init; }
    public required int TempoBpm { get; init; }
    public required int BeatNumerator { get; init; }
    public required int BeatDenominator { get; init; }
    public required IReadOnlyList<StandardMidiTrackInfo> Tracks { get; init; }
    public required RcpPlaybackPlan PlaybackPlan { get; init; }
}

public sealed class StandardMidiParser
{
    public StandardMidiSong Parse(byte[] fileData)
    {
        ArgumentNullException.ThrowIfNull(fileData);
        var data = fileData.AsSpan();
        if (!LooksLikeStandardMidi(data))
        {
            throw new InvalidDataException("Not a Standard MIDI file.");
        }

        var offset = 0;
        ReadChunkHeader(data, ref offset, out var headerType, out var headerLength);
        if (!headerType.SequenceEqual("MThd"u8) || headerLength < 6)
        {
            throw new InvalidDataException("Invalid MIDI header chunk.");
        }

        if (offset + headerLength > data.Length)
        {
            throw new InvalidDataException("Truncated MIDI header chunk.");
        }

        var format = (int)ReadUInt16BE(data, ref offset);
        var trackCount = (int)ReadUInt16BE(data, ref offset);
        var division = (int)ReadUInt16BE(data, ref offset);

        if (format is < 0 or > 1)
        {
            throw new NotSupportedException($"MIDI format {format} is not supported.");
        }

        if ((division & 0x8000) != 0)
        {
            throw new NotSupportedException("SMPTE time division MIDI files are not supported.");
        }

        var ppqn = Math.Max(division, 1);
        offset += (headerLength - 6);

        var allEvents = new List<ScheduledMidiEvent>(16_384);
        var tempoEvents = new List<TempoEvent>(64);
        var trackInfos = new List<StandardMidiTrackInfo>(Math.Max(trackCount, 1));

        var title = string.Empty;
        var comment = string.Empty;
        var beatNumerator = 4;
        var beatDenominator = 4;

        for (var i = 0; i < trackCount; i++)
        {
            if (offset >= data.Length)
            {
                throw new InvalidDataException("Unexpected end of file while reading track chunks.");
            }

            ReadChunkHeader(data, ref offset, out var chunkType, out var chunkLength);
            if (!chunkType.SequenceEqual("MTrk"u8))
            {
                throw new InvalidDataException("Expected MTrk chunk.");
            }

            if (offset + chunkLength > data.Length)
            {
                throw new InvalidDataException("Truncated MIDI track chunk.");
            }

            var trackData = data.Slice(offset, chunkLength);
            offset += chunkLength;

            var info = ParseTrack(
                trackData,
                i,
                allEvents,
                tempoEvents,
                ref title,
                ref comment,
                ref beatNumerator,
                ref beatDenominator);
            trackInfos.Add(info);
        }

        var orderedTempo = tempoEvents
            .OrderBy(e => e.Tick)
            .GroupBy(e => e.Tick)
            .Select(g => g.Last())
            .ToList();

        var initialTempo = 120.0;
        var firstAtZero = orderedTempo.LastOrDefault(t => t.Tick == 0);
        if (firstAtZero is not null)
        {
            initialTempo = Math.Max(firstAtZero.Bpm, 1.0);
        }

        var orderedMidi = MidiEventOrdering.OrderByTimeline(allEvents);

        var playbackPlan = new RcpPlaybackPlan
        {
            TimeBase = ppqn,
            InitialTempoBpm = initialTempo,
            TempoEvents = orderedTempo,
            MidiEvents = orderedMidi,
            SourceSong = null,
            BuildDiagnostics = new RcpBuildDiagnostics
            {
                UnsupportedCommands = [],
                LoopExpansionLimitTracks = []
            }
        };

        if (string.IsNullOrWhiteSpace(title))
        {
            title = "STANDARD MIDI FILE";
        }

        return new StandardMidiSong
        {
            Title = title,
            Comment = comment,
            TimeBase = ppqn,
            TempoBpm = (int)Math.Round(initialTempo, MidpointRounding.AwayFromZero),
            BeatNumerator = Math.Max(beatNumerator, 1),
            BeatDenominator = Math.Max(beatDenominator, 1),
            Tracks = trackInfos,
            PlaybackPlan = playbackPlan
        };
    }

    public static bool LooksLikeStandardMidi(ReadOnlySpan<byte> data)
    {
        return data.Length >= 14 &&
               data[0] == (byte)'M' &&
               data[1] == (byte)'T' &&
               data[2] == (byte)'h' &&
               data[3] == (byte)'d';
    }

    private static StandardMidiTrackInfo ParseTrack(
        ReadOnlySpan<byte> trackData,
        int trackIndex,
        List<ScheduledMidiEvent> outEvents,
        List<TempoEvent> outTempoEvents,
        ref string songTitle,
        ref string songComment,
        ref int beatNumerator,
        ref int beatDenominator)
    {
        var position = 0;
        long tick = 0;
        var runningStatus = 0;
        var trackName = string.Empty;
        int? defaultChannel = null;

        while (position < trackData.Length)
        {
            var delta = ReadVariableLength(trackData, ref position);
            tick += delta;

            if (position >= trackData.Length)
            {
                break;
            }

            var statusByte = trackData[position++];
            int status;

            if (statusByte < 0x80)
            {
                if (runningStatus == 0)
                {
                    throw new InvalidDataException($"Track {trackIndex + 1}: running status used without prior status.");
                }

                status = runningStatus;
                position--;
            }
            else
            {
                status = statusByte;
            }

            if (status is >= 0x80 and <= 0xEF)
            {
                runningStatus = status;
                var dataBytes = ((status & 0xF0) == 0xC0 || (status & 0xF0) == 0xD0) ? 1 : 2;
                EnsureLength(trackData, position, dataBytes, trackIndex);
                var data1 = trackData[position++];
                var data2 = dataBytes == 2 ? trackData[position++] : (byte)0;

                defaultChannel ??= (status & 0x0F);
                outEvents.Add(new ScheduledMidiEvent
                {
                    Tick = tick,
                    Packet = new MidiEventPacket
                    {
                        Kind = MidiMessageKind.Short,
                        ShortMessage = PackShort((byte)status, data1, data2),
                        SysExData = null
                    }
                });
                continue;
            }

            runningStatus = 0;

            if (status == 0xFF)
            {
                EnsureLength(trackData, position, 1, trackIndex);
                var metaType = trackData[position++];
                var metaLen = ReadVariableLength(trackData, ref position);
                EnsureLength(trackData, position, metaLen, trackIndex);
                var metaData = trackData.Slice(position, metaLen);
                position += metaLen;

                switch (metaType)
                {
                    case 0x2F:
                        position = trackData.Length;
                        break;
                    case 0x03:
                    {
                        var text = DecodeText(metaData);
                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            trackName = text;
                            if (trackIndex == 0 && string.IsNullOrWhiteSpace(songTitle))
                            {
                                songTitle = text;
                            }
                        }
                        break;
                    }
                    case 0x01:
                    {
                        if (string.IsNullOrWhiteSpace(songComment))
                        {
                            var text = DecodeText(metaData);
                            if (!string.IsNullOrWhiteSpace(text))
                            {
                                songComment = text;
                            }
                        }
                        break;
                    }
                    case 0x51 when metaData.Length == 3:
                    {
                        var usPerQuarter = (metaData[0] << 16) | (metaData[1] << 8) | metaData[2];
                        if (usPerQuarter > 0)
                        {
                            outTempoEvents.Add(new TempoEvent
                            {
                                Tick = tick,
                                Bpm = 60_000_000.0 / usPerQuarter
                            });
                        }
                        break;
                    }
                    case 0x58 when metaData.Length >= 2:
                    {
                        beatNumerator = Math.Max((int)metaData[0], 1);
                        var power = Math.Clamp(metaData[1], (byte)0, (byte)7);
                        beatDenominator = 1 << power;
                        break;
                    }
                }

                continue;
            }

            if (status is 0xF0 or 0xF7)
            {
                var len = ReadVariableLength(trackData, ref position);
                EnsureLength(trackData, position, len, trackIndex);
                var payload = trackData.Slice(position, len).ToArray();
                position += len;

                var sysEx = NormalizeSysEx(status, payload);
                if (sysEx is { Length: > 1 })
                {
                    outEvents.Add(new ScheduledMidiEvent
                    {
                        Tick = tick,
                        Packet = new MidiEventPacket
                        {
                            Kind = MidiMessageKind.SysEx,
                            ShortMessage = 0,
                            SysExData = sysEx
                        }
                    });
                }

                continue;
            }

            // Unsupported system common/realtime data in file stream.
            var systemDataLength = status switch
            {
                0xF1 => 1,
                0xF2 => 2,
                0xF3 => 1,
                _ => 0
            };

            EnsureLength(trackData, position, systemDataLength, trackIndex);
            position += systemDataLength;
        }

        if (string.IsNullOrWhiteSpace(trackName))
        {
            trackName = $"Track {trackIndex + 1:00}";
        }

        return new StandardMidiTrackInfo
        {
            TrackId = trackIndex + 1,
            Name = trackName,
            DefaultChannel = defaultChannel ?? (trackIndex % 16),
            IsMuted = false
        };
    }

    private static void ReadChunkHeader(ReadOnlySpan<byte> data, ref int offset, out ReadOnlySpan<byte> type, out int length)
    {
        if (offset + 8 > data.Length)
        {
            throw new InvalidDataException("Unexpected end of MIDI chunk header.");
        }

        type = data.Slice(offset, 4);
        offset += 4;
        length = (int)ReadUInt32BE(data, ref offset);
    }

    private static int ReadVariableLength(ReadOnlySpan<byte> data, ref int position)
    {
        var value = 0;
        for (var i = 0; i < 4; i++)
        {
            if (position >= data.Length)
            {
                throw new InvalidDataException("Unexpected end of variable-length value.");
            }

            var b = data[position++];
            value = (value << 7) | (b & 0x7F);
            if ((b & 0x80) == 0)
            {
                return value;
            }
        }

        throw new InvalidDataException("Invalid variable-length value.");
    }

    private static uint PackShort(byte status, byte data1, byte data2)
    {
        return (uint)(status | (data1 << 8) | (data2 << 16));
    }

    private static string DecodeText(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length == 0)
        {
            return string.Empty;
        }

        var utf8 = Encoding.UTF8.GetString(bytes).Trim();
        if (!utf8.Contains('\uFFFD'))
        {
            return utf8;
        }

        return Encoding.Latin1.GetString(bytes).Trim();
    }

    private static byte[]? NormalizeSysEx(int status, byte[] payload)
    {
        if (payload.Length == 0 && status == 0xF7)
        {
            return null;
        }

        var output = new List<byte>(payload.Length + 2);
        if (status == 0xF0)
        {
            output.Add(0xF0);
            output.AddRange(payload);
            if (output.Count == 1 || output[^1] != 0xF7)
            {
                output.Add(0xF7);
            }
            return output.ToArray();
        }

        if (payload[0] != 0xF0)
        {
            output.Add(0xF0);
        }
        output.AddRange(payload);
        if (output[^1] != 0xF7)
        {
            output.Add(0xF7);
        }
        return output.ToArray();
    }

    private static ushort ReadUInt16BE(ReadOnlySpan<byte> data, ref int offset)
    {
        if (offset + 2 > data.Length)
        {
            throw new InvalidDataException("Unexpected end of file.");
        }

        var value = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset, 2));
        offset += 2;
        return value;
    }

    private static uint ReadUInt32BE(ReadOnlySpan<byte> data, ref int offset)
    {
        if (offset + 4 > data.Length)
        {
            throw new InvalidDataException("Unexpected end of file.");
        }

        var value = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(offset, 4));
        offset += 4;
        return value;
    }

    private static void EnsureLength(ReadOnlySpan<byte> trackData, int position, int length, int trackIndex)
    {
        if (position + length > trackData.Length)
        {
            throw new InvalidDataException($"Track {trackIndex + 1}: unexpected end of event data.");
        }
    }
}
