using RcpPlayer.Core.Model;

namespace RcpPlayer.Core.Playback;

public sealed class RcpSequenceBuilderOptions
{
    public int InfinityLoopCount { get; init; } = 2;
    public int MaxExpandedEventsPerTrack { get; init; } = 200_000;
    public bool IgnoreMutedTracks { get; init; } = true;
}

public sealed class RcpPlaybackPlan
{
    public required int TimeBase { get; init; }
    public required double InitialTempoBpm { get; init; }
    public required IReadOnlyList<TempoEvent> TempoEvents { get; init; }
    public required IReadOnlyList<ScheduledMidiEvent> MidiEvents { get; init; }
    public RcpSong? SourceSong { get; init; }
    public required RcpBuildDiagnostics BuildDiagnostics { get; init; }
}

public sealed class UnsupportedRcpCommandStat
{
    public required byte Command { get; init; }
    public required int Count { get; init; }
    public required IReadOnlyList<int> Tracks { get; init; }
}

public sealed class RcpBuildDiagnostics
{
    public required IReadOnlyList<UnsupportedRcpCommandStat> UnsupportedCommands { get; init; }
    public required IReadOnlyList<int> LoopExpansionLimitTracks { get; init; }
}

public sealed class RcpSequenceBuilder
{
    public RcpPlaybackPlan Build(RcpSong song, RcpSequenceBuilderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(song);
        options ??= new RcpSequenceBuilderOptions();

        var tempoModifiers = new List<TempoModifierCommand>();
        var midiEvents = new List<ScheduledMidiEvent>();
        var unsupportedCommands = new Dictionary<byte, CommandAccumulator>();
        var loopExpansionLimitTracks = new HashSet<int>();

        foreach (var track in song.Tracks)
        {
            if (track.IsMuted && options.IgnoreMutedTracks)
            {
                continue;
            }

            ExpandTrack(song, track, midiEvents, tempoModifiers, unsupportedCommands, loopExpansionLimitTracks, options);
        }

        var orderedTempo = ResolveTempoEvents(song, tempoModifiers);

        var orderedMidi = MidiEventOrdering.OrderByTimeline(midiEvents);
        var unsupportedStats = unsupportedCommands
            .OrderBy(kvp => kvp.Key)
            .Select(kvp => new UnsupportedRcpCommandStat
            {
                Command = kvp.Key,
                Count = kvp.Value.Count,
                Tracks = kvp.Value.Tracks.OrderBy(t => t).ToList()
            })
            .ToList();

        return new RcpPlaybackPlan
        {
            TimeBase = Math.Max(song.TimeBase, 1),
            InitialTempoBpm = Math.Max(song.TempoBpm, 1),
            TempoEvents = orderedTempo,
            MidiEvents = orderedMidi,
            SourceSong = song,
            BuildDiagnostics = new RcpBuildDiagnostics
            {
                UnsupportedCommands = unsupportedStats,
                LoopExpansionLimitTracks = loopExpansionLimitTracks.OrderBy(t => t).ToList()
            }
        };
    }

    private static void ExpandTrack(
        RcpSong song,
        RcpTrack track,
        List<ScheduledMidiEvent> midiEvents,
        List<TempoModifierCommand> tempoModifiers,
        Dictionary<byte, CommandAccumulator> unsupportedCommands,
        HashSet<int> loopExpansionLimitTracks,
        RcpSequenceBuilderOptions options)
    {
        var events = track.Events;
        if (events.Count == 0)
        {
            return;
        }

        long tick = 0;
        var channel = ClampChannel(track.DefaultChannel);
        var ip = 0;
        var expandedEventCount = 0;

        var rolandDev = 0x10;
        var rolandModel = 0x16;
        var rolandBaseH = 0x00;
        var rolandBaseM = 0x10;

        var yamahaDev = 0x10;
        var yamahaModel = 0x4C;
        var yamahaBaseH = 0x00;
        var yamahaBaseM = 0x00;

        var loops = new Stack<LoopFrame>();
        var repeatMeasureReturnIps = new Stack<int>();
        var measureStartIndices = BuildMeasureStartIndices(events);
        var activeNotes = new Dictionary<(int Channel, int Note), ActiveNoteState>();

        while (ip >= 0 && ip < events.Count)
        {
            expandedEventCount++;
            if (expandedEventCount > options.MaxExpandedEventsPerTrack)
            {
                loopExpansionLimitTracks.Add(track.TrackId);
                break;
            }

            var e = events[ip];
            var cmd = e.CommandOrNote;

            if (cmd < 0x80)
            {
                PruneExpiredNotes(activeNotes, tick);

                var note = cmd;
                var velocity = (byte)Clamp7Bit(e.Param2);
                var gate = e.Param1;
                if (gate > 0)
                {
                    var noteKey = (channel, (int)note);
                    var offTick = tick + gate;
                    if (activeNotes.TryGetValue(noteKey, out var active))
                    {
                        // Recomposer behavior: retriggering the same active note updates its length.
                        var oldOffEvent = midiEvents[active.OffEventIndex];
                        midiEvents[active.OffEventIndex] = new ScheduledMidiEvent
                        {
                            Tick = offTick,
                            Packet = oldOffEvent.Packet
                        };
                        activeNotes[noteKey] = new ActiveNoteState(active.OffEventIndex, offTick);
                    }
                    else
                    {
                        midiEvents.Add(new ScheduledMidiEvent
                        {
                            Tick = tick,
                            Packet = new MidiEventPacket
                            {
                                Kind = MidiMessageKind.Short,
                                ShortMessage = PackShort((byte)(0x90 | channel), note, velocity),
                                SysExData = null
                            }
                        });
                        midiEvents.Add(new ScheduledMidiEvent
                        {
                            Tick = offTick,
                            Packet = new MidiEventPacket
                            {
                                Kind = MidiMessageKind.Short,
                                ShortMessage = PackShort((byte)(0x80 | channel), note, 0),
                                SysExData = null
                            }
                        });
                        activeNotes[noteKey] = new ActiveNoteState(midiEvents.Count - 1, offTick);
                    }
                }

                tick += e.DelayTicks;
                ip++;
                continue;
            }

            switch (cmd)
            {
                case 0x90:
                case 0x91:
                case 0x92:
                case 0x93:
                case 0x94:
                case 0x95:
                case 0x96:
                case 0x97:
                {
                    var index = cmd - 0x90;
                    if (index < song.UserExclusives.Count)
                    {
                        var body = song.UserExclusives[index].DataWithoutLeadingF0;
                        var ex = ExpandSysExTemplate(body, Clamp7Bit(e.Param1), Clamp7Bit(e.Param2), channel);
                        midiEvents.Add(NewSysExEvent(tick, ex));
                    }

                    tick += e.DelayTicks;
                    ip++;
                    break;
                }
                case 0x98:
                {
                    var raw = new List<byte> { (byte)Clamp7Bit(e.Param1), (byte)Clamp7Bit(e.Param2) };
                    var isG36Event = e.RawLength >= 6;
                    var localIp = ip + 1;
                    while (localIp < events.Count && events[localIp].CommandOrNote == 0xF7)
                    {
                        AppendContinuationBytes(raw, events[localIp], isG36Event);
                        localIp++;
                    }

                    var ex = ExpandSysExTemplate(raw.ToArray(), Clamp7Bit(e.Param1), Clamp7Bit(e.Param2), channel);
                    midiEvents.Add(NewSysExEvent(tick, ex));
                    tick += e.DelayTicks;
                    ip = localIp;
                    break;
                }
                case 0x99:
                    // External command (MCI/RUN/...) does not emit MIDI in realtime playback.
                    tick += e.DelayTicks;
                    ip = SkipContinuationEvents(events, ip + 1);
                    break;
                case 0xE1:
                    midiEvents.Add(NewShortEvent(tick, 0xB0 | channel, 32, Clamp7Bit(e.Param2)));
                    midiEvents.Add(NewShortEvent(tick, 0xC0 | channel, Clamp7Bit(e.Param1), 0));
                    tick += e.DelayTicks;
                    ip++;
                    break;
                case 0xE2:
                    midiEvents.Add(NewShortEvent(tick, 0xB0 | channel, 0, Clamp7Bit(e.Param2)));
                    midiEvents.Add(NewShortEvent(tick, 0xC0 | channel, Clamp7Bit(e.Param1), 0));
                    tick += e.DelayTicks;
                    ip++;
                    break;
                case 0xE5:
                    // Key scan command (UI control in original Recomposer); keep timing only.
                    tick += e.DelayTicks;
                    ip++;
                    break;
                case 0xEA:
                    midiEvents.Add(NewShortEvent(tick, 0xD0 | channel, Clamp7Bit(e.Param1), 0));
                    tick += e.DelayTicks;
                    ip++;
                    break;
                case 0xEB:
                    midiEvents.Add(NewShortEvent(tick, 0xB0 | channel, Clamp7Bit(e.Param1), Clamp7Bit(e.Param2)));
                    tick += e.DelayTicks;
                    ip++;
                    break;
                case 0xEC:
                    midiEvents.Add(NewShortEvent(tick, 0xC0 | channel, Clamp7Bit(e.Param1), 0));
                    tick += e.DelayTicks;
                    ip++;
                    break;
                case 0xED:
                    midiEvents.Add(NewShortEvent(tick, 0xA0 | channel, Clamp7Bit(e.Param1), Clamp7Bit(e.Param2)));
                    tick += e.DelayTicks;
                    ip++;
                    break;
                case 0xEE:
                    midiEvents.Add(NewShortEvent(tick, 0xE0 | channel, Clamp7Bit(e.Param1), Clamp7Bit(e.Param2)));
                    tick += e.DelayTicks;
                    ip++;
                    break;
                case 0xE6:
                    channel = ClampChannel(NormalizeCommandChannel(e.Param1));
                    tick += e.DelayTicks;
                    ip++;
                    break;
                case 0xE7:
                {
                    tempoModifiers.Add(new TempoModifierCommand(
                        Tick: tick,
                        Ratio: Math.Max(1, e.Param1),
                        Gradation: Clamp8Bit(e.Param2)));
                    tick += e.DelayTicks;
                    ip++;
                    break;
                }
                case 0xC0:
                    midiEvents.Add(NewSysExEvent(tick, new byte[] { 0xF0, 0x43, (byte)(0x10 + channel), 0x08, (byte)Clamp7Bit(e.Param1), (byte)Clamp7Bit(e.Param2), 0xF7 }));
                    tick += e.DelayTicks;
                    ip++;
                    break;
                case 0xC1:
                    midiEvents.Add(NewSysExEvent(tick, new byte[] { 0xF0, 0x43, (byte)(0x10 + channel), 0x00, (byte)Clamp7Bit(e.Param1), (byte)Clamp7Bit(e.Param2), 0xF7 }));
                    tick += e.DelayTicks;
                    ip++;
                    break;
                case 0xC2:
                    midiEvents.Add(NewSysExEvent(tick, new byte[] { 0xF0, 0x43, (byte)(0x10 + channel), 0x04, (byte)Clamp7Bit(e.Param1), (byte)Clamp7Bit(e.Param2), 0xF7 }));
                    tick += e.DelayTicks;
                    ip++;
                    break;
                case 0xC3:
                    midiEvents.Add(NewSysExEvent(tick, new byte[] { 0xF0, 0x43, (byte)(0x10 + channel), 0x11, (byte)Clamp7Bit(e.Param1), (byte)Clamp7Bit(e.Param2), 0xF7 }));
                    tick += e.DelayTicks;
                    ip++;
                    break;
                case 0xC5:
                    midiEvents.Add(NewSysExEvent(tick, new byte[] { 0xF0, 0x43, (byte)(0x10 + channel), 0x15, (byte)Clamp7Bit(e.Param1), (byte)Clamp7Bit(e.Param2), 0xF7 }));
                    tick += e.DelayTicks;
                    ip++;
                    break;
                case 0xC6:
                    midiEvents.Add(NewSysExEvent(tick, new byte[] { 0xF0, 0x43, 0x75, (byte)channel, 0x10, (byte)Clamp7Bit(e.Param1), (byte)Clamp7Bit(e.Param2), 0xF7 }));
                    tick += e.DelayTicks;
                    ip++;
                    break;
                case 0xC7:
                    midiEvents.Add(NewSysExEvent(tick, new byte[] { 0xF0, 0x43, (byte)(0x10 + channel), 0x12, (byte)Clamp7Bit(e.Param1), (byte)Clamp7Bit(e.Param2), 0xF7 }));
                    tick += e.DelayTicks;
                    ip++;
                    break;
                case 0xC8:
                    midiEvents.Add(NewSysExEvent(tick, new byte[] { 0xF0, 0x43, (byte)(0x10 + channel), 0x13, (byte)Clamp7Bit(e.Param1), (byte)Clamp7Bit(e.Param2), 0xF7 }));
                    tick += e.DelayTicks;
                    ip++;
                    break;
                case 0xC9:
                    midiEvents.Add(NewSysExEvent(tick, new byte[] { 0xF0, 0x43, (byte)(0x10 + channel), 0x10, (byte)Clamp7Bit(e.Param1), (byte)Clamp7Bit(e.Param2), 0xF7 }));
                    tick += e.DelayTicks;
                    ip++;
                    break;
                case 0xCA:
                    midiEvents.Add(NewSysExEvent(tick, new byte[] { 0xF0, 0x43, (byte)(0x10 + channel), 0x10, 0x7B, (byte)Clamp7Bit(e.Param1), (byte)Clamp7Bit(e.Param2), 0xF7 }));
                    tick += e.DelayTicks;
                    ip++;
                    break;
                case 0xCB:
                    midiEvents.Add(NewSysExEvent(tick, new byte[] { 0xF0, 0x43, (byte)(0x10 + channel), 0x10, 0x7C, (byte)Clamp7Bit(e.Param1), (byte)Clamp7Bit(e.Param2), 0xF7 }));
                    tick += e.DelayTicks;
                    ip++;
                    break;
                case 0xCC:
                    midiEvents.Add(NewSysExEvent(tick, new byte[] { 0xF0, 0x43, (byte)(0x10 + channel), 0x1B, (byte)Clamp7Bit(e.Param1), (byte)Clamp7Bit(e.Param2), 0xF7 }));
                    tick += e.DelayTicks;
                    ip++;
                    break;
                case 0xCD:
                    midiEvents.Add(NewSysExEvent(tick, new byte[] { 0xF0, 0x43, (byte)(0x10 + channel), 0x18, (byte)Clamp7Bit(e.Param1), (byte)Clamp7Bit(e.Param2), 0xF7 }));
                    tick += e.DelayTicks;
                    ip++;
                    break;
                case 0xCE:
                    midiEvents.Add(NewSysExEvent(tick, new byte[] { 0xF0, 0x43, (byte)(0x10 + channel), 0x19, (byte)Clamp7Bit(e.Param1), (byte)Clamp7Bit(e.Param2), 0xF7 }));
                    tick += e.DelayTicks;
                    ip++;
                    break;
                case 0xCF:
                    midiEvents.Add(NewSysExEvent(tick, new byte[] { 0xF0, 0x43, (byte)(0x10 + channel), 0x1A, (byte)Clamp7Bit(e.Param1), (byte)Clamp7Bit(e.Param2), 0xF7 }));
                    tick += e.DelayTicks;
                    ip++;
                    break;
                case 0xD0:
                    yamahaBaseH = Clamp7Bit(e.Param1);
                    yamahaBaseM = Clamp7Bit(e.Param2);
                    tick += e.DelayTicks;
                    ip++;
                    break;
                case 0xD1:
                    yamahaDev = Clamp7Bit(e.Param1);
                    yamahaModel = Clamp7Bit(e.Param2);
                    tick += e.DelayTicks;
                    ip++;
                    break;
                case 0xD2:
                    midiEvents.Add(NewSysExEvent(tick, new byte[]
                    {
                        0xF0, 0x43, (byte)yamahaDev, (byte)yamahaModel,
                        (byte)yamahaBaseH, (byte)yamahaBaseM, (byte)Clamp7Bit(e.Param1), (byte)Clamp7Bit(e.Param2), 0xF7
                    }));
                    tick += e.DelayTicks;
                    ip++;
                    break;
                case 0xD3:
                    midiEvents.Add(NewSysExEvent(tick, new byte[]
                    {
                        0xF0, 0x43, 0x10, 0x4C,
                        (byte)yamahaBaseH, (byte)yamahaBaseM, (byte)Clamp7Bit(e.Param1), (byte)Clamp7Bit(e.Param2), 0xF7
                    }));
                    tick += e.DelayTicks;
                    ip++;
                    break;
                case 0xDC:
                    midiEvents.Add(NewSysExEvent(tick, new byte[] { 0xF0, 0x41, 0x32, (byte)channel, (byte)Clamp7Bit(e.Param1), (byte)Clamp7Bit(e.Param2), 0xF7 }));
                    tick += e.DelayTicks;
                    ip++;
                    break;
                case 0xDD:
                    rolandBaseH = Clamp7Bit(e.Param1);
                    rolandBaseM = Clamp7Bit(e.Param2);
                    tick += e.DelayTicks;
                    ip++;
                    break;
                case 0xDE:
                {
                    var addrL = Clamp7Bit(e.Param1);
                    var parameter = Clamp7Bit(e.Param2);
                    var sum = rolandBaseH + rolandBaseM + addrL + parameter;
                    var check = (128 - (sum & 0x7F)) & 0x7F;
                    midiEvents.Add(NewSysExEvent(tick, new byte[]
                    {
                        0xF0, 0x41, (byte)rolandDev, (byte)rolandModel, 0x12,
                        (byte)rolandBaseH, (byte)rolandBaseM, (byte)addrL, (byte)parameter, (byte)check, 0xF7
                    }));
                    tick += e.DelayTicks;
                    ip++;
                    break;
                }
                case 0xDF:
                    rolandDev = Clamp7Bit(e.Param1);
                    rolandModel = Clamp7Bit(e.Param2);
                    tick += e.DelayTicks;
                    ip++;
                    break;
                case 0xF9:
                    loops.Push(new LoopFrame(ip + 1));
                    ip++;
                    break;
                case 0xF8:
                {
                    if (loops.Count == 0)
                    {
                        ip++;
                        break;
                    }

                    var top = loops.Pop();
                    var repeatCount = e.DelayTicks == 0 ? Math.Max(options.InfinityLoopCount, 1) : e.DelayTicks;
                    if (top.Iteration + 1 < repeatCount)
                    {
                        top = top with { Iteration = top.Iteration + 1 };
                        loops.Push(top);
                        ip = top.StartIndex;
                    }
                    else
                    {
                        ip++;
                    }

                    break;
                }
                case 0xFD:
                    // Bar/measure separator event seen in many RCP files.
                    // It does not emit MIDI, but its delay contributes to timeline.
                    tick += e.DelayTicks;
                    ip = repeatMeasureReturnIps.Count > 0
                        ? repeatMeasureReturnIps.Pop()
                        : ip + 1;
                    break;
                case 0xFC:
                {
                    var measureId = GetRepeatMeasureId(e);
                    if (measureId >= 0 && measureId < measureStartIndices.Count)
                    {
                        var destination = measureStartIndices[measureId];
                        if (destination >= 0 &&
                            destination < events.Count &&
                            destination != ip &&
                            repeatMeasureReturnIps.Count < 64)
                        {
                            repeatMeasureReturnIps.Push(ip + 1);
                            ip = destination;
                            break;
                        }
                    }

                    ip++;
                    break;
                }
                case 0xF7:
                    // Continuation event handled by commands 98/99/F6; ignore standalone.
                    ip++;
                    break;
                case 0xF6:
                    // Comment command has no realtime MIDI effect.
                    ip = SkipContinuationEvents(events, ip + 1);
                    break;
                case 0xF5:
                    // Key signature command is metadata for SMF conversion only.
                    ip++;
                    break;
                case 0xFE:
                    return;
                default:
                    if (cmd >= 0x80)
                    {
                        RecordUnsupportedCommand(unsupportedCommands, cmd, track.TrackId);
                    }

                    if (cmd < 0xF0)
                    {
                        tick += e.DelayTicks;
                    }

                    ip++;
                    break;
            }
        }
    }

    private static byte[] ExpandSysExTemplate(byte[] raw, int p1, int p2, int channel)
    {
        var result = new List<byte> { 0xF0 };
        var checksumMode = false;
        var checksumSum = 0;

        foreach (var token in raw)
        {
            if (token == 0xF7)
            {
                result.Add(0xF7);
                return result.ToArray();
            }

            if (token == 0x83)
            {
                checksumMode = true;
                checksumSum = 0;
                continue;
            }

            byte value;
            if (token == 0x80)
            {
                value = (byte)p1;
            }
            else if (token == 0x81)
            {
                value = (byte)p2;
            }
            else if (token == 0x82)
            {
                value = (byte)channel;
            }
            else if (token == 0x84)
            {
                value = (byte)((128 - (checksumSum & 0x7F)) & 0x7F);
                checksumMode = false;
            }
            else
            {
                value = token;
            }

            if (checksumMode && token != 0x84)
            {
                checksumSum += value;
            }

            result.Add(value);
        }

        if (result[^1] != 0xF7)
        {
            result.Add(0xF7);
        }

        return result.ToArray();
    }

    private static int NormalizeCommandChannel(int value)
    {
        if (value is >= 1 and <= 16)
        {
            return value - 1;
        }

        if (value is >= 17 and <= 32)
        {
            return value - 17;
        }

        return value & 0x0F;
    }

    private static int Clamp7Bit(int value)
    {
        return Math.Clamp(value, 0, 127);
    }

    private static int Clamp8Bit(int value)
    {
        return Math.Clamp(value, 0, 255);
    }

    private static int ClampChannel(int channel)
    {
        return Math.Clamp(channel, 0, 15);
    }

    private static void PruneExpiredNotes(Dictionary<(int Channel, int Note), ActiveNoteState> activeNotes, long tick)
    {
        if (activeNotes.Count == 0)
        {
            return;
        }

        var expired = activeNotes
            // Keep offTick == tick alive until current note command is processed.
            // This allows same-tick same-note events to behave as length extension
            // instead of retriggering a fresh NoteOn.
            .Where(kvp => kvp.Value.OffTick < tick)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var key in expired)
        {
            activeNotes.Remove(key);
        }
    }

    private static IReadOnlyList<TempoEvent> ResolveTempoEvents(RcpSong song, IReadOnlyList<TempoModifierCommand> commands)
    {
        if (commands.Count == 0)
        {
            return [];
        }

        var baseTempo = Math.Max(song.TempoBpm, 1.0);
        var currentTempo = baseTempo;
        var resolved = new List<TempoEvent>(commands.Count * 6);

        foreach (var item in commands.Select((c, i) => new OrderedTempoModifier(c, i)).OrderBy(x => x.Command.Tick).ThenBy(x => x.Order))
        {
            var cmd = item.Command;
            var targetTempo = Math.Max(1.0, baseTempo * (Math.Max(1, cmd.Ratio) / 64.0));
            if (cmd.Gradation <= 0)
            {
                resolved.Add(new TempoEvent { Tick = cmd.Tick, Bpm = targetTempo });
                currentTempo = targetTempo;
                continue;
            }

            var gradation = Math.Max(cmd.Gradation, 1);
            for (var step = 1; step <= gradation; step++)
            {
                var ratio = step / (double)gradation;
                var tempo = currentTempo + ((targetTempo - currentTempo) * ratio);
                resolved.Add(new TempoEvent
                {
                    Tick = cmd.Tick + step,
                    Bpm = Math.Max(1.0, tempo)
                });
            }

            currentTempo = targetTempo;
        }

        return resolved
            .OrderBy(e => e.Tick)
            .GroupBy(e => e.Tick)
            .Select(g => g.Last())
            .ToList();
    }

    private static int SkipContinuationEvents(IReadOnlyList<RcpEvent> events, int startIndex)
    {
        var index = startIndex;
        while (index < events.Count && events[index].CommandOrNote == 0xF7)
        {
            index++;
        }

        return index;
    }

    private static IReadOnlyList<int> BuildMeasureStartIndices(IReadOnlyList<RcpEvent> events)
    {
        var starts = new List<int> { 0 };
        for (var i = 0; i < events.Count; i++)
        {
            if (events[i].CommandOrNote != 0xFD)
            {
                continue;
            }

            var next = i + 1;
            if (next < events.Count && starts[^1] != next)
            {
                starts.Add(next);
            }
        }

        return starts;
    }

    private static int GetRepeatMeasureId(RcpEvent e)
    {
        if (e.RawLength >= 6)
        {
            return Math.Max(e.DelayTicks, 0);
        }

        var low = e.DelayTicks & 0xFF;
        var high = e.Param1 & 0x03;
        return (high << 8) | low;
    }

    private static void AppendContinuationBytes(List<byte> raw, RcpEvent continuation, bool isG36Event)
    {
        if (!isG36Event)
        {
            raw.Add((byte)Clamp7Bit(continuation.Param1));
            raw.Add((byte)Clamp7Bit(continuation.Param2));
            return;
        }

        raw.Add((byte)Clamp7Bit(continuation.Param2));
        raw.Add((byte)Clamp7Bit(continuation.DelayTicks & 0xFF));
        raw.Add((byte)Clamp7Bit((continuation.DelayTicks >> 8) & 0xFF));
        raw.Add((byte)Clamp7Bit(continuation.Param1 & 0xFF));
        raw.Add((byte)Clamp7Bit((continuation.Param1 >> 8) & 0xFF));
    }

    private static ScheduledMidiEvent NewShortEvent(long tick, int status, int data1, int data2)
    {
        return new ScheduledMidiEvent
        {
            Tick = tick,
            Packet = new MidiEventPacket
            {
                Kind = MidiMessageKind.Short,
                ShortMessage = PackShort((byte)status, (byte)Clamp7Bit(data1), (byte)Clamp7Bit(data2)),
                SysExData = null
            }
        };
    }

    private static ScheduledMidiEvent NewSysExEvent(long tick, byte[] data)
    {
        return new ScheduledMidiEvent
        {
            Tick = tick,
            Packet = new MidiEventPacket
            {
                Kind = MidiMessageKind.SysEx,
                ShortMessage = 0,
                SysExData = data
            }
        };
    }

    private static uint PackShort(byte status, byte data1, byte data2)
    {
        return (uint)(status | (data1 << 8) | (data2 << 16));
    }

    private static void RecordUnsupportedCommand(Dictionary<byte, CommandAccumulator> unsupportedCommands, byte command, int trackId)
    {
        if (!unsupportedCommands.TryGetValue(command, out var accumulator))
        {
            accumulator = new CommandAccumulator();
            unsupportedCommands[command] = accumulator;
        }

        accumulator.Count++;
        accumulator.Tracks.Add(trackId);
    }

    private readonly record struct LoopFrame(int StartIndex, int Iteration = 0);
    private readonly record struct ActiveNoteState(int OffEventIndex, long OffTick);
    private readonly record struct TempoModifierCommand(long Tick, int Ratio, int Gradation);
    private readonly record struct OrderedTempoModifier(TempoModifierCommand Command, int Order);

    private sealed class CommandAccumulator
    {
        public int Count { get; set; }
        public HashSet<int> Tracks { get; } = [];
    }
}
