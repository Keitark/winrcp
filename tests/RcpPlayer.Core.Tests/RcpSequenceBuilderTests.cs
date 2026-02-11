using RcpPlayer.Core.Model;
using RcpPlayer.Core.Playback;

namespace RcpPlayer.Core.Tests;

public sealed class RcpSequenceBuilderTests
{
    [Fact]
    public void Build_FdEvent_AdvancesTimelineWithoutUnsupportedWarning()
    {
        var song = new RcpSong
        {
            Format = RcpFormat.RcpV2,
            Title = "test",
            Comment = string.Empty,
            TimeBase = 48,
            TempoBpm = 120,
            BeatNumerator = 4,
            BeatDenominator = 4,
            Cm6FileName = null,
            GsdAFileName = null,
            GsdBFileName = null,
            UserExclusives = [],
            Tracks =
            [
                new RcpTrack
                {
                    TrackId = 1,
                    Name = "track1",
                    DefaultChannel = 0,
                    IsMuted = false,
                    Events =
                    [
                        new RcpEvent { Index = 0, CommandOrNote = 60, DelayTicks = 0, Param1 = 1, Param2 = 100, RawLength = 4 },
                        new RcpEvent { Index = 1, CommandOrNote = 0xFD, DelayTicks = 1, Param1 = 0, Param2 = 0, RawLength = 4 },
                        new RcpEvent { Index = 2, CommandOrNote = 62, DelayTicks = 0, Param1 = 1, Param2 = 100, RawLength = 4 },
                        new RcpEvent { Index = 3, CommandOrNote = 0xFE, DelayTicks = 0, Param1 = 0, Param2 = 0, RawLength = 4 }
                    ]
                }
            ]
        };

        var builder = new RcpSequenceBuilder();
        var plan = builder.Build(song);

        var noteOnTicks = plan.MidiEvents
            .Where(e => e.Packet.Kind == MidiMessageKind.Short && (((byte)e.Packet.ShortMessage) & 0xF0) == 0x90)
            .Select(e => e.Tick)
            .OrderBy(t => t)
            .ToList();

        Assert.Equal([0L, 1L], noteOnTicks);
        Assert.DoesNotContain(plan.BuildDiagnostics.UnsupportedCommands, c => c.Command == 0xFD);
    }

    [Fact]
    public void Build_LoopExpansionLimit_DoesNotThrowAndReportsTrack()
    {
        var song = CreateSong(
        [
            new RcpEvent { Index = 0, CommandOrNote = 0xF9, DelayTicks = 0, Param1 = 0, Param2 = 0, RawLength = 4 },
            new RcpEvent { Index = 1, CommandOrNote = 60, DelayTicks = 0, Param1 = 1, Param2 = 100, RawLength = 4 },
            new RcpEvent { Index = 2, CommandOrNote = 0xF8, DelayTicks = 255, Param1 = 0, Param2 = 0, RawLength = 4 },
            new RcpEvent { Index = 3, CommandOrNote = 0xFE, DelayTicks = 0, Param1 = 0, Param2 = 0, RawLength = 4 }
        ]);

        var builder = new RcpSequenceBuilder();
        var plan = builder.Build(song, new RcpSequenceBuilderOptions
        {
            MaxExpandedEventsPerTrack = 100
        });

        Assert.Contains(1, plan.BuildDiagnostics.LoopExpansionLimitTracks);
        Assert.NotEmpty(plan.MidiEvents);
    }

    [Fact]
    public void Build_BankProgramLsbCommand_E1_EmitsCc32AndProgram()
    {
        var song = CreateSong(
        [
            new RcpEvent { Index = 0, CommandOrNote = 0xE1, DelayTicks = 0, Param1 = 5, Param2 = 7, RawLength = 4 },
            new RcpEvent { Index = 1, CommandOrNote = 0xFE, DelayTicks = 0, Param1 = 0, Param2 = 0, RawLength = 4 }
        ]);

        var builder = new RcpSequenceBuilder();
        var plan = builder.Build(song);

        Assert.Contains(plan.MidiEvents, e => e.Packet.Kind == MidiMessageKind.Short &&
                                              ((byte)e.Packet.ShortMessage) == 0xB0 &&
                                              ((e.Packet.ShortMessage >> 8) & 0x7F) == 32 &&
                                              ((e.Packet.ShortMessage >> 16) & 0x7F) == 7);
        Assert.Contains(plan.MidiEvents, e => e.Packet.Kind == MidiMessageKind.Short &&
                                              ((byte)e.Packet.ShortMessage) == 0xC0 &&
                                              ((e.Packet.ShortMessage >> 8) & 0x7F) == 5);
        Assert.DoesNotContain(plan.BuildDiagnostics.UnsupportedCommands, c => c.Command == 0xE1);
    }

    [Fact]
    public void Build_SameMeasureCommand_Fc_RepeatsTargetMeasure()
    {
        var song = CreateSong(
        [
            new RcpEvent { Index = 0, CommandOrNote = 60, DelayTicks = 0, Param1 = 1, Param2 = 100, RawLength = 4 },
            new RcpEvent { Index = 1, CommandOrNote = 0xFD, DelayTicks = 1, Param1 = 0, Param2 = 0, RawLength = 4 },
            new RcpEvent { Index = 2, CommandOrNote = 0xFC, DelayTicks = 0, Param1 = 0, Param2 = 0, RawLength = 4 },
            new RcpEvent { Index = 3, CommandOrNote = 0xFE, DelayTicks = 0, Param1 = 0, Param2 = 0, RawLength = 4 }
        ]);

        var builder = new RcpSequenceBuilder();
        var plan = builder.Build(song);

        var noteOnTicks = plan.MidiEvents
            .Where(e => e.Packet.Kind == MidiMessageKind.Short && (((byte)e.Packet.ShortMessage) & 0xF0) == 0x90)
            .Select(e => e.Tick)
            .OrderBy(t => t)
            .ToList();
        var noteOffTicks = plan.MidiEvents
            .Where(e => e.Packet.Kind == MidiMessageKind.Short && (((byte)e.Packet.ShortMessage) & 0xF0) == 0x80)
            .Select(e => e.Tick)
            .OrderBy(t => t)
            .ToList();

        Assert.Equal([0L], noteOnTicks);
        Assert.Equal([2L], noteOffTicks);
        Assert.DoesNotContain(plan.BuildDiagnostics.UnsupportedCommands, c => c.Command == 0xFC);
    }

    [Fact]
    public void Build_ExtAndCommentCommands_AreRecognizedWithoutUnsupportedWarning()
    {
        var song = CreateSong(
        [
            new RcpEvent { Index = 0, CommandOrNote = 0x99, DelayTicks = 2, Param1 = 0, Param2 = 0, RawLength = 4 },
            new RcpEvent { Index = 1, CommandOrNote = 0xF7, DelayTicks = 0, Param1 = 65, Param2 = 66, RawLength = 4 },
            new RcpEvent { Index = 2, CommandOrNote = 0xF6, DelayTicks = 0, Param1 = 67, Param2 = 68, RawLength = 4 },
            new RcpEvent { Index = 3, CommandOrNote = 0xF7, DelayTicks = 0, Param1 = 69, Param2 = 70, RawLength = 4 },
            new RcpEvent { Index = 4, CommandOrNote = 0xFE, DelayTicks = 0, Param1 = 0, Param2 = 0, RawLength = 4 }
        ]);

        var builder = new RcpSequenceBuilder();
        var plan = builder.Build(song);

        Assert.Empty(plan.MidiEvents);
        Assert.DoesNotContain(plan.BuildDiagnostics.UnsupportedCommands, c => c.Command == 0x99);
        Assert.DoesNotContain(plan.BuildDiagnostics.UnsupportedCommands, c => c.Command == 0xF6);
        Assert.DoesNotContain(plan.BuildDiagnostics.UnsupportedCommands, c => c.Command == 0xF7);
    }

    [Fact]
    public void Build_SameTickEvents_PreservesInsertionOrder()
    {
        var song = new RcpSong
        {
            Format = RcpFormat.RcpV2,
            Title = "test",
            Comment = string.Empty,
            TimeBase = 48,
            TempoBpm = 120,
            BeatNumerator = 4,
            BeatDenominator = 4,
            Cm6FileName = null,
            GsdAFileName = null,
            GsdBFileName = null,
            UserExclusives = [],
            Tracks =
            [
                // Track 2 first so insertion order would otherwise put NoteOn before NoteOff at tick 10.
                new RcpTrack
                {
                    TrackId = 2,
                    Name = "track2",
                    DefaultChannel = 0,
                    IsMuted = false,
                    Events =
                    [
                        new RcpEvent { Index = 0, CommandOrNote = 0xE5, DelayTicks = 10, Param1 = 0, Param2 = 0, RawLength = 4 },
                        new RcpEvent { Index = 1, CommandOrNote = 60, DelayTicks = 0, Param1 = 10, Param2 = 100, RawLength = 4 },
                        new RcpEvent { Index = 2, CommandOrNote = 0xFE, DelayTicks = 0, Param1 = 0, Param2 = 0, RawLength = 4 }
                    ]
                },
                new RcpTrack
                {
                    TrackId = 1,
                    Name = "track1",
                    DefaultChannel = 0,
                    IsMuted = false,
                    Events =
                    [
                        new RcpEvent { Index = 0, CommandOrNote = 60, DelayTicks = 10, Param1 = 10, Param2 = 100, RawLength = 4 },
                        new RcpEvent { Index = 1, CommandOrNote = 0xFE, DelayTicks = 0, Param1 = 0, Param2 = 0, RawLength = 4 }
                    ]
                }
            ]
        };

        var builder = new RcpSequenceBuilder();
        var plan = builder.Build(song);

        var sameTick = plan.MidiEvents
            .Where(e => e.Tick == 10 && e.Packet.Kind == MidiMessageKind.Short)
            .Select(e => (byte)(e.Packet.ShortMessage & 0xF0))
            .ToList();

        Assert.Contains((byte)0x80, sameTick);
        Assert.Contains((byte)0x90, sameTick);
        Assert.True(sameTick.IndexOf(0x90) < sameTick.IndexOf(0x80), "Same-tick MIDI events should preserve insertion order.");
    }

    [Fact]
    public void Build_TempoModifierE7_NoGradation_UsesBaseTempoRatio()
    {
        var song = CreateSong(
        [
            new RcpEvent { Index = 0, CommandOrNote = 0xE7, DelayTicks = 0, Param1 = 0x20, Param2 = 0, RawLength = 4 },
            new RcpEvent { Index = 1, CommandOrNote = 0xFE, DelayTicks = 0, Param1 = 0, Param2 = 0, RawLength = 4 }
        ]);

        var builder = new RcpSequenceBuilder();
        var plan = builder.Build(song);

        var tempo = Assert.Single(plan.TempoEvents);
        Assert.Equal(0L, tempo.Tick);
        Assert.Equal(60.0, tempo.Bpm, 3);
    }

    [Fact]
    public void Build_TempoModifierE7_WithGradation_InterpolatesTempo()
    {
        var song = CreateSong(
        [
            new RcpEvent { Index = 0, CommandOrNote = 0xE7, DelayTicks = 0, Param1 = 0x80, Param2 = 4, RawLength = 4 },
            new RcpEvent { Index = 1, CommandOrNote = 0xFE, DelayTicks = 0, Param1 = 0, Param2 = 0, RawLength = 4 }
        ]);

        var builder = new RcpSequenceBuilder();
        var plan = builder.Build(song);

        Assert.Equal(4, plan.TempoEvents.Count);
        Assert.Equal(1L, plan.TempoEvents[0].Tick);
        Assert.Equal(2L, plan.TempoEvents[1].Tick);
        Assert.Equal(3L, plan.TempoEvents[2].Tick);
        Assert.Equal(4L, plan.TempoEvents[3].Tick);
        Assert.Equal(150.0, plan.TempoEvents[0].Bpm, 3);
        Assert.Equal(180.0, plan.TempoEvents[1].Bpm, 3);
        Assert.Equal(210.0, plan.TempoEvents[2].Bpm, 3);
        Assert.Equal(240.0, plan.TempoEvents[3].Bpm, 3);
    }

    [Fact]
    public void Build_RepeatedActiveSameNote_ExtendsDurationWithoutDuplicateNoteOn()
    {
        var song = CreateSong(
        [
            new RcpEvent { Index = 0, CommandOrNote = 60, DelayTicks = 2, Param1 = 6, Param2 = 100, RawLength = 4 },
            new RcpEvent { Index = 1, CommandOrNote = 60, DelayTicks = 1, Param1 = 4, Param2 = 100, RawLength = 4 },
            new RcpEvent { Index = 2, CommandOrNote = 0xFE, DelayTicks = 0, Param1 = 0, Param2 = 0, RawLength = 4 }
        ]);

        var builder = new RcpSequenceBuilder();
        var plan = builder.Build(song);

        var noteOn = plan.MidiEvents.Where(e =>
            e.Packet.Kind == MidiMessageKind.Short &&
            ((byte)e.Packet.ShortMessage & 0xF0) == 0x90 &&
            (((e.Packet.ShortMessage >> 16) & 0x7F) > 0)).ToList();
        var noteOff = plan.MidiEvents.Where(e =>
            e.Packet.Kind == MidiMessageKind.Short &&
            ((byte)e.Packet.ShortMessage & 0xF0) == 0x80).ToList();

        Assert.Single(noteOn);
        Assert.Single(noteOff);
        Assert.Equal(0L, noteOn[0].Tick);
        Assert.Equal(6L, noteOff[0].Tick);
    }

    [Fact]
    public void Build_RepeatedAtBoundaryTick_ExtendsWithoutRetrigger()
    {
        var song = CreateSong(
        [
            new RcpEvent { Index = 0, CommandOrNote = 60, DelayTicks = 5, Param1 = 5, Param2 = 100, RawLength = 4 },
            new RcpEvent { Index = 1, CommandOrNote = 60, DelayTicks = 0, Param1 = 7, Param2 = 100, RawLength = 4 },
            new RcpEvent { Index = 2, CommandOrNote = 0xFE, DelayTicks = 0, Param1 = 0, Param2 = 0, RawLength = 4 }
        ]);

        var builder = new RcpSequenceBuilder();
        var plan = builder.Build(song);

        var noteOn = plan.MidiEvents.Where(e =>
            e.Packet.Kind == MidiMessageKind.Short &&
            ((byte)e.Packet.ShortMessage & 0xF0) == 0x90 &&
            (((e.Packet.ShortMessage >> 16) & 0x7F) > 0)).ToList();
        var noteOff = plan.MidiEvents.Where(e =>
            e.Packet.Kind == MidiMessageKind.Short &&
            ((byte)e.Packet.ShortMessage & 0xF0) == 0x80).ToList();

        Assert.Single(noteOn);
        Assert.Single(noteOff);
        Assert.Equal(0L, noteOn[0].Tick);
        Assert.Equal(12L, noteOff[0].Tick);
    }

    [Fact]
    public void Build_ZeroDurationNote_DoesNotEmitNoteOnOrNoteOff()
    {
        var song = CreateSong(
        [
            new RcpEvent { Index = 0, CommandOrNote = 60, DelayTicks = 3, Param1 = 0, Param2 = 100, RawLength = 4 },
            new RcpEvent { Index = 1, CommandOrNote = 0xFE, DelayTicks = 0, Param1 = 0, Param2 = 0, RawLength = 4 }
        ]);

        var builder = new RcpSequenceBuilder();
        var plan = builder.Build(song);

        Assert.Empty(plan.MidiEvents.Where(e =>
            e.Packet.Kind == MidiMessageKind.Short &&
            (((byte)e.Packet.ShortMessage & 0xF0) is 0x80 or 0x90)));
    }

    private static RcpSong CreateSong(IReadOnlyList<RcpEvent> events)
    {
        return new RcpSong
        {
            Format = RcpFormat.RcpV2,
            Title = "test",
            Comment = string.Empty,
            TimeBase = 48,
            TempoBpm = 120,
            BeatNumerator = 4,
            BeatDenominator = 4,
            Cm6FileName = null,
            GsdAFileName = null,
            GsdBFileName = null,
            UserExclusives = [],
            Tracks =
            [
                new RcpTrack
                {
                    TrackId = 1,
                    Name = "track1",
                    DefaultChannel = 0,
                    IsMuted = false,
                    Events = events
                }
            ]
        };
    }
}
