using System.Text;
using RcpPlayer.Core.Parsing;
using RcpPlayer.Core.Playback;

namespace RcpPlayer.Core.Tests;

public sealed class RcpParserTests
{
    [Fact]
    public void Parse_MinimalRcp_ParsesHeaderTrackAndEvents()
    {
        var data = BuildMinimalRcp();
        var parser = new RcpParser();

        var song = parser.Parse(data);

        Assert.Equal(48, song.TimeBase);
        Assert.Equal(120, song.TempoBpm);
        Assert.Single(song.Tracks);
        Assert.True(song.Tracks[0].Events.Count >= 2);
        Assert.Equal((byte)60, song.Tracks[0].Events[0].CommandOrNote);
    }

    [Fact]
    public void Build_MinimalRcp_ProducesNoteOnAndNoteOff()
    {
        var parser = new RcpParser();
        var song = parser.Parse(BuildMinimalRcp());
        var builder = new RcpSequenceBuilder();

        var plan = builder.Build(song);

        Assert.NotEmpty(plan.MidiEvents);
        Assert.Contains(plan.MidiEvents, e =>
            (e.Packet.ShortMessage & 0xF0) == 0x90 &&
            ((e.Packet.ShortMessage >> 16) & 0x7F) > 0);
        Assert.Contains(plan.MidiEvents, e =>
            (e.Packet.ShortMessage & 0xF0) == 0x90 &&
            ((e.Packet.ShortMessage >> 16) & 0x7F) == 0);
    }

    [Fact]
    public void Parse_RcpV2_ExtendedTitle_AppendsCompatibility24Bytes()
    {
        var parser = new RcpParser();
        const string title40 = "TITLE / (C)KONAMI / [SC-55|CM-3";
        const string ext24 = "00|CM-500(C)] /By TAKA-P";
        var song = parser.Parse(BuildMinimalRcpWithTitleExtension(title40, ext24));

        Assert.Equal(title40 + ext24, song.Title);
    }

    [Fact]
    public void Parse_RcpV2_ExtendedTitle_CanBeDisabled()
    {
        var parser = new RcpParser();
        const string title40 = "TITLE / (C)KONAMI / [SC-55|CM-3";
        const string ext24 = "00|CM-500(C)] /By TAKA-P";
        var options = new RcpParserOptions
        {
            EnableExtendedTitle24 = false
        };

        var song = parser.Parse(BuildMinimalRcpWithTitleExtension(title40, ext24), options);

        Assert.Equal(title40, song.Title);
    }

    [Fact]
    public void Parse_RcpV2_ExtendedTitle_IgnoresNonTextBytes()
    {
        var parser = new RcpParser();
        const string title40 = "TITLE / (C)KONAMI / [SC-55|CM-3";
        var extensionBytes = new byte[0x18];
        extensionBytes[0] = 0xFF;
        extensionBytes[1] = 0x01;
        extensionBytes[2] = 0x10;

        var song = parser.Parse(BuildMinimalRcpWithTitleExtensionBytes(title40, extensionBytes));

        Assert.Equal(title40, song.Title);
    }

    [Fact]
    public void Parse_RcpV2_ExtendedTitle_ReconstructsSplitShiftJisCharacterAcrossBoundary()
    {
        var parser = new RcpParser();
        var sjis = Encoding.GetEncoding(932);
        var titleRaw = new byte[0x28];
        var extRaw = new byte[0x18];
        Array.Fill(titleRaw, (byte)0x20);

        // Build title bytes ending with a dangling Shift-JIS lead byte.
        var prefix = sjis.GetBytes("HAPPY WAKE UP! [観月 あ");
        Array.Copy(prefix, 0, titleRaw, 0, Math.Min(prefix.Length, titleRaw.Length - 1));
        titleRaw[^1] = 0x82; // dangling lead byte

        // First extension byte closes the dangling lead (0x82,0xE8 = ら).
        extRaw[0] = 0xE8;
        var tail = sjis.GetBytes("さ]  By けけほ");
        Array.Copy(tail, 0, extRaw, 1, Math.Min(tail.Length, extRaw.Length - 1));

        var song = parser.Parse(BuildMinimalRcpWithRawTitleExtensionBytes(titleRaw, extRaw));

        var combined = new byte[titleRaw.Length + extRaw.Length];
        Array.Copy(titleRaw, 0, combined, 0, titleRaw.Length);
        Array.Copy(extRaw, 0, combined, titleRaw.Length, extRaw.Length);
        var expected = sjis.GetString(combined);
        var terminator = expected.IndexOf('\0');
        if (terminator >= 0)
        {
            expected = expected[..terminator];
        }

        expected = expected.Trim();
        Assert.Equal(expected, song.Title);
        Assert.DoesNotContain("閧ｳ", song.Title);
    }

    private static byte[] BuildMinimalRcp()
    {
        const int headerSize = 0x586;
        const int trackHeaderSize = 0x2C;
        const int eventCount = 3;
        const int eventSize = 4;
        var trackLength = trackHeaderSize + (eventCount * eventSize);
        var buffer = new byte[headerSize + trackLength];

        var signature = Encoding.ASCII.GetBytes("RCM-PC98V2.0(C)COME ON MUSIC\r\n\0\0");
        signature.CopyTo(buffer, 0);
        Encoding.ASCII.GetBytes("Test Song").CopyTo(buffer, 0x20);

        buffer[0x1C0] = 48; // Timebase low
        buffer[0x1E7] = 0;  // Timebase high
        buffer[0x1C1] = 120;
        buffer[0x1C2] = 4;
        buffer[0x1C3] = 4;
        buffer[0x1E6] = 1; // Track count

        var trackOffset = headerSize;
        buffer[trackOffset] = (byte)(trackLength & 0xFF);
        buffer[trackOffset + 1] = (byte)((trackLength >> 8) & 0xFF);
        buffer[trackOffset + 2] = 1;  // Track Id
        buffer[trackOffset + 4] = 1;  // Channel (1-based)
        Encoding.ASCII.GetBytes("TRK1").CopyTo(buffer, trackOffset + 8);

        var eventOffset = trackOffset + trackHeaderSize;

        // Note C4, delay 24, gate 12, velocity 100
        buffer[eventOffset] = 60;
        buffer[eventOffset + 1] = 24;
        buffer[eventOffset + 2] = 12;
        buffer[eventOffset + 3] = 100;

        // Program change, delay 0, program 1
        buffer[eventOffset + 4] = 0xEC;
        buffer[eventOffset + 5] = 0;
        buffer[eventOffset + 6] = 1;
        buffer[eventOffset + 7] = 0;

        // End of track
        buffer[eventOffset + 8] = 0xFE;
        buffer[eventOffset + 9] = 0;
        buffer[eventOffset + 10] = 0;
        buffer[eventOffset + 11] = 0;

        return buffer;
    }

    private static byte[] BuildMinimalRcpWithTitleExtension(string title40, string ext24)
    {
        var extBytes = Encoding.GetEncoding(932).GetBytes(ext24);
        return BuildMinimalRcpWithTitleExtensionBytes(title40, extBytes);
    }

    private static byte[] BuildMinimalRcpWithTitleExtensionBytes(string title40, byte[] extBytes)
    {
        var data = BuildMinimalRcp();
        Array.Clear(data, 0x20, 0x28);
        Array.Clear(data, 0x48, 0x18);

        var titleRaw = Encoding.GetEncoding(932).GetBytes(title40);
        Array.Copy(titleRaw, 0, data, 0x20, Math.Min(titleRaw.Length, 0x28));
        Array.Copy(extBytes, 0, data, 0x48, Math.Min(extBytes.Length, 0x18));
        return data;
    }

    private static byte[] BuildMinimalRcpWithRawTitleExtensionBytes(byte[] titleRaw, byte[] extBytes)
    {
        var data = BuildMinimalRcp();
        Array.Clear(data, 0x20, 0x28);
        Array.Clear(data, 0x48, 0x18);
        Array.Copy(titleRaw, 0, data, 0x20, Math.Min(titleRaw.Length, 0x28));
        Array.Copy(extBytes, 0, data, 0x48, Math.Min(extBytes.Length, 0x18));
        return data;
    }
}
