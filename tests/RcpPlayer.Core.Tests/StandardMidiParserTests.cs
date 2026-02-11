using System.Buffers.Binary;
using RcpPlayer.Core.Parsing;
using RcpPlayer.Core.Playback;

namespace RcpPlayer.Core.Tests;

public sealed class StandardMidiParserTests
{
    [Fact]
    public void Parse_Type0File_BuildsPlaybackPlanAndMetadata()
    {
        var parser = new StandardMidiParser();
        var data = BuildType0Midi([
            0x00, 0xFF, 0x03, 0x05, (byte)'P', (byte)'i', (byte)'a', (byte)'n', (byte)'o',
            0x00, 0xFF, 0x51, 0x03, 0x07, 0xA1, 0x20,
            0x00, 0xFF, 0x58, 0x04, 0x04, 0x02, 0x18, 0x08,
            0x00, 0xC0, 0x00,
            0x00, 0x90, 0x3C, 0x64,
            0x83, 0x60, 0x80, 0x3C, 0x00,
            0x00, 0xFF, 0x2F, 0x00
        ]);

        var song = parser.Parse(data);

        Assert.Equal("Piano", song.Title);
        Assert.Equal(480, song.TimeBase);
        Assert.Equal(120, song.TempoBpm);
        Assert.Equal(4, song.BeatNumerator);
        Assert.Equal(4, song.BeatDenominator);
        Assert.Single(song.Tracks);
        Assert.Equal("Piano", song.Tracks[0].Name);
        Assert.Equal(0, song.Tracks[0].DefaultChannel);

        Assert.Equal(3, song.PlaybackPlan.MidiEvents.Count);
        Assert.Equal(480, song.PlaybackPlan.MidiEvents[^1].Tick);
        Assert.Single(song.PlaybackPlan.TempoEvents);
    }

    [Fact]
    public void Parse_RunningStatus_NoteMessagesAreDecoded()
    {
        var parser = new StandardMidiParser();
        var data = BuildType0Midi([
            0x00, 0x90, 0x3C, 0x40, // Note on C4
            0x78, 0x40, 0x40,       // Running status note on E4 after 120 ticks
            0x78, 0x80, 0x3C, 0x00, // Note off C4
            0x00, 0x80, 0x40, 0x00, // Note off E4
            0x00, 0xFF, 0x2F, 0x00
        ]);

        var song = parser.Parse(data);
        var events = song.PlaybackPlan.MidiEvents;

        Assert.Equal(4, events.Count);
        Assert.Equal(0L, events[0].Tick);
        Assert.Equal(120L, events[1].Tick);
        Assert.Equal(240L, events[2].Tick);
        Assert.Equal(240L, events[3].Tick);

        var secondStatus = (byte)(events[1].Packet.ShortMessage & 0xFF);
        var secondNote = (byte)((events[1].Packet.ShortMessage >> 8) & 0x7F);
        Assert.Equal(0x90, secondStatus);
        Assert.Equal(0x40, secondNote);
    }

    [Fact]
    public void Parse_Type1SameTickCrossTrack_PreservesTrackOrderAtSameTick()
    {
        var parser = new StandardMidiParser();
        var track1 = new byte[]
        {
            0x83, 0x60, 0x90, 0x3C, 0x64, // tick 480 note on
            0x00, 0xFF, 0x2F, 0x00
        };
        var track2 = new byte[]
        {
            0x83, 0x60, 0x80, 0x3C, 0x00, // tick 480 note off
            0x00, 0xFF, 0x2F, 0x00
        };
        var data = BuildType1Midi(track1, track2);

        var song = parser.Parse(data);
        var at480 = song.PlaybackPlan.MidiEvents
            .Where(e => e.Tick == 480 && e.Packet.Kind == MidiMessageKind.Short)
            .Select(e => (byte)(e.Packet.ShortMessage & 0xF0))
            .ToList();

        Assert.Contains((byte)0x80, at480);
        Assert.Contains((byte)0x90, at480);
        Assert.True(at480.IndexOf(0x90) < at480.IndexOf(0x80), "Same-tick MIDI events should preserve source order.");
    }

    private static byte[] BuildType0Midi(byte[] trackEvents)
    {
        var header = new byte[14];
        header[0] = (byte)'M';
        header[1] = (byte)'T';
        header[2] = (byte)'h';
        header[3] = (byte)'d';
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), 6);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(8), 0);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(10), 1);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(12), 480);

        var trackChunk = new byte[8 + trackEvents.Length];
        trackChunk[0] = (byte)'M';
        trackChunk[1] = (byte)'T';
        trackChunk[2] = (byte)'r';
        trackChunk[3] = (byte)'k';
        BinaryPrimitives.WriteUInt32BigEndian(trackChunk.AsSpan(4), (uint)trackEvents.Length);
        trackEvents.CopyTo(trackChunk.AsSpan(8));

        var output = new byte[header.Length + trackChunk.Length];
        header.CopyTo(output.AsSpan(0));
        trackChunk.CopyTo(output.AsSpan(header.Length));
        return output;
    }

    private static byte[] BuildType1Midi(byte[] trackEvents1, byte[] trackEvents2)
    {
        var header = new byte[14];
        header[0] = (byte)'M';
        header[1] = (byte)'T';
        header[2] = (byte)'h';
        header[3] = (byte)'d';
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), 6);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(10), 2);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(12), 480);

        var trackChunk1 = new byte[8 + trackEvents1.Length];
        trackChunk1[0] = (byte)'M';
        trackChunk1[1] = (byte)'T';
        trackChunk1[2] = (byte)'r';
        trackChunk1[3] = (byte)'k';
        BinaryPrimitives.WriteUInt32BigEndian(trackChunk1.AsSpan(4), (uint)trackEvents1.Length);
        trackEvents1.CopyTo(trackChunk1.AsSpan(8));

        var trackChunk2 = new byte[8 + trackEvents2.Length];
        trackChunk2[0] = (byte)'M';
        trackChunk2[1] = (byte)'T';
        trackChunk2[2] = (byte)'r';
        trackChunk2[3] = (byte)'k';
        BinaryPrimitives.WriteUInt32BigEndian(trackChunk2.AsSpan(4), (uint)trackEvents2.Length);
        trackEvents2.CopyTo(trackChunk2.AsSpan(8));

        var output = new byte[header.Length + trackChunk1.Length + trackChunk2.Length];
        var offset = 0;
        header.CopyTo(output.AsSpan(offset));
        offset += header.Length;
        trackChunk1.CopyTo(output.AsSpan(offset));
        offset += trackChunk1.Length;
        trackChunk2.CopyTo(output.AsSpan(offset));
        return output;
    }
}
