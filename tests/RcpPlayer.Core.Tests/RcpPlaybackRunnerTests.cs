using RcpPlayer.Core.Playback;
using RcpPlayer.Core.Model;

namespace RcpPlayer.Core.Tests;

public sealed class RcpPlaybackRunnerTests
{
    [Fact]
    public async Task PlayAsync_FiltersEvents_WhenPredicateReturnsFalse()
    {
        var runner = new RcpPlaybackRunner();
        var output = new FakeMidiOutput();
        var ch1NoteOn = PackShort(0x91, 60, 100);
        var ch2NoteOn = PackShort(0x92, 64, 100);
        var ch1NoteOff = PackShort(0x81, 60, 0);
        var plan = new RcpPlaybackPlan
        {
            TimeBase = 480,
            InitialTempoBpm = 120,
            TempoEvents = [],
            BuildDiagnostics = new RcpBuildDiagnostics
            {
                UnsupportedCommands = [],
                LoopExpansionLimitTracks = []
            },
            MidiEvents =
            [
                NewShortEvent(0, ch1NoteOn),
                NewShortEvent(0, ch2NoteOn),
                NewShortEvent(0, ch1NoteOff)
            ]
        };

        await runner.PlayAsync(
            plan,
            output,
            CancellationToken.None,
            progress: null,
            onEventDispatched: null,
            shouldSendEvent: e => e.Packet.ShortMessage != ch1NoteOn);

        Assert.DoesNotContain(ch1NoteOn, output.ShortMessages);
        Assert.Contains(ch2NoteOn, output.ShortMessages);
        Assert.Contains(ch1NoteOff, output.ShortMessages);
    }

    [Fact]
    public async Task PlayAsync_WhenCanceled_StillSendsAllNotesOff()
    {
        var runner = new RcpPlaybackRunner();
        var output = new FakeMidiOutput();
        var plan = new RcpPlaybackPlan
        {
            TimeBase = 480,
            InitialTempoBpm = 120,
            TempoEvents = [],
            BuildDiagnostics = new RcpBuildDiagnostics
            {
                UnsupportedCommands = [],
                LoopExpansionLimitTracks = []
            },
            MidiEvents =
            [
                NewShortEvent(240, PackShort(0x90, 60, 100))
            ]
        };
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await runner.PlayAsync(plan, output, cts.Token));

        var allNotesOffCount = output.ShortMessages.Count(m =>
            (m & 0xF0) == 0xB0 &&
            ((m >> 8) & 0x7F) == 123);
        Assert.Equal(16, allNotesOffCount);
    }

    [Fact]
    public async Task PlayAsync_WithRealtimeLoopSong_RepeatsUntilCanceled()
    {
        var runner = new RcpPlaybackRunner();
        var output = new FakeMidiOutput();
        var song = new RcpSong
        {
            Format = RcpFormat.RcpV2,
            Title = "loop",
            Comment = string.Empty,
            TimeBase = 960,
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
                        new RcpEvent { Index = 0, CommandOrNote = 0xF9, DelayTicks = 0, Param1 = 0, Param2 = 0, RawLength = 4 },
                        new RcpEvent { Index = 1, CommandOrNote = 60, DelayTicks = 1, Param1 = 1, Param2 = 100, RawLength = 4 },
                        new RcpEvent { Index = 2, CommandOrNote = 61, DelayTicks = 1, Param1 = 1, Param2 = 100, RawLength = 4 },
                        new RcpEvent { Index = 3, CommandOrNote = 0xF8, DelayTicks = 0, Param1 = 0, Param2 = 0, RawLength = 4 },
                        new RcpEvent { Index = 4, CommandOrNote = 0xFE, DelayTicks = 0, Param1 = 0, Param2 = 0, RawLength = 4 }
                    ]
                }
            ]
        };

        var plan = new RcpPlaybackPlan
        {
            TimeBase = 960,
            InitialTempoBpm = 120,
            TempoEvents = [],
            MidiEvents = [NewShortEvent(0, PackShort(0x90, 60, 100))],
            SourceSong = song,
            BuildDiagnostics = new RcpBuildDiagnostics
            {
                UnsupportedCommands = [],
                LoopExpansionLimitTracks = []
            }
        };

        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(40));

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await runner.PlayAsync(plan, output, cts.Token));

        var noteOnCount = output.ShortMessages.Count(m => (m & 0xF0) == 0x90 && ((m >> 16) & 0x7F) > 0);
        Assert.True(noteOnCount >= 2, $"Expected realtime loop to emit repeated NoteOn events, actual count={noteOnCount}.");
    }

    private static ScheduledMidiEvent NewShortEvent(long tick, uint shortMessage)
    {
        return new ScheduledMidiEvent
        {
            Tick = tick,
            Packet = new MidiEventPacket
            {
                Kind = MidiMessageKind.Short,
                ShortMessage = shortMessage,
                SysExData = null
            }
        };
    }

    private static uint PackShort(byte status, byte data1, byte data2)
    {
        return (uint)(status | (data1 << 8) | (data2 << 16));
    }

    private sealed class FakeMidiOutput : IMidiOutput
    {
        public List<uint> ShortMessages { get; } = [];

        public Task<IReadOnlyList<MidiEndpointInfo>> GetEndpointsAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<MidiEndpointInfo>>([]);
        }

        public Task OpenAsync(string endpointId, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task SendShortAsync(uint shortMessage, CancellationToken cancellationToken = default)
        {
            ShortMessages.Add(shortMessage);
            return Task.CompletedTask;
        }

        public Task SendSysExAsync(byte[] data, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task CloseAsync()
        {
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }
    }
}
