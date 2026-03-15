using RcpPlayer.Core.Model;
using RcpPlayer.Core.Playback;

namespace RcpPlayer.Core.Tests;

public sealed class RcpSequenceBuilderTests
{
    [Fact]
    public void Build_FdEvent_DoesNotAdvanceTimelineWithoutUnsupportedWarning()
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
            .Where(e => e.Packet.Kind == MidiMessageKind.Short &&
                        (((byte)e.Packet.ShortMessage) & 0xF0) == 0x90 &&
                        (((e.Packet.ShortMessage >> 16) & 0x7F) > 0))
            .Select(e => e.Tick)
            .OrderBy(t => t)
            .ToList();

        Assert.Equal([0L, 0L], noteOnTicks);
        Assert.DoesNotContain(plan.BuildDiagnostics.UnsupportedCommands, c => c.Command == 0xFD);
    }

    [Fact]
    public void Build_LoopExpansionLimit_DoesNotThrowAndReportsTrack()
    {
        var song = CreateSong(
        [
            new RcpEvent { Index = 0, CommandOrNote = 0xF9, DelayTicks = 0, Param1 = 0, Param2 = 0, RawLength = 4 },
            new RcpEvent { Index = 1, CommandOrNote = 60, DelayTicks = 0, Param1 = 1, Param2 = 100, RawLength = 4 },
            new RcpEvent { Index = 2, CommandOrNote = 0xF8, DelayTicks = 126, Param1 = 0, Param2 = 0, RawLength = 4 },
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
    public void Build_F8_255_IsTreatedAsInfiniteAndStaysBounded()
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
                    Name = "inf255",
                    DefaultChannel = 0,
                    IsMuted = false,
                    Events =
                    [
                        new RcpEvent { Index = 0, CommandOrNote = 0xF9, DelayTicks = 0, Param1 = 0, Param2 = 0, RawLength = 4 },
                        new RcpEvent { Index = 1, CommandOrNote = 60, DelayTicks = 1, Param1 = 1, Param2 = 100, RawLength = 4 },
                        new RcpEvent { Index = 2, CommandOrNote = 61, DelayTicks = 1, Param1 = 1, Param2 = 100, RawLength = 4 },
                        new RcpEvent { Index = 3, CommandOrNote = 0xF8, DelayTicks = 255, Param1 = 0, Param2 = 0, RawLength = 4 },
                        new RcpEvent { Index = 4, CommandOrNote = 0xFE, DelayTicks = 0, Param1 = 0, Param2 = 0, RawLength = 4 }
                    ]
                },
                new RcpTrack
                {
                    TrackId = 2,
                    Name = "finite",
                    DefaultChannel = 1,
                    IsMuted = false,
                    Events =
                    [
                        new RcpEvent { Index = 0, CommandOrNote = 65, DelayTicks = 1, Param1 = 1, Param2 = 96, RawLength = 4 },
                        new RcpEvent { Index = 1, CommandOrNote = 67, DelayTicks = 1, Param1 = 1, Param2 = 96, RawLength = 4 },
                        new RcpEvent { Index = 2, CommandOrNote = 0xFE, DelayTicks = 0, Param1 = 0, Param2 = 0, RawLength = 4 }
                    ]
                }
            ]
        };

        var builder = new RcpSequenceBuilder();
        var plan = builder.Build(song);

        Assert.NotEmpty(plan.MidiEvents);
        Assert.True(plan.MidiEvents.Max(e => e.Tick) <= 6, "F8=255 should use bounded infinite-loop behavior.");
    }

    [Fact]
    public void Build_InfiniteLoopTrack_StopsAfterAllActiveInfiniteTracksPlayedOnce()
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
                    Name = "inf",
                    DefaultChannel = 0,
                    IsMuted = false,
                    Events =
                    [
                        new RcpEvent { Index = 0, CommandOrNote = 0xF9, DelayTicks = 0, Param1 = 0, Param2 = 0, RawLength = 4 },
                        new RcpEvent { Index = 1, CommandOrNote = 60, DelayTicks = 1, Param1 = 1, Param2 = 100, RawLength = 4 },
                        new RcpEvent { Index = 2, CommandOrNote = 61, DelayTicks = 1, Param1 = 1, Param2 = 100, RawLength = 4 },
                        new RcpEvent { Index = 3, CommandOrNote = 0xF8, DelayTicks = 0, Param1 = 0, Param2 = 0, RawLength = 4 },
                        new RcpEvent { Index = 4, CommandOrNote = 0xFE, DelayTicks = 0, Param1 = 0, Param2 = 0, RawLength = 4 }
                    ]
                },
                new RcpTrack
                {
                    TrackId = 2,
                    Name = "finite",
                    DefaultChannel = 1,
                    IsMuted = false,
                    Events =
                    [
                        new RcpEvent { Index = 0, CommandOrNote = 0xF9, DelayTicks = 0, Param1 = 0, Param2 = 0, RawLength = 4 },
                        new RcpEvent { Index = 1, CommandOrNote = 64, DelayTicks = 1, Param1 = 1, Param2 = 100, RawLength = 4 },
                        new RcpEvent { Index = 2, CommandOrNote = 0xF8, DelayTicks = 2, Param1 = 0, Param2 = 0, RawLength = 4 },
                        new RcpEvent { Index = 3, CommandOrNote = 67, DelayTicks = 1, Param1 = 1, Param2 = 100, RawLength = 4 },
                        new RcpEvent { Index = 4, CommandOrNote = 0xFE, DelayTicks = 0, Param1 = 0, Param2 = 0, RawLength = 4 }
                    ]
                }
            ]
        };

        var builder = new RcpSequenceBuilder();
        var plan = builder.Build(song);

        var noteOns = plan.MidiEvents
            .Where(e => e.Packet.Kind == MidiMessageKind.Short && (((byte)e.Packet.ShortMessage) & 0xF0) == 0x90)
            .ToList();

        var infTrackNote60Count = noteOns.Count(e =>
            ((e.Packet.ShortMessage >> 8) & 0x7F) == 60 &&
            (((byte)e.Packet.ShortMessage) & 0x0F) == 0);

        Assert.True(infTrackNote60Count >= 1, "Infinite tracks should emit at least one full cycle.");
        Assert.True(plan.MidiEvents.Max(e => e.Tick) <= 6, "Offline rendering should remain bounded when infinite tracks are present.");
    }

    [Fact]
    public void Build_ControlOnlyInfiniteTrack_DoesNotBlockCompletion()
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
                    Name = "note",
                    DefaultChannel = 0,
                    IsMuted = false,
                    Events =
                    [
                        new RcpEvent { Index = 0, CommandOrNote = 60, DelayTicks = 1, Param1 = 1, Param2 = 100, RawLength = 4 },
                        new RcpEvent { Index = 1, CommandOrNote = 62, DelayTicks = 1, Param1 = 1, Param2 = 100, RawLength = 4 },
                        new RcpEvent { Index = 2, CommandOrNote = 0xFE, DelayTicks = 0, Param1 = 0, Param2 = 0, RawLength = 4 }
                    ]
                },
                new RcpTrack
                {
                    TrackId = 2,
                    Name = "ctrl-inf",
                    DefaultChannel = 1,
                    IsMuted = false,
                    Events =
                    [
                        new RcpEvent { Index = 0, CommandOrNote = 0xF9, DelayTicks = 0, Param1 = 0, Param2 = 0, RawLength = 4 },
                        new RcpEvent { Index = 1, CommandOrNote = 0xEB, DelayTicks = 0, Param1 = 7, Param2 = 100, RawLength = 4 },
                        new RcpEvent { Index = 2, CommandOrNote = 0xF8, DelayTicks = 0, Param1 = 0, Param2 = 0, RawLength = 4 },
                        new RcpEvent { Index = 3, CommandOrNote = 0xFE, DelayTicks = 0, Param1 = 0, Param2 = 0, RawLength = 4 }
                    ]
                }
            ]
        };

        var builder = new RcpSequenceBuilder();
        var plan = builder.Build(song);

        Assert.NotEmpty(plan.MidiEvents);
        Assert.True(plan.MidiEvents.Max(e => e.Tick) <= 3, "Control-only infinite track should not keep offline renderer running.");
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
            .Where(e => e.Packet.Kind == MidiMessageKind.Short &&
                        (((byte)e.Packet.ShortMessage) & 0xF0) == 0x90 &&
                        (((e.Packet.ShortMessage >> 16) & 0x7F) > 0))
            .Select(e => e.Tick)
            .OrderBy(t => t)
            .ToList();
        var noteOffTicks = plan.MidiEvents
            .Where(e => e.Packet.Kind == MidiMessageKind.Short &&
                        (((byte)e.Packet.ShortMessage) & 0xF0) == 0x90 &&
                        (((e.Packet.ShortMessage >> 16) & 0x7F) == 0))
            .Select(e => e.Tick)
            .OrderBy(t => t)
            .ToList();

        Assert.Equal([0L], noteOnTicks);
        Assert.Equal([1L], noteOffTicks);
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
            .Where(e => e.Tick == 10 && e.Packet.Kind == MidiMessageKind.Short && (((byte)e.Packet.ShortMessage) & 0xF0) == 0x90)
            .Select(e => (byte)((e.Packet.ShortMessage >> 16) & 0x7F))
            .ToList();

        Assert.Contains((byte)0, sameTick);
        Assert.Contains((byte)100, sameTick);
        Assert.True(sameTick.IndexOf(100) < sameTick.IndexOf(0), "Same-tick MIDI events should preserve insertion order.");
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

        var tempo = Assert.Single(plan.TempoEvents);
        Assert.Equal(0L, tempo.Tick);
        Assert.Equal(240.0, tempo.Bpm, 3);
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
            ((byte)e.Packet.ShortMessage & 0xF0) == 0x90 &&
            (((e.Packet.ShortMessage >> 16) & 0x7F) == 0)).ToList();

        Assert.Single(noteOn);
        Assert.Single(noteOff);
        Assert.Equal(0L, noteOn[0].Tick);
        Assert.Equal(6L, noteOff[0].Tick);
    }

    [Fact]
    public void Build_RepeatedAtBoundaryTick_CanExtendWithoutRetrigger()
    {
        var song = CreateSong(
        [
            new RcpEvent { Index = 0, CommandOrNote = 60, DelayTicks = 5, Param1 = 5, Param2 = 100, RawLength = 4 },
            new RcpEvent { Index = 1, CommandOrNote = 60, DelayTicks = 0, Param1 = 7, Param2 = 100, RawLength = 4 },
            new RcpEvent { Index = 2, CommandOrNote = 0xFE, DelayTicks = 0, Param1 = 0, Param2 = 0, RawLength = 4 }
        ]);

        var builder = new RcpSequenceBuilder();
        var plan = builder.Build(song, new RcpSequenceBuilderOptions
        {
            KeepBoundaryActiveSameNote = true
        });

        var noteOn = plan.MidiEvents.Where(e =>
            e.Packet.Kind == MidiMessageKind.Short &&
            ((byte)e.Packet.ShortMessage & 0xF0) == 0x90 &&
            (((e.Packet.ShortMessage >> 16) & 0x7F) > 0)).ToList();
        var noteOff = plan.MidiEvents.Where(e =>
            e.Packet.Kind == MidiMessageKind.Short &&
            ((byte)e.Packet.ShortMessage & 0xF0) == 0x90 &&
            (((e.Packet.ShortMessage >> 16) & 0x7F) == 0)).ToList();

        Assert.Single(noteOn);
        Assert.Single(noteOff);
        Assert.Equal(0L, noteOn[0].Tick);
        Assert.Equal(12L, noteOff[0].Tick);
    }

    [Fact]
    public void Build_RepeatedActiveSameNote_ReferenceMode_AllowsOverlap()
    {
        var song = CreateSong(
        [
            new RcpEvent { Index = 0, CommandOrNote = 60, DelayTicks = 2, Param1 = 6, Param2 = 100, RawLength = 4 },
            new RcpEvent { Index = 1, CommandOrNote = 60, DelayTicks = 1, Param1 = 4, Param2 = 100, RawLength = 4 },
            new RcpEvent { Index = 2, CommandOrNote = 0xFE, DelayTicks = 0, Param1 = 0, Param2 = 0, RawLength = 4 }
        ]);

        var builder = new RcpSequenceBuilder();
        var plan = builder.Build(song, new RcpSequenceBuilderOptions
        {
            MergeActiveSameNote = false
        });

        var noteOn = plan.MidiEvents.Where(e =>
            e.Packet.Kind == MidiMessageKind.Short &&
            ((byte)e.Packet.ShortMessage & 0xF0) == 0x90 &&
            (((e.Packet.ShortMessage >> 16) & 0x7F) > 0))
            .Select(e => e.Tick)
            .OrderBy(t => t)
            .ToList();
        var noteOff = plan.MidiEvents.Where(e =>
            e.Packet.Kind == MidiMessageKind.Short &&
            ((byte)e.Packet.ShortMessage & 0xF0) == 0x90 &&
            (((e.Packet.ShortMessage >> 16) & 0x7F) == 0))
            .Select(e => e.Tick)
            .OrderBy(t => t)
            .ToList();

        Assert.Equal([0L, 2L], noteOn);
        Assert.Equal([6L, 6L], noteOff);
    }

    [Fact]
    public void Build_ReferenceMode_EmitsEmptyUserExclusiveAsF0F7()
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
            UserExclusives =
            [
                new RcpUserExclusive { Name = "u0", DataWithoutLeadingF0 = [] }
            ],
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
                        new RcpEvent { Index = 0, CommandOrNote = 0x90, DelayTicks = 0, Param1 = 0, Param2 = 0, RawLength = 4 },
                        new RcpEvent { Index = 1, CommandOrNote = 0xFE, DelayTicks = 0, Param1 = 0, Param2 = 0, RawLength = 4 }
                    ]
                }
            ]
        };

        var builder = new RcpSequenceBuilder();
        var plan = builder.Build(song, new RcpSequenceBuilderOptions
        {
            EmitEmptySysEx = true
        });

        var syx = Assert.Single(plan.MidiEvents, e => e.Packet.Kind == MidiMessageKind.SysEx);
        Assert.Equal(0L, syx.Tick);
        Assert.Equal("F0-F7", BitConverter.ToString(Assert.IsType<byte[]>(syx.Packet.SysExData)));
    }

    [Fact]
    public void Build_DefaultsToX68BoundaryNoteOffBeforeControl()
    {
        var song = CreateSong(
        [
            new RcpEvent { Index = 0, CommandOrNote = 60, DelayTicks = 2, Param1 = 2, Param2 = 100, RawLength = 4 },
            new RcpEvent { Index = 1, CommandOrNote = 0xEB, DelayTicks = 0, Param1 = 1, Param2 = 51, RawLength = 4 },
            new RcpEvent { Index = 2, CommandOrNote = 0xFE, DelayTicks = 0, Param1 = 0, Param2 = 0, RawLength = 4 }
        ]);

        var builder = new RcpSequenceBuilder();
        var plan = builder.Build(song);

        var tick2 = plan.MidiEvents.Where(e => e.Tick == 2).ToList();
        Assert.True(tick2.Count >= 2);
        Assert.Equal(MidiMessageKind.Short, tick2[0].Packet.Kind);
        Assert.Equal(0, (int)((tick2[0].Packet.ShortMessage >> 16) & 0x7F)); // note off
        Assert.Equal(0xB0, (int)(((byte)tick2[1].Packet.ShortMessage) & 0xF0)); // control change
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

        Assert.DoesNotContain(plan.MidiEvents, e =>
            e.Packet.Kind == MidiMessageKind.Short &&
            (((byte)e.Packet.ShortMessage & 0xF0) == 0x90) &&
            (((e.Packet.ShortMessage >> 16) & 0x7F) > 0));
    }

    [Fact]
    public void Build_DefaultsToX68VelocityZeroNoteEventsDoNotShortenActiveNote()
    {
        var song = CreateSong(
        [
            new RcpEvent { Index = 0, CommandOrNote = 60, DelayTicks = 2, Param1 = 8, Param2 = 100, RawLength = 4 },
            new RcpEvent { Index = 1, CommandOrNote = 60, DelayTicks = 0, Param1 = 4, Param2 = 0, RawLength = 4 },
            new RcpEvent { Index = 2, CommandOrNote = 0xFE, DelayTicks = 0, Param1 = 0, Param2 = 0, RawLength = 4 }
        ]);

        var builder = new RcpSequenceBuilder();
        var plan = builder.Build(song);

        var shorts = plan.MidiEvents
            .Where(e => e.Packet.Kind == MidiMessageKind.Short)
            .Select(e => new
            {
                e.Tick,
                Message = e.Packet.ShortMessage
            })
            .ToList();

        Assert.Collection(
            shorts,
            e =>
            {
                Assert.Equal(0L, e.Tick);
                Assert.Equal((uint)0x00643C90, e.Message);
            },
            e =>
            {
                Assert.Equal(8L, e.Tick);
                Assert.Equal((uint)0x00003C90, e.Message);
            });
    }

    [Fact]
    public void Build_CanTreatVelocityZeroNoteAsImmediateNoteOff()
    {
        var song = CreateSong(
        [
            new RcpEvent { Index = 0, CommandOrNote = 60, DelayTicks = 2, Param1 = 8, Param2 = 100, RawLength = 4 },
            new RcpEvent { Index = 1, CommandOrNote = 60, DelayTicks = 0, Param1 = 4, Param2 = 0, RawLength = 4 },
            new RcpEvent { Index = 2, CommandOrNote = 0xFE, DelayTicks = 0, Param1 = 0, Param2 = 0, RawLength = 4 }
        ]);

        var builder = new RcpSequenceBuilder();
        var plan = builder.Build(song, new RcpSequenceBuilderOptions
        {
            TreatVelocityZeroNoteAsNoteOff = true
        });

        var shorts = plan.MidiEvents
            .Where(e => e.Packet.Kind == MidiMessageKind.Short)
            .Select(e => new
            {
                e.Tick,
                Message = e.Packet.ShortMessage
            })
            .ToList();

        Assert.Collection(
            shorts,
            e =>
            {
                Assert.Equal(0L, e.Tick);
                Assert.Equal((uint)0x00643C90, e.Message);
            },
            e =>
            {
                Assert.Equal(2L, e.Tick);
                Assert.Equal((uint)0x00003C90, e.Message);
            });
    }

    [Fact]
    public void Build_E2_EmitsCc0Cc32AndProgram()
    {
        var song = CreateSong(
        [
            new RcpEvent { Index = 0, CommandOrNote = 0xE2, DelayTicks = 0, Param1 = 0x11, Param2 = 0x22, RawLength = 4 },
            new RcpEvent { Index = 1, CommandOrNote = 0xFE, DelayTicks = 0, Param1 = 0, Param2 = 0, RawLength = 4 }
        ]);

        var builder = new RcpSequenceBuilder();
        var plan = builder.Build(song);

        var shorts = plan.MidiEvents
            .Where(e => e.Packet.Kind == MidiMessageKind.Short)
            .Select(e => e.Packet.ShortMessage)
            .ToList();

        Assert.Equal(3, shorts.Count);
        Assert.Equal((uint)0x002200B0, shorts[0]); // CC0 bank
        Assert.Equal((uint)0x000020B0, shorts[1]); // CC32 bank LSB = 0
        Assert.Equal((uint)0x000011C0, shorts[2]); // Program change
    }

    [Fact]
    public void Build_E6_RcdStrictMode_Channel33_DisablesMidiOutput()
    {
        var song = CreateSong(
        [
            new RcpEvent { Index = 0, CommandOrNote = 0xE6, DelayTicks = 0, Param1 = 33, Param2 = 0, RawLength = 4 },
            new RcpEvent { Index = 1, CommandOrNote = 60, DelayTicks = 0, Param1 = 4, Param2 = 100, RawLength = 4 },
            new RcpEvent { Index = 2, CommandOrNote = 0xFE, DelayTicks = 0, Param1 = 0, Param2 = 0, RawLength = 4 }
        ]);

        var builder = new RcpSequenceBuilder();
        var strictPlan = builder.Build(song, new RcpSequenceBuilderOptions
        {
            RcdStrictMode = true
        });
        var defaultPlan = builder.Build(song, new RcpSequenceBuilderOptions
        {
            RcdStrictMode = false
        });

        Assert.DoesNotContain(strictPlan.MidiEvents, e => e.Packet.Kind == MidiMessageKind.Short);
        Assert.Contains(defaultPlan.MidiEvents, e => e.Packet.Kind == MidiMessageKind.Short);
    }

    [Fact]
    public void Build_Ec_RcdStrictMode_DropsUserToneProgramSysEx()
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
                    DefaultChannel = 1,
                    IsMuted = false,
                    Events =
                    [
                        new RcpEvent { Index = 0, CommandOrNote = 0xEC, DelayTicks = 0, Param1 = 0x81, Param2 = 0, RawLength = 4 },
                        new RcpEvent { Index = 1, CommandOrNote = 0xFE, DelayTicks = 0, Param1 = 0, Param2 = 0, RawLength = 4 }
                    ]
                }
            ]
        };

        var builder = new RcpSequenceBuilder();
        var strictPlan = builder.Build(song, new RcpSequenceBuilderOptions
        {
            RcdStrictMode = true
        });
        var defaultPlan = builder.Build(song, new RcpSequenceBuilderOptions
        {
            RcdStrictMode = false
        });

        Assert.DoesNotContain(strictPlan.MidiEvents, e => e.Packet.Kind == MidiMessageKind.SysEx);
        Assert.Contains(defaultPlan.MidiEvents, e => e.Packet.Kind == MidiMessageKind.SysEx);
    }

    [Fact]
    public void Build_RealtimeExpansion_PreservesSourceTrackOrderWithinSameTick()
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
                    TrackId = 10,
                    Name = "first",
                    DefaultChannel = 0,
                    IsMuted = false,
                    Events =
                    [
                        new RcpEvent { Index = 0, CommandOrNote = 60, DelayTicks = 0, Param1 = 1, Param2 = 100, RawLength = 4 },
                        new RcpEvent { Index = 1, CommandOrNote = 0xFE, DelayTicks = 0, Param1 = 0, Param2 = 0, RawLength = 4 }
                    ]
                },
                new RcpTrack
                {
                    TrackId = 2,
                    Name = "second",
                    DefaultChannel = 1,
                    IsMuted = false,
                    Events =
                    [
                        new RcpEvent { Index = 0, CommandOrNote = 61, DelayTicks = 0, Param1 = 1, Param2 = 100, RawLength = 4 },
                        new RcpEvent { Index = 1, CommandOrNote = 0xFE, DelayTicks = 0, Param1 = 0, Param2 = 0, RawLength = 4 }
                    ]
                },
                new RcpTrack
                {
                    TrackId = 99,
                    Name = "loop-driver",
                    DefaultChannel = 2,
                    IsMuted = false,
                    Events =
                    [
                        new RcpEvent { Index = 0, CommandOrNote = 0xF9, DelayTicks = 0, Param1 = 0, Param2 = 0, RawLength = 4 },
                        new RcpEvent { Index = 1, CommandOrNote = 0xEB, DelayTicks = 0, Param1 = 7, Param2 = 100, RawLength = 4 },
                        new RcpEvent { Index = 2, CommandOrNote = 0xF8, DelayTicks = 0, Param1 = 0, Param2 = 0, RawLength = 4 },
                        new RcpEvent { Index = 3, CommandOrNote = 0xFE, DelayTicks = 0, Param1 = 0, Param2 = 0, RawLength = 4 }
                    ]
                }
            ]
        };

        var builder = new RcpSequenceBuilder();
        var plan = builder.Build(song);

        var noteOns = plan.MidiEvents
            .Where(e => e.Packet.Kind == MidiMessageKind.Short &&
                        (((byte)e.Packet.ShortMessage) & 0xF0) == 0x90 &&
                        (((e.Packet.ShortMessage >> 16) & 0x7F) > 0))
            .Take(2)
            .Select(e => (((byte)e.Packet.ShortMessage) & 0x0F) + 1)
            .ToList();

        Assert.Equal([1, 2], noteOns);
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
