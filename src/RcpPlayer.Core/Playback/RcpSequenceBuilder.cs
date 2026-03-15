using RcpPlayer.Core.Model;

namespace RcpPlayer.Core.Playback;

public sealed class RcpSequenceBuilderOptions
{
    public int InfinityLoopCount { get; init; } = 2;
    public int MaxExpandedEventsPerTrack { get; init; } = 200_000;
    public bool MergeActiveSameNote { get; init; } = true;
    public bool KeepBoundaryActiveSameNote { get; init; } = false;
    public bool EmitEmptySysEx { get; init; } = false;
    public bool UseRealtimeForInfiniteLoops { get; init; } = true;
    public bool StopOnInfiniteTracksOnly { get; init; } = true;
    public bool FreezeInfiniteTracksAfterFirstCycle { get; init; } = false;
    public bool IgnoreMutedTracks { get; init; } = true;
    public bool TreatHighLoopCountAsInfinite { get; init; } = true;
    public bool BalanceInfiniteLoopTracks { get; init; } = false;
    public long InfiniteLoopTailTicks { get; init; } = 0;
    public int InfiniteLoopTailMeasures { get; init; } = 0;
    public bool IgnorePendingOnAllTracksPlayedStop { get; init; } = false;
    public bool EmitPendingNoteOffsAfterTrackEnd { get; init; } = true;
    // x68 playback does not coerce velocity-zero note events into MIDI
    // note-offs, even though conventional MIDI receivers often do.
    public bool TreatVelocityZeroNoteAsNoteOff { get; init; } = false;
    public bool RcpcvCompatTerminateDrumSetupAtReverbComment { get; init; } = false;
    public bool RcpcvCompatBarDelayBeforeVelocityZeroNote { get; init; } = false;
    public bool RcpcvCompatLimitSysExPayloadTo255 { get; init; } = false;
    public bool RcpcvCompatDropFollowingSysExAfterLongPayload { get; init; } = false;
    public bool RcpcvCompatDeduplicateSysExAtSameTick { get; init; } = false;
    public int RcpcvCompatMaxSysExPerTick { get; init; } = 0;
    public bool RcdStrictMode { get; init; } = false;
    public bool EnableLoopDebugLog { get; init; } = false;
    public int LoopDebugMaxLines { get; init; } = 200_000;
    public Action<string>? LoopDebugWriter { get; init; }
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

        if (HasTrackRequiringRealtimeExpansion(
                song,
                options.IgnoreMutedTracks,
                options.UseRealtimeForInfiniteLoops,
                options.TreatHighLoopCountAsInfinite))
        {
            return BuildWithTrackCompletion(song, options);
        }

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

        if (options.RcpcvCompatLimitSysExPayloadTo255)
        {
            ApplySysExPayloadCap(midiEvents, 255);
        }

        var orderedTempo = ResolveTempoEvents(song, tempoModifiers);

        var orderedMidi = MidiEventOrdering.OrderByTimeline(midiEvents);
        orderedMidi = ApplyRcpcvSysExPerTickCap(orderedMidi, options.RcpcvCompatMaxSysExPerTick);
        orderedMidi = ApplyRcpcvSameTickSysExDedup(orderedMidi, options.RcpcvCompatDeduplicateSysExAtSameTick);
        var globalStartTickOffset = GetGlobalStartTickOffset(song);
        if (globalStartTickOffset > 0)
        {
            orderedTempo = ShiftTempoTicks(orderedTempo, globalStartTickOffset);
            orderedMidi = ShiftMidiTicks(orderedMidi, globalStartTickOffset);
        }
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
            SourceSong = null,
            BuildDiagnostics = new RcpBuildDiagnostics
            {
                UnsupportedCommands = unsupportedStats,
                LoopExpansionLimitTracks = loopExpansionLimitTracks.OrderBy(t => t).ToList()
            }
        };
    }

    private static bool HasTrackRequiringRealtimeExpansion(
        RcpSong song,
        bool ignoreMutedTracks,
        bool useRealtimeForInfiniteLoops,
        bool treatHighLoopCountAsInfinite)
    {
        foreach (var track in song.Tracks)
        {
            if (ignoreMutedTracks && track.IsMuted)
            {
                continue;
            }

            if (useRealtimeForInfiniteLoops &&
                track.Events.Any(e => e.CommandOrNote == 0xF8 && IsInfiniteLoopRepeatCount(e.DelayTicks, treatHighLoopCountAsInfinite)))
            {
                return true;
            }

            // Repeat-measure jump command (FC) is stateful at runtime and can explode
            // when naively expanded offline. Route FC tracks through realtime expansion.
            // FD alone is a measure separator and can remain on offline expansion.
            if (track.Events.Any(e => e.CommandOrNote == 0xFC))
            {
                return true;
            }
        }

        return false;
    }

    private static RcpPlaybackPlan BuildWithTrackCompletion(RcpSong song, RcpSequenceBuilderOptions options)
    {
        var hasInfiniteTracks = song.Tracks.Any(
            track => (!options.IgnoreMutedTracks || !track.IsMuted) &&
                     track.Events.Any(
                         e => e.CommandOrNote == 0xF8 &&
                              IsInfiniteLoopRepeatCount(e.DelayTicks, options.TreatHighLoopCountAsInfinite)));
        var stopOnInfiniteTracks = options.StopOnInfiniteTracksOnly && hasInfiniteTracks;
        var freezeInfiniteTracksAfterFirstCycle = options.FreezeInfiniteTracksAfterFirstCycle || stopOnInfiniteTracks;

        var infiniteLoopRepeatOverrides = options.BalanceInfiniteLoopTracks
            ? BuildInfiniteLoopRepeatOverrides(song, options)
            : BuildFixedInfiniteLoopRepeatOverrides(song, options);

        var sequencer = new RcpRealtimeSequencer(
            song,
            options.IgnoreMutedTracks,
            options.MergeActiveSameNote,
            options.KeepBoundaryActiveSameNote,
            options.EmitEmptySysEx,
            options.EmitPendingNoteOffsAfterTrackEnd,
            options.TreatVelocityZeroNoteAsNoteOff,
            freezeInfiniteTracksAfterFirstCycle,
            options.TreatHighLoopCountAsInfinite,
            options.RcpcvCompatTerminateDrumSetupAtReverbComment,
            options.RcpcvCompatBarDelayBeforeVelocityZeroNote,
            options.RcpcvCompatLimitSysExPayloadTo255,
            options.RcpcvCompatDropFollowingSysExAfterLongPayload,
            options.RcdStrictMode,
            infiniteLoopRepeatOverrides);
        var tailTicks = 0L;
        if (hasInfiniteTracks && !stopOnInfiniteTracks)
        {
            if (options.InfiniteLoopTailTicks > 0)
            {
                tailTicks = options.InfiniteLoopTailTicks;
            }
            else if (options.InfiniteLoopTailMeasures > 0)
            {
                var beatDenominator = song.BeatDenominator is >= 1 and <= 7
                    ? 1 << song.BeatDenominator
                    : Math.Max(song.BeatDenominator, 1);
                var ticksPerMeasure = Math.Max(song.TimeBase, 1) * Math.Max(song.BeatNumerator, 1) * 4L / beatDenominator;
                if (ticksPerMeasure <= 0)
                {
                    ticksPerMeasure = Math.Max(song.TimeBase, 1) * 4L;
                }

                tailTicks = ticksPerMeasure * options.InfiniteLoopTailMeasures;
            }
        }
        long? allTracksPlayedTick = null;
        var midiEvents = new List<ScheduledMidiEvent>(65_536);
        var tempoModifiers = new List<TempoModifierCommand>(256);
        var diagnostics = new RcpBuildDiagnostics
        {
            UnsupportedCommands = [],
            LoopExpansionLimitTracks = []
        };
        var debugEnabled = options.EnableLoopDebugLog;
        var debugLines = 0;
        var debugLimit = Math.Max(options.LoopDebugMaxLines, 1000);
        void Debug(string text)
        {
            if (!debugEnabled || options.LoopDebugWriter is null || debugLines >= debugLimit)
            {
                return;
            }

            options.LoopDebugWriter(text);
            debugLines++;
        }

        var guardSteps = 0;
        var guardLimit = Math.Max(options.MaxExpandedEventsPerTrack, 50_000) * Math.Max(song.Tracks.Count, 1);
        Debug($"-- LOOP DEBUG START tracks={song.Tracks.Count} guardLimit={guardLimit} --");
        while (sequencer.TryDequeueNextTick(out var tick, out var tickEvents, out var tickTempoModifiers))
        {
            guardSteps++;
            if (guardSteps > guardLimit)
            {
                Debug($"-- BREAK reason=guard_limit steps={guardSteps} totalEvents={midiEvents.Count} --");
                break;
            }

            if (tickEvents.Count > 0)
            {
                midiEvents.AddRange(tickEvents);
            }

            if (tickTempoModifiers.Count > 0)
            {
                foreach (var item in tickTempoModifiers)
                {
                    tempoModifiers.Add(new TempoModifierCommand(item.Tick, item.Ratio, item.Gradation));
                }
            }

            Debug(
                $"STEP step={guardSteps} tick={tickEvents.FirstOrDefault()?.Tick ?? tickTempoModifiers.FirstOrDefault().Tick} midi={tickEvents.Count} tempo={tickTempoModifiers.Count} totalMidi={midiEvents.Count}");
            foreach (var state in sequencer.GetTrackDebugStates())
            {
                Debug(
                    $"  T{state.TrackId:00} active={state.IsActive} played={state.HasPlayedOnce} ended={state.IsEnded} ip={state.EventPointer} tick={state.CurrentTick} loops={state.LoopDepth} ret={state.RepeatReturnDepth} notes={state.ActiveNoteCount} pendingOff={state.PendingNoteOffCount} readyMidi={state.ReadyMidiCount} readyTempo={state.ReadyTempoCount} emittedNote={state.HasEmittedMusicalNote}");
            }

            if (tailTicks > 0)
            {
                if (!allTracksPlayedTick.HasValue && sequencer.AllActiveTracksPlayedOnceIgnorePending())
                {
                    allTracksPlayedTick = tick;
                    Debug($"-- MARK all_tracks_played_tick={tick} tailTicks={tailTicks} --");
                }

                if (allTracksPlayedTick.HasValue && tick >= allTracksPlayedTick.Value + tailTicks)
                {
                    Debug($"-- BREAK reason=tail_window_reached tick={tick} allPlayedTick={allTracksPlayedTick.Value} tailTicks={tailTicks} --");
                    break;
                }
            }

            var shouldStop = false;
            if (hasInfiniteTracks)
            {
                shouldStop = stopOnInfiniteTracks
                    ? sequencer.AllActiveInfiniteTracksPlayedOnce()
                    : tailTicks > 0
                        ? false
                        : options.IgnorePendingOnAllTracksPlayedStop
                            ? sequencer.AllActiveTracksPlayedOnceIgnorePending()
                            : sequencer.AllActiveTracksPlayedOnce();
            }
            if (shouldStop)
            {
                var reason = stopOnInfiniteTracks
                    ? "all_active_infinite_tracks_played_once"
                    : "all_active_tracks_played_once";
                Debug($"-- BREAK reason={reason} steps={guardSteps} totalEvents={midiEvents.Count} --");
                break;
            }
        }

        Debug($"-- LOOP DEBUG END steps={guardSteps} totalMidi={midiEvents.Count} totalTempoMods={tempoModifiers.Count} --");

        if (options.RcpcvCompatLimitSysExPayloadTo255)
        {
            ApplySysExPayloadCap(midiEvents, 255);
        }

        var orderedTempo = ResolveTempoEvents(song, tempoModifiers);
        var orderedMidi = MidiEventOrdering.OrderByTimeline(midiEvents);
        orderedMidi = ApplyRcpcvSysExPerTickCap(orderedMidi, options.RcpcvCompatMaxSysExPerTick);
        orderedMidi = ApplyRcpcvSameTickSysExDedup(orderedMidi, options.RcpcvCompatDeduplicateSysExAtSameTick);
        var globalStartTickOffset = GetGlobalStartTickOffset(song);
        if (globalStartTickOffset > 0)
        {
            orderedTempo = ShiftTempoTicks(orderedTempo, globalStartTickOffset);
            orderedMidi = ShiftMidiTicks(orderedMidi, globalStartTickOffset);
        }

        return new RcpPlaybackPlan
        {
            TimeBase = Math.Max(song.TimeBase, 1),
            InitialTempoBpm = Math.Max(song.TempoBpm, 1),
            TempoEvents = orderedTempo,
            MidiEvents = orderedMidi,
            SourceSong = null,
            BuildDiagnostics = diagnostics
        };
    }

    private static IReadOnlyDictionary<int, int> BuildInfiniteLoopRepeatOverrides(
        RcpSong song,
        RcpSequenceBuilderOptions options)
    {
        var analyses = new List<TrackLoopAnalysis>(song.Tracks.Count);
        foreach (var track in song.Tracks)
        {
            if (options.IgnoreMutedTracks && track.IsMuted)
            {
                continue;
            }

            analyses.Add(AnalyzeTrackLoop(song, track, options.TreatHighLoopCountAsInfinite));
        }

        if (analyses.Count == 0)
        {
            return new Dictionary<int, int>();
        }

        var baseLoopCount = Math.Max(options.InfinityLoopCount, 1);
        var minLoopTicks = Math.Max(song.TimeBase / 4, 1);
        var maxDuration = 0L;

        foreach (var item in analyses)
        {
            var duration = item.DurationTick;
            if (item.HasInfiniteLoop && item.LoopTicks > 0)
            {
                duration += item.LoopTicks * (baseLoopCount - 1L);
            }

            if (duration > maxDuration)
            {
                maxDuration = duration;
            }
        }

        var overrides = new Dictionary<int, int>();
        foreach (var item in analyses)
        {
            if (!item.HasInfiniteLoop || item.LoopTicks <= 0)
            {
                continue;
            }

            var loopCount = baseLoopCount;
            var loopTicks = item.LoopTicks;
            var expandedDuration = item.DurationTick + (loopTicks * (loopCount - 1L));
            if (loopTicks >= minLoopTicks && expandedDuration + (loopTicks / 4L) < maxDuration)
            {
                var desired = maxDuration - item.LoopStartTick;
                if (desired > 0)
                {
                    var adjusted = (desired + (loopTicks / 3L)) / loopTicks;
                    loopCount = (int)Math.Clamp(adjusted, 1, short.MaxValue);
                }
            }

            overrides[item.TrackId] = loopCount;
        }

        return overrides;
    }

    private static IReadOnlyDictionary<int, int> BuildFixedInfiniteLoopRepeatOverrides(
        RcpSong song,
        RcpSequenceBuilderOptions options)
    {
        var overrides = new Dictionary<int, int>();
        var repeatCount = Math.Max(options.InfinityLoopCount, 1);
        foreach (var track in song.Tracks)
        {
            if (options.IgnoreMutedTracks && track.IsMuted)
            {
                continue;
            }

            var hasInfiniteLoop = track.Events.Any(
                e => e.CommandOrNote == 0xF8 &&
                     IsInfiniteLoopRepeatCount(e.DelayTicks, options.TreatHighLoopCountAsInfinite));
            if (!hasInfiniteLoop)
            {
                continue;
            }

            overrides[track.TrackId] = repeatCount;
        }

        return overrides;
    }

    private static TrackLoopAnalysis AnalyzeTrackLoop(
        RcpSong song,
        RcpTrack track,
        bool treatHighLoopCountAsInfinite)
    {
        var events = track.Events;
        if (events.Count == 0)
        {
            return new TrackLoopAnalysis(track.TrackId, false, 0, 0, Math.Max(track.StartTick, 0));
        }

        var measureStartIndices = BuildMeasureStartIndices(events);
        var trackHeaderSize = GetTrackHeaderSize(song.Format);
        var trackEventSize = GetDefaultEventSize(song.Format);
        var eventIndexByTrackOffset = BuildTrackOffsetLookup(events, trackHeaderSize, trackEventSize);
        var loopStack = new Stack<LoopTickFrame>();
        int? repeatMeasureParentIp = null;
        var measureCount = 1;
        var ip = 0;
        var tick = (long)Math.Max(track.StartTick, 0);
        var guard = 0;
        var guardLimit = Math.Max(events.Count * 512, 8192);

        while (ip >= 0 && ip < events.Count && guard++ < guardLimit)
        {
            var e = events[ip];
            var cmd = e.CommandOrNote;
            if (cmd < 0x80)
            {
                tick += Math.Max(e.DelayTicks, 0);
                ip++;
                continue;
            }

            switch (cmd)
            {
                case 0xF9:
                    loopStack.Push(new LoopTickFrame(ip + 1, tick, repeatMeasureParentIp ?? -1));
                    ip++;
                    break;
                case 0xF8:
                    if (loopStack.Count == 0)
                    {
                        ip++;
                        break;
                    }

                    var top = loopStack.Pop();
                    if (IsInfiniteLoopRepeatCount(e.DelayTicks, treatHighLoopCountAsInfinite))
                    {
                        var loopStartTick = top.StartTick;
                        var loopTicks = Math.Max(0L, tick - loopStartTick);
                        return new TrackLoopAnalysis(track.TrackId, true, loopStartTick, loopTicks, tick);
                    }

                    var repeatCount = Math.Max(e.DelayTicks, 1);
                    if (top.Iteration + 1 < repeatCount)
                    {
                        top = top with { Iteration = top.Iteration + 1 };
                        loopStack.Push(top);
                        repeatMeasureParentIp = top.ParentIp >= 0 ? top.ParentIp : null;
                        ip = top.StartIndex;
                    }
                    else
                    {
                        repeatMeasureParentIp = top.ParentIp >= 0 ? top.ParentIp : null;
                        ip++;
                    }

                    break;
                case 0xFC:
                    if (repeatMeasureParentIp.HasValue)
                    {
                        ip = repeatMeasureParentIp.Value;
                        repeatMeasureParentIp = null;
                        break;
                    }

                    var chainIp = ip;
                    var parentIp = -1;
                    var resolved = false;
                    for (var chainGuard = 0; chainGuard < 256; chainGuard++)
                    {
                        if (!TryResolveRepeatDestinationForAnalysis(
                                events,
                                measureStartIndices,
                                eventIndexByTrackOffset,
                                trackHeaderSize,
                                trackEventSize,
                                chainIp,
                                out var measureId,
                                out var destination,
                                out var returnIp))
                        {
                            ip = chainIp;
                            repeatMeasureParentIp = parentIp >= 0 ? parentIp : null;
                            resolved = true;
                            break;
                        }

                        if (measureId >= measureCount ||
                            destination < 0 ||
                            destination >= events.Count ||
                            destination == chainIp)
                        {
                            ip = returnIp;
                            repeatMeasureParentIp = parentIp >= 0 ? parentIp : null;
                            resolved = true;
                            break;
                        }

                        if (parentIp < 0)
                        {
                            parentIp = returnIp;
                        }

                        chainIp = destination;
                        if (events[chainIp].CommandOrNote != 0xFC)
                        {
                            ip = chainIp;
                            repeatMeasureParentIp = parentIp >= 0 ? parentIp : null;
                            resolved = true;
                            break;
                        }
                    }

                    if (!resolved)
                    {
                        ip++;
                    }

                    break;
                case 0xFD:
                    if (repeatMeasureParentIp.HasValue)
                    {
                        ip = repeatMeasureParentIp.Value;
                        repeatMeasureParentIp = null;
                    }
                    else
                    {
                        ip++;
                    }

                    measureCount = Math.Min(measureCount + 1, short.MaxValue);
                    break;
                case 0xFE:
                    return new TrackLoopAnalysis(track.TrackId, false, 0, 0, tick);
                default:
                    if (cmd < 0xF0)
                    {
                        tick += Math.Max(e.DelayTicks, 0);
                    }

                    ip++;
                    break;
            }
        }

        return new TrackLoopAnalysis(track.TrackId, false, 0, 0, tick);
    }

    private static bool TryResolveRepeatDestinationForAnalysis(
        IReadOnlyList<RcpEvent> events,
        IReadOnlyList<int> measureStartIndices,
        IReadOnlyDictionary<int, int> eventIndexByTrackOffset,
        int trackHeaderSize,
        int trackEventSize,
        int commandIp,
        out int measureId,
        out int destinationIp,
        out int returnIp)
    {
        measureId = -1;
        destinationIp = -1;
        returnIp = commandIp + 1;
        if (commandIp < 0 || commandIp >= events.Count)
        {
            return false;
        }

        var e = events[commandIp];
        if (e.CommandOrNote != 0xFC)
        {
            return false;
        }

        measureId = GetRepeatMeasureId(e);
        if (e.RawLength < 6)
        {
            var repeatOffset = ((e.Param1 & ~0x03) & 0xFF) | ((e.Param2 & 0xFF) << 8);
            if (repeatOffset >= trackHeaderSize && eventIndexByTrackOffset.TryGetValue(repeatOffset, out var fromOffset))
            {
                destinationIp = fromOffset;
            }
            else if (repeatOffset >= trackHeaderSize &&
                     (repeatOffset - trackHeaderSize) % trackEventSize == 0)
            {
                var fromLinear = (repeatOffset - trackHeaderSize) / trackEventSize;
                if (fromLinear >= 0 && fromLinear < events.Count)
                {
                    destinationIp = fromLinear;
                }
            }
        }
        else
        {
            var commandId = e.Param1;
            if (commandId >= 0x30)
            {
                var repeatOffsetLong = (long)trackHeaderSize + ((long)commandId - 0x30L) * trackEventSize;
                if (repeatOffsetLong >= 0 && repeatOffsetLong <= int.MaxValue)
                {
                    var repeatOffset = (int)repeatOffsetLong;
                    if (repeatOffset >= trackHeaderSize && eventIndexByTrackOffset.TryGetValue(repeatOffset, out var fromOffset))
                    {
                        destinationIp = fromOffset;
                    }
                    else if (repeatOffset >= trackHeaderSize &&
                             (repeatOffset - trackHeaderSize) % trackEventSize == 0)
                    {
                        var fromLinear = (repeatOffset - trackHeaderSize) / trackEventSize;
                        if (fromLinear >= 0 && fromLinear < events.Count)
                        {
                            destinationIp = fromLinear;
                        }
                    }
                }
            }
        }

        if (destinationIp < 0 && measureId >= 0 && measureId < measureStartIndices.Count)
        {
            destinationIp = measureStartIndices[measureId];
        }

        return true;
    }

    private static int GetTrackHeaderSize(RcpFormat format)
    {
        return format == RcpFormat.G36 ? 0x2E : 0x2C;
    }

    private static int GetDefaultEventSize(RcpFormat format)
    {
        return format == RcpFormat.G36 ? 6 : 4;
    }

    private static IReadOnlyDictionary<int, int> BuildTrackOffsetLookup(
        IReadOnlyList<RcpEvent> events,
        int headerSize,
        int defaultEventSize)
    {
        var map = new Dictionary<int, int>(events.Count);
        var offset = headerSize;
        for (var i = 0; i < events.Count; i++)
        {
            if (!map.ContainsKey(offset))
            {
                map[offset] = i;
            }

            var eventSize = events[i].RawLength > 0 ? events[i].RawLength : defaultEventSize;
            offset += Math.Max(eventSize, 1);
        }

        return map;
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
        var startTick = track.StartTick;
        if (startTick > 0)
        {
            tick = startTick;
            startTick = 0;
        }

        // rcpcv still emits MIDI for tracks flagged as "dummy channel" in some files.
        // Start enabled and let explicit E6 channel-state commands control suppression.
        var midiEnabled = true;
        var ip = 0;
        var expandedEventCount = 0;

        var rolandDev = 0x10;
        var rolandModel = 0x16;
        var rolandBaseH = 0x10;
        var rolandBaseM = 0x00;

        var yamahaDev = 0x10;
        var yamahaModel = 0x4C;
        var yamahaBaseH = 0x00;
        var yamahaBaseM = 0x00;

        var loops = new Stack<LoopFrame>();
        var repeatMeasureReturnIps = new Stack<int>();
        var measureStartIndices = BuildMeasureStartIndices(events);
        var trackHeaderSize = GetTrackHeaderSize(song.Format);
        var trackEventSize = GetDefaultEventSize(song.Format);
        var eventIndexByTrackOffset = BuildTrackOffsetLookup(events, trackHeaderSize, trackEventSize);
        var primaryLoopStartIndex = FindPrimaryLoopStartIndex(
            events,
            measureStartIndices,
            eventIndexByTrackOffset,
            trackHeaderSize,
            trackEventSize,
            options.TreatHighLoopCountAsInfinite);
        var activeNotes = new Dictionary<int, ActiveNoteState>();
        var rcpcvDrumSetupDetected = false;
        var rcpcvDrumSetupReverbCount = 0;
        var rcpcvDropNextDrumSetupDataEntry = false;
        var rcpcvPreviousBarDelay = 0;
        var rcpcvLongSysExSeen = false;

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
            var rcpcvBarDelayForThisEvent = rcpcvPreviousBarDelay;
            rcpcvPreviousBarDelay = 0;
            var isVelocityZeroNote = cmd < 0x80 && options.MergeActiveSameNote && ToByte(e.Param2) == 0;
            if (!options.KeepBoundaryActiveSameNote && !isVelocityZeroNote)
            {
                // Reference conversion mode: emit note-offs due at this tick before
                // handling any command at the same tick.
                PruneExpiredNotes(activeNotes, tick, false);
            }

            if (cmd < 0x80)
            {
                var note = (byte)((cmd + track.TrackTransposition) & 0x7F);
                var velocity = ToByte(e.Param2);
                var gate = e.Param1;
                if (options.MergeActiveSameNote)
                {
                    var keepBoundaryForThisEvent = options.KeepBoundaryActiveSameNote || velocity == 0;
                    PruneExpiredNotes(activeNotes, tick, keepBoundaryForThisEvent);
                }

                if (midiEnabled)
                {
                    if (velocity == 0)
                    {
                        if (!options.TreatVelocityZeroNoteAsNoteOff)
                        {
                            ApplyStartTickDelay(ref tick, ref startTick, e.DelayTicks);
                            ip++;
                            continue;
                        }

                        var noteKey = BuildActiveNoteKey(channel, note);
                        if (options.MergeActiveSameNote && activeNotes.TryGetValue(noteKey, out var active))
                        {
                            var velocityZeroOffTick = tick;
                            if (options.RcpcvCompatBarDelayBeforeVelocityZeroNote &&
                                rcpcvBarDelayForThisEvent > 0)
                            {
                                velocityZeroOffTick += rcpcvBarDelayForThisEvent;
                            }

                            // Velocity=0 note is treated as immediate note-off of the same pitch.
                            // Update the pending off tick (shorten or extend) to this event tick.
                            if (velocityZeroOffTick != active.OffTick)
                            {
                                var oldOffEvent = midiEvents[active.OffEventIndex];
                                midiEvents[active.OffEventIndex] = new ScheduledMidiEvent
                                {
                                    Tick = velocityZeroOffTick,
                                    SourceTrackId = oldOffEvent.SourceTrackId,
                                    SourceEventIndex = oldOffEvent.SourceEventIndex,
                                    SourceCommand = oldOffEvent.SourceCommand,
                                    Packet = oldOffEvent.Packet
                                };
                                activeNotes[noteKey] = new ActiveNoteState(active.OffEventIndex, velocityZeroOffTick);
                            }
                        }
                        ApplyStartTickDelay(ref tick, ref startTick, e.DelayTicks);
                        ip++;
                        continue;
                    }

                    if (gate <= 0)
                    {
                        ApplyStartTickDelay(ref tick, ref startTick, e.DelayTicks);
                        ip++;
                        continue;
                    }

                    var offTick = tick + gate;
                    if (options.MergeActiveSameNote)
                    {
                        var noteKey = BuildActiveNoteKey(channel, note);
                        if (activeNotes.TryGetValue(noteKey, out var active))
                        {
                            // App playback mode: retriggering the same active note extends its length.
                            var oldOffEvent = midiEvents[active.OffEventIndex];
                            midiEvents[active.OffEventIndex] = new ScheduledMidiEvent
                            {
                                Tick = offTick,
                                SourceTrackId = oldOffEvent.SourceTrackId,
                                SourceEventIndex = oldOffEvent.SourceEventIndex,
                                SourceCommand = oldOffEvent.SourceCommand,
                                Packet = oldOffEvent.Packet
                            };
                            activeNotes[noteKey] = new ActiveNoteState(active.OffEventIndex, offTick);
                        }
                        else
                        {
                            midiEvents.Add(new ScheduledMidiEvent
                            {
                                Tick = tick,
                                SourceTrackId = track.TrackId,
                                SourceEventIndex = ip,
                                SourceCommand = e.CommandOrNote,
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
                                SourceTrackId = track.TrackId,
                                SourceEventIndex = ip,
                                SourceCommand = e.CommandOrNote,
                                Packet = new MidiEventPacket
                                {
                                    Kind = MidiMessageKind.Short,
                                    ShortMessage = PackShort((byte)(0x90 | channel), note, 0),
                                    SysExData = null
                                }
                            });
                            activeNotes[noteKey] = new ActiveNoteState(midiEvents.Count - 1, offTick);
                        }
                    }
                    else
                    {
                        // Reference-compat mode: allow overlapping same-pitch notes.
                        midiEvents.Add(new ScheduledMidiEvent
                        {
                            Tick = tick,
                            SourceTrackId = track.TrackId,
                            SourceEventIndex = ip,
                            SourceCommand = e.CommandOrNote,
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
                            SourceTrackId = track.TrackId,
                            SourceEventIndex = ip,
                            SourceCommand = e.CommandOrNote,
                            Packet = new MidiEventPacket
                            {
                                Kind = MidiMessageKind.Short,
                                ShortMessage = PackShort((byte)(0x90 | channel), note, 0),
                                SysExData = null
                            }
                        });
                    }
                }

                ApplyStartTickDelay(ref tick, ref startTick, e.DelayTicks);
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
                    if (midiEnabled && index < song.UserExclusives.Count)
                    {
                        var body = song.UserExclusives[index].DataWithoutLeadingF0;
                        var ex = ExpandSysExTemplate(body, e.Param1 & 0xFF, e.Param2 & 0xFF, channel);
                        if (ShouldEmitSysEx(ex, options.EmitEmptySysEx))
                        {
                            midiEvents.Add(NewSysExEvent(tick, ex));
                        }
                    }

                    ApplyStartTickDelay(ref tick, ref startTick, e.DelayTicks);
                    ip++;
                    break;
                }
                case 0x98:
                {
                    var raw = new List<byte>();
                    var isG36Event = e.RawLength >= 6;
                    var localIp = ip + 1;
                    while (localIp < events.Count && events[localIp].CommandOrNote == 0xF7)
                    {
                        AppendContinuationBytes(raw, events[localIp], isG36Event);
                        localIp++;
                    }

                    if (midiEnabled && (!options.RcpcvCompatDropFollowingSysExAfterLongPayload || !rcpcvLongSysExSeen))
                    {
                        var bytes = raw.ToArray();
                        var ex = ExpandSysExTemplate(bytes, e.Param1 & 0xFF, e.Param2 & 0xFF, channel);
                        if (options.RcpcvCompatDropFollowingSysExAfterLongPayload && ex.Length > 256)
                        {
                            rcpcvLongSysExSeen = true;
                        }

                        if (ShouldEmitSysEx(ex, options.EmitEmptySysEx))
                        {
                            midiEvents.Add(NewSysExEvent(tick, ex));
                        }
                    }

                    ApplyStartTickDelay(ref tick, ref startTick, e.DelayTicks);
                    ip = localIp;
                    break;
                }
                case 0x99:
                    // External command (MCI/RUN/...) does not emit MIDI in realtime playback.
                    ApplyStartTickDelay(ref tick, ref startTick, e.DelayTicks);
                    ip = SkipContinuationEvents(events, ip + 1);
                    break;
                case 0xE1:
                    if (midiEnabled)
                    {
                        midiEvents.Add(NewShortEvent(tick, 0xB0 | channel, 32, e.Param2));
                        midiEvents.Add(NewShortEvent(tick, 0xC0 | channel, e.Param1, 0));
                    }

                    ApplyStartTickDelay(ref tick, ref startTick, e.DelayTicks);
                    ip++;
                    break;
                case 0xE2:
                    if (midiEnabled)
                    {
                        midiEvents.Add(NewShortEvent(tick, 0xB0 | channel, 0, e.Param2));
                        midiEvents.Add(NewShortEvent(tick, 0xB0 | channel, 32, 0));
                        midiEvents.Add(NewShortEvent(tick, 0xC0 | channel, e.Param1, 0));
                    }

                    ApplyStartTickDelay(ref tick, ref startTick, e.DelayTicks);
                    ip++;
                    break;
                case 0xE5:
                    // Key scan command (UI control in original Recomposer); keep timing only.
                    ApplyStartTickDelay(ref tick, ref startTick, e.DelayTicks);
                    ip++;
                    break;
                case 0xEA:
                    if (midiEnabled)
                    {
                        midiEvents.Add(NewShortEvent(tick, 0xD0 | channel, e.Param1, 0));
                    }

                    ApplyStartTickDelay(ref tick, ref startTick, e.DelayTicks);
                    ip++;
                    break;
                case 0xEB:
                    if (options.RcpcvCompatTerminateDrumSetupAtReverbComment &&
                        channel is 9 or 10 &&
                        rcpcvDropNextDrumSetupDataEntry &&
                        e.Param1 == 6)
                    {
                        rcpcvDropNextDrumSetupDataEntry = false;
                        ApplyStartTickDelay(ref tick, ref startTick, e.DelayTicks);
                        ip++;
                        break;
                    }

                    if (midiEnabled)
                    {
                        midiEvents.Add(NewShortEvent(tick, 0xB0 | channel, e.Param1, e.Param2));
                    }

                    ApplyStartTickDelay(ref tick, ref startTick, e.DelayTicks);
                    ip++;
                    break;
                case 0xEC:
                    if (midiEnabled)
                    {
                        if (e.Param1 < 0x80)
                        {
                            midiEvents.Add(NewShortEvent(tick, 0xC0 | channel, e.Param1, 0));
                        }
                        else if (!options.RcdStrictMode && e.Param1 < 0xC0 && channel is >= 1 and < 9)
                        {
                            var mt32 = BuildMt32PatchChangeSysEx(channel, e.Param1);
                            midiEvents.Add(NewSysExEvent(tick, mt32));
                        }
                    }

                    ApplyStartTickDelay(ref tick, ref startTick, e.DelayTicks);
                    ip++;
                    break;
                case 0xED:
                    if (midiEnabled)
                    {
                        midiEvents.Add(NewShortEvent(tick, 0xA0 | channel, e.Param1, e.Param2));
                    }

                    ApplyStartTickDelay(ref tick, ref startTick, e.DelayTicks);
                    ip++;
                    break;
                case 0xEE:
                    if (midiEnabled)
                    {
                        midiEvents.Add(NewShortEvent(tick, 0xE0 | channel, e.Param1, e.Param2, track.TrackId, ip, e.CommandOrNote));
                    }

                    ApplyStartTickDelay(ref tick, ref startTick, e.DelayTicks);
                    ip++;
                    break;
                case 0xE6:
                {
                    DecodeCommandChannelState(e.Param1, options.RcdStrictMode, out midiEnabled, out channel);
                    ApplyStartTickDelay(ref tick, ref startTick, e.DelayTicks);
                    ip++;
                    break;
                }
                case 0xE7:
                {
                    tempoModifiers.Add(new TempoModifierCommand(
                        Tick: tick,
                        Ratio: Math.Max(1, e.Param1),
                        Gradation: 0));
                    ApplyStartTickDelay(ref tick, ref startTick, e.DelayTicks);
                    ip++;
                    break;
                }
                case 0xC0:
                    if (midiEnabled)
                    {
                        midiEvents.Add(NewSysExEvent(tick, [0xF0, 0x43, (byte)(0x10 + channel), 0x08, ToByte(e.Param1), ToByte(e.Param2), 0xF7]));
                    }

                    ApplyStartTickDelay(ref tick, ref startTick, e.DelayTicks);
                    ip++;
                    break;
                case 0xC1:
                    if (midiEnabled)
                    {
                        midiEvents.Add(NewSysExEvent(tick, [0xF0, 0x43, (byte)(0x10 + channel), 0x00, ToByte(e.Param1), ToByte(e.Param2), 0xF7]));
                    }

                    ApplyStartTickDelay(ref tick, ref startTick, e.DelayTicks);
                    ip++;
                    break;
                case 0xC2:
                    if (midiEnabled)
                    {
                        midiEvents.Add(NewSysExEvent(tick, [0xF0, 0x43, (byte)(0x10 + channel), 0x04, ToByte(e.Param1), ToByte(e.Param2), 0xF7]));
                    }

                    ApplyStartTickDelay(ref tick, ref startTick, e.DelayTicks);
                    ip++;
                    break;
                case 0xC3:
                    if (midiEnabled)
                    {
                        midiEvents.Add(NewSysExEvent(tick, [0xF0, 0x43, (byte)(0x10 + channel), 0x11, ToByte(e.Param1), ToByte(e.Param2), 0xF7]));
                    }

                    ApplyStartTickDelay(ref tick, ref startTick, e.DelayTicks);
                    ip++;
                    break;
                case 0xC5:
                    if (midiEnabled)
                    {
                        midiEvents.Add(NewSysExEvent(tick, [0xF0, 0x43, (byte)(0x10 + channel), 0x15, ToByte(e.Param1), ToByte(e.Param2), 0xF7]));
                    }

                    ApplyStartTickDelay(ref tick, ref startTick, e.DelayTicks);
                    ip++;
                    break;
                case 0xC6:
                    if (midiEnabled)
                    {
                        midiEvents.Add(NewSysExEvent(tick, [0xF0, 0x43, 0x75, (byte)channel, 0x10, ToByte(e.Param1), ToByte(e.Param2), 0xF7]));
                    }

                    ApplyStartTickDelay(ref tick, ref startTick, e.DelayTicks);
                    ip++;
                    break;
                case 0xC7:
                    if (midiEnabled)
                    {
                        midiEvents.Add(NewSysExEvent(tick, [0xF0, 0x43, (byte)(0x10 + channel), 0x12, ToByte(e.Param1), ToByte(e.Param2), 0xF7]));
                    }

                    ApplyStartTickDelay(ref tick, ref startTick, e.DelayTicks);
                    ip++;
                    break;
                case 0xC8:
                    if (midiEnabled)
                    {
                        midiEvents.Add(NewSysExEvent(tick, [0xF0, 0x43, (byte)(0x10 + channel), 0x13, ToByte(e.Param1), ToByte(e.Param2), 0xF7]));
                    }

                    ApplyStartTickDelay(ref tick, ref startTick, e.DelayTicks);
                    ip++;
                    break;
                case 0xC9:
                    if (midiEnabled)
                    {
                        midiEvents.Add(NewSysExEvent(tick, [0xF0, 0x43, (byte)(0x10 + channel), 0x10, ToByte(e.Param1), ToByte(e.Param2), 0xF7]));
                    }

                    ApplyStartTickDelay(ref tick, ref startTick, e.DelayTicks);
                    ip++;
                    break;
                case 0xCA:
                    if (midiEnabled)
                    {
                        midiEvents.Add(NewSysExEvent(tick, [0xF0, 0x43, (byte)(0x10 + channel), 0x10, 0x7B, ToByte(e.Param1), ToByte(e.Param2), 0xF7]));
                    }

                    ApplyStartTickDelay(ref tick, ref startTick, e.DelayTicks);
                    ip++;
                    break;
                case 0xCB:
                    if (midiEnabled)
                    {
                        midiEvents.Add(NewSysExEvent(tick, [0xF0, 0x43, (byte)(0x10 + channel), 0x10, 0x7C, ToByte(e.Param1), ToByte(e.Param2), 0xF7]));
                    }

                    ApplyStartTickDelay(ref tick, ref startTick, e.DelayTicks);
                    ip++;
                    break;
                case 0xCC:
                    if (midiEnabled)
                    {
                        midiEvents.Add(NewSysExEvent(tick, [0xF0, 0x43, (byte)(0x10 + channel), 0x1B, ToByte(e.Param1), ToByte(e.Param2), 0xF7]));
                    }

                    ApplyStartTickDelay(ref tick, ref startTick, e.DelayTicks);
                    ip++;
                    break;
                case 0xCD:
                    if (midiEnabled)
                    {
                        midiEvents.Add(NewSysExEvent(tick, [0xF0, 0x43, (byte)(0x10 + channel), 0x18, ToByte(e.Param1), ToByte(e.Param2), 0xF7]));
                    }

                    ApplyStartTickDelay(ref tick, ref startTick, e.DelayTicks);
                    ip++;
                    break;
                case 0xCE:
                    if (midiEnabled)
                    {
                        midiEvents.Add(NewSysExEvent(tick, [0xF0, 0x43, (byte)(0x10 + channel), 0x19, ToByte(e.Param1), ToByte(e.Param2), 0xF7]));
                    }

                    ApplyStartTickDelay(ref tick, ref startTick, e.DelayTicks);
                    ip++;
                    break;
                case 0xCF:
                    if (midiEnabled)
                    {
                        midiEvents.Add(NewSysExEvent(tick, [0xF0, 0x43, (byte)(0x10 + channel), 0x1A, ToByte(e.Param1), ToByte(e.Param2), 0xF7]));
                    }

                    ApplyStartTickDelay(ref tick, ref startTick, e.DelayTicks);
                    ip++;
                    break;
                case 0xD0:
                    yamahaBaseH = e.Param1 & 0xFF;
                    yamahaBaseM = e.Param2 & 0xFF;
                    ApplyStartTickDelay(ref tick, ref startTick, e.DelayTicks);
                    ip++;
                    break;
                case 0xD1:
                    yamahaDev = e.Param1 & 0xFF;
                    yamahaModel = e.Param2 & 0xFF;
                    ApplyStartTickDelay(ref tick, ref startTick, e.DelayTicks);
                    ip++;
                    break;
                case 0xD2:
                    if (midiEnabled)
                    {
                        midiEvents.Add(NewSysExEvent(tick, new byte[]
                        {
                            0xF0, 0x43, (byte)yamahaDev, (byte)yamahaModel,
                            (byte)yamahaBaseH, (byte)yamahaBaseM, ToByte(e.Param1), ToByte(e.Param2), 0xF7
                        }));
                    }

                    ApplyStartTickDelay(ref tick, ref startTick, e.DelayTicks);
                    ip++;
                    break;
                case 0xD3:
                    if (midiEnabled)
                    {
                        midiEvents.Add(NewSysExEvent(tick, new byte[]
                        {
                            0xF0, 0x43, 0x10, 0x4C,
                            (byte)yamahaBaseH, (byte)yamahaBaseM, ToByte(e.Param1), ToByte(e.Param2), 0xF7
                        }));
                    }

                    ApplyStartTickDelay(ref tick, ref startTick, e.DelayTicks);
                    ip++;
                    break;
                case 0xDC:
                    if (midiEnabled)
                    {
                        midiEvents.Add(NewSysExEvent(tick, [0xF0, 0x41, 0x32, (byte)channel, ToByte(e.Param1), ToByte(e.Param2), 0xF7]));
                    }

                    ApplyStartTickDelay(ref tick, ref startTick, e.DelayTicks);
                    ip++;
                    break;
                case 0xDD:
                    rolandBaseH = e.Param1 & 0xFF;
                    rolandBaseM = e.Param2 & 0xFF;
                    ApplyStartTickDelay(ref tick, ref startTick, e.DelayTicks);
                    ip++;
                    break;
                case 0xDE:
                {
                    var addrL = e.Param1 & 0xFF;
                    var parameter = e.Param2 & 0xFF;
                    var sum = rolandBaseH + rolandBaseM + addrL + parameter;
                    var check = (128 - (sum & 0x7F)) & 0x7F;
                    if (midiEnabled &&
                        (!options.RcpcvCompatDropFollowingSysExAfterLongPayload || !rcpcvLongSysExSeen))
                    {
                        var ex = new byte[]
                        {
                            0xF0, 0x41, (byte)rolandDev, (byte)rolandModel, 0x12,
                            (byte)rolandBaseH, (byte)rolandBaseM, (byte)addrL, (byte)parameter, (byte)check, 0xF7
                        };
                        if (options.RcpcvCompatDropFollowingSysExAfterLongPayload && ex.Length > 256)
                        {
                            rcpcvLongSysExSeen = true;
                        }

                        midiEvents.Add(NewSysExEvent(tick, ex));
                    }

                    ApplyStartTickDelay(ref tick, ref startTick, e.DelayTicks);
                    ip++;
                    break;
                }
                case 0xDF:
                    rolandDev = e.Param1 & 0xFF;
                    rolandModel = e.Param2 & 0xFF;
                    ApplyStartTickDelay(ref tick, ref startTick, e.DelayTicks);
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
                    var isInfiniteLoop = IsInfiniteLoopRepeatCount(e.DelayTicks, options.TreatHighLoopCountAsInfinite);
                    var repeatCount = isInfiniteLoop ? Math.Max(options.InfinityLoopCount, 1) : e.DelayTicks;
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
                    if (options.RcpcvCompatTerminateDrumSetupAtReverbComment &&
                        channel is 9 or 10 &&
                        rcpcvDrumSetupDetected)
                    {
                        if (!options.EmitPendingNoteOffsAfterTrackEnd && options.MergeActiveSameNote && activeNotes.Count > 0)
                        {
                            RemoveActiveNoteOffEvents(midiEvents, activeNotes);
                        }

                        return;
                    }

                    // Bar/measure separator event seen in many RCP files.
                    // It does not emit MIDI and does not advance timeline.
                    var hadRepeatReturn = repeatMeasureReturnIps.Count > 0;
                    ip = hadRepeatReturn
                        ? repeatMeasureReturnIps.Pop()
                        : ip + 1;
                    if (options.RcpcvCompatBarDelayBeforeVelocityZeroNote && !hadRepeatReturn)
                    {
                        rcpcvPreviousBarDelay = Math.Max(e.DelayTicks, 0);
                    }
                    break;
                case 0xFC:
                {
                    if (repeatMeasureReturnIps.Count > 0)
                    {
                        ip = repeatMeasureReturnIps.Pop();
                        break;
                    }

                    var chainIp = ip;
                    var parentIp = -1;
                    var resolved = false;
                    for (var chainGuard = 0; chainGuard < 256; chainGuard++)
                    {
                        if (!TryResolveRepeatDestination(
                                events,
                                measureStartIndices,
                                eventIndexByTrackOffset,
                                trackHeaderSize,
                                trackEventSize,
                                chainIp,
                                out var measureId,
                                out var destination,
                                out var returnIp))
                        {
                            ip = chainIp + 1;
                            if (parentIp >= 0 && repeatMeasureReturnIps.Count < 64)
                            {
                                repeatMeasureReturnIps.Push(parentIp);
                            }

                            resolved = true;
                            break;
                        }

                        if (measureId >= measureStartIndices.Count ||
                            destination < 0 ||
                            destination >= events.Count ||
                            destination == chainIp)
                        {
                            ip = returnIp;
                            if (parentIp >= 0 && repeatMeasureReturnIps.Count < 64)
                            {
                                repeatMeasureReturnIps.Push(parentIp);
                            }

                            resolved = true;
                            break;
                        }

                        if (parentIp < 0)
                        {
                            parentIp = returnIp;
                        }

                        chainIp = destination;
                        if (events[chainIp].CommandOrNote != 0xFC)
                        {
                            if (parentIp >= 0 && repeatMeasureReturnIps.Count < 64)
                            {
                                repeatMeasureReturnIps.Push(parentIp);
                            }

                            ip = chainIp;
                            resolved = true;
                            break;
                        }

                        if (chainGuard == 255)
                        {
                            ip = parentIp >= 0 ? parentIp : ip + 1;
                            resolved = true;
                        }
                    }

                    if (!resolved)
                    {
                        ip++;
                    }

                    break;
                }
                case 0xF7:
                    // Continuation event handled by commands 98/99/F6; ignore standalone.
                    ip++;
                    break;
                case 0xF6:
                    if (options.RcpcvCompatTerminateDrumSetupAtReverbComment && channel is 9 or 10)
                    {
                        if (IsReverbCommentBlock(events, ip))
                        {
                            rcpcvDrumSetupDetected = true;
                            rcpcvDrumSetupReverbCount++;
                            if (rcpcvDrumSetupReverbCount == 5)
                            {
                                rcpcvDropNextDrumSetupDataEntry = true;
                            }
                        }
                        else if (rcpcvDrumSetupDetected &&
                                 rcpcvDrumSetupReverbCount >= 5 &&
                                 IsChorusCommentBlock(events, ip))
                        {
                            if (!options.EmitPendingNoteOffsAfterTrackEnd && options.MergeActiveSameNote && activeNotes.Count > 0)
                            {
                                RemoveActiveNoteOffEvents(midiEvents, activeNotes);
                            }

                            return;
                        }
                    }

                    // Comment command has no realtime MIDI effect.
                    ip = SkipContinuationEvents(events, ip + 1);
                    break;
                case 0xF5:
                    // Key signature command is metadata for SMF conversion only.
                    ip++;
                    break;
                case 0xFE:
                    if (!options.EmitPendingNoteOffsAfterTrackEnd && options.MergeActiveSameNote && activeNotes.Count > 0)
                    {
                        RemoveActiveNoteOffEvents(midiEvents, activeNotes);
                    }

                    return;
                default:
                    if (cmd >= 0x80)
                    {
                        RecordUnsupportedCommand(unsupportedCommands, cmd, track.TrackId);
                    }

                    if (cmd < 0xF0)
                    {
                        ApplyStartTickDelay(ref tick, ref startTick, e.DelayTicks);
                    }

                    ip++;
                    break;
            }
        }
    }

    private static byte[] ExpandSysExTemplate(byte[] raw, int p1, int p2, int channel)
    {
        var result = new List<byte>(raw.Length + 2) { 0xF0 };
        var checksum = 0;

        foreach (var token in raw)
        {
            var value = (int)token;
            if ((value & 0x80) != 0)
            {
                switch (value)
                {
                    case 0x80:
                        value = p1 & 0xFF;
                        break;
                    case 0x81:
                        value = p2 & 0xFF;
                        break;
                    case 0x82:
                        value = channel & 0xFF;
                        break;
                    case 0x83:
                        checksum = 0;
                        continue;
                    case 0x84:
                        value = (0x100 - checksum) & 0x7F;
                        break;
                    case 0xF7:
                        result.Add(0xF7);
                        return result.ToArray();
                    default:
                        continue;
                }
            }

            if ((value & 0x80) == 0)
            {
                result.Add((byte)value);
                checksum = (checksum + value) & 0xFF;
            }
        }

        if (result[^1] != 0xF7)
        {
            result.Add(0xF7);
        }

        return result.ToArray();
    }

    private static bool ShouldEmitSysEx(byte[] ex, bool emitEmptySysEx)
    {
        return emitEmptySysEx ? ex.Length >= 2 : ex.Length > 2;
    }

    private static void DecodeCommandChannelState(int value, bool rcdStrictMode, out bool midiEnabled, out int channel)
    {
        var decoded = (value - 1) & 0xFF;
        if (rcdStrictMode)
        {
            if (decoded >= 32)
            {
                midiEnabled = false;
                channel = 0;
                return;
            }

            midiEnabled = true;
            channel = ClampChannel(decoded & 0x0F);
            return;
        }

        if ((decoded & 0x80) != 0)
        {
            midiEnabled = false;
            channel = 0;
            return;
        }

        midiEnabled = true;
        channel = ClampChannel(decoded & 0x0F);
    }

    private static bool IsInfiniteLoopRepeatCount(int repeatCount, bool treatHighLoopCountAsInfinite)
    {
        return repeatCount <= 0 || (treatHighLoopCountAsInfinite && repeatCount >= 0x7F);
    }

    private static long GetGlobalStartTickOffset(RcpSong song)
    {
        var hasTrack = false;
        var minStart = 0;
        foreach (var track in song.Tracks)
        {
            if (!hasTrack)
            {
                minStart = track.StartTick;
                hasTrack = true;
                continue;
            }

            if (track.StartTick < minStart)
            {
                minStart = track.StartTick;
            }
        }

        if (!hasTrack || minStart >= 0)
        {
            return 0;
        }

        return -minStart;
    }

    private static IReadOnlyList<ScheduledMidiEvent> ShiftMidiTicks(IReadOnlyList<ScheduledMidiEvent> events, long tickOffset)
    {
        if (tickOffset <= 0 || events.Count == 0)
        {
            return events;
        }

        var shifted = new List<ScheduledMidiEvent>(events.Count);
        foreach (var e in events)
        {
            shifted.Add(new ScheduledMidiEvent
            {
                Tick = e.Tick + tickOffset,
                SourceTrackId = e.SourceTrackId,
                SourceEventIndex = e.SourceEventIndex,
                SourceCommand = e.SourceCommand,
                Packet = e.Packet
            });
        }

        return shifted;
    }

    private static IReadOnlyList<TempoEvent> ShiftTempoTicks(IReadOnlyList<TempoEvent> events, long tickOffset)
    {
        if (tickOffset <= 0 || events.Count == 0)
        {
            return events;
        }

        var shifted = new List<TempoEvent>(events.Count);
        foreach (var e in events)
        {
            shifted.Add(new TempoEvent
            {
                Tick = e.Tick + tickOffset,
                Bpm = e.Bpm
            });
        }

        return shifted;
    }

    private static void ApplyStartTickDelay(ref long tick, ref int startTick, int delayTicks)
    {
        var delay = Math.Max(delayTicks, 0);
        if (startTick < 0 && delay > 0)
        {
            startTick += delay;
            if (startTick < 0)
            {
                return;
            }

            delay = startTick;
            startTick = 0;
        }

        tick += delay;
    }

    private static byte[] BuildMt32PatchChangeSysEx(int channel, int patch)
    {
        var partMemOfs = (channel - 1) << 4;
        var data = new byte[]
        {
            (byte)((patch >> 6) & 0x03),
            (byte)(patch & 0x3F),
            0x18,
            0x32,
            0x0C,
            0x00,
            0x01
        };

        var addrH = 0x03;
        var addrM = 0x00;
        var addrL = partMemOfs & 0x7F;
        var sum = addrH + addrM + addrL;
        for (var i = 0; i < data.Length; i++)
        {
            sum += data[i];
        }

        var checksum = (0x100 - sum) & 0x7F;
        return
        [
            0xF0,
            0x41,
            0x10,
            0x16,
            0x12,
            (byte)addrH,
            (byte)addrM,
            (byte)addrL,
            ..data,
            (byte)checksum,
            0xF7
        ];
    }

    private static int Clamp7Bit(int value)
    {
        return Math.Clamp(value, 0, 127);
    }

    private static IReadOnlyList<ScheduledMidiEvent> ApplyRcpcvSysExPerTickCap(
        IReadOnlyList<ScheduledMidiEvent> orderedMidiEvents,
        int maxSysExPerTick)
    {
        if (maxSysExPerTick <= 0 || orderedMidiEvents.Count == 0)
        {
            return orderedMidiEvents;
        }

        var filtered = new List<ScheduledMidiEvent>(orderedMidiEvents.Count);
        var currentTick = long.MinValue;
        var sysexCount = 0;

        foreach (var e in orderedMidiEvents)
        {
            if (e.Tick != currentTick)
            {
                currentTick = e.Tick;
                sysexCount = 0;
            }

            if (e.Packet.Kind == MidiMessageKind.SysEx)
            {
                sysexCount++;
                if (sysexCount > maxSysExPerTick)
                {
                    continue;
                }
            }

            filtered.Add(e);
        }

        return filtered;
    }

    private static IReadOnlyList<ScheduledMidiEvent> ApplyRcpcvSameTickSysExDedup(
        IReadOnlyList<ScheduledMidiEvent> orderedMidiEvents,
        bool enabled)
    {
        if (!enabled || orderedMidiEvents.Count == 0)
        {
            return orderedMidiEvents;
        }

        var deduped = new List<ScheduledMidiEvent>(orderedMidiEvents.Count);
        var currentTick = long.MinValue;
        var seenSysEx = new List<byte[]>(16);
        foreach (var e in orderedMidiEvents)
        {
            if (e.Tick != currentTick)
            {
                currentTick = e.Tick;
                seenSysEx.Clear();
            }

            if (e.Packet.Kind == MidiMessageKind.SysEx &&
                e.Packet.SysExData is { Length: > 0 } data)
            {
                var duplicate = false;
                foreach (var prior in seenSysEx)
                {
                    if (data.AsSpan().SequenceEqual(prior))
                    {
                        duplicate = true;
                        break;
                    }
                }

                if (duplicate)
                {
                    continue;
                }

                seenSysEx.Add(data);
            }

            deduped.Add(e);
        }

        return deduped;
    }

    private static void ApplySysExPayloadCap(List<ScheduledMidiEvent> midiEvents, int maxPayloadLength)
    {
        if (midiEvents.Count == 0 || maxPayloadLength <= 0)
        {
            return;
        }

        for (var i = 0; i < midiEvents.Count; i++)
        {
            var e = midiEvents[i];
            if (e.Packet.Kind != MidiMessageKind.SysEx || e.Packet.SysExData is not { Length: > 0 } data)
            {
                continue;
            }

            var capped = CapSysExPayload(data, maxPayloadLength);
            if (ReferenceEquals(capped, data))
            {
                continue;
            }

            midiEvents[i] = new ScheduledMidiEvent
            {
                Tick = e.Tick,
                SourceTrackId = e.SourceTrackId,
                SourceEventIndex = e.SourceEventIndex,
                SourceCommand = e.SourceCommand,
                Packet = new MidiEventPacket
                {
                    Kind = MidiMessageKind.SysEx,
                    ShortMessage = 0,
                    SysExData = capped
                }
            };
        }
    }

    private static byte[] CapSysExPayload(byte[] data, int maxPayloadLength)
    {
        if (data.Length == 0 || data[0] != 0xF0)
        {
            return data;
        }

        var payloadLength = data.Length - 1;
        if (payloadLength <= maxPayloadLength)
        {
            return data;
        }

        var output = new byte[1 + maxPayloadLength];
        output[0] = 0xF0;
        Array.Copy(data, 1, output, 1, maxPayloadLength);
        if (output[^1] != 0xF7)
        {
            Array.Resize(ref output, output.Length + 1);
            output[^1] = 0xF7;
        }

        return output;
    }

    private static int Clamp8Bit(int value)
    {
        return Math.Clamp(value, 0, 255);
    }

    private static int ClampChannel(int channel)
    {
        return Math.Clamp(channel, 0, 15);
    }

    private static int BuildActiveNoteKey(int channel, int note)
    {
        var ch = ClampChannel(channel) & 0x0F;
        var n = Clamp7Bit(note) & 0x7F;
        return (ch << 7) | n;
    }

    private static byte ToByte(int value)
    {
        return (byte)(value & 0xFF);
    }

    private static void PruneExpiredNotes(
        Dictionary<int, ActiveNoteState> activeNotes,
        long tick,
        bool keepBoundaryActiveSameNote)
    {
        if (activeNotes.Count == 0)
        {
            return;
        }

        var expired = activeNotes
            .Where(kvp => keepBoundaryActiveSameNote
                ? kvp.Value.OffTick < tick
                : kvp.Value.OffTick <= tick)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var key in expired)
        {
            activeNotes.Remove(key);
        }
    }

    private static void RemoveActiveNoteOffEvents(
        List<ScheduledMidiEvent> midiEvents,
        Dictionary<int, ActiveNoteState> activeNotes)
    {
        if (activeNotes.Count == 0)
        {
            return;
        }

        var indices = activeNotes.Values
            .Select(v => v.OffEventIndex)
            .Distinct()
            .Where(i => i >= 0 && i < midiEvents.Count)
            .OrderByDescending(i => i)
            .ToList();

        foreach (var index in indices)
        {
            midiEvents.RemoveAt(index);
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

    private static bool IsReverbCommentBlock(IReadOnlyList<RcpEvent> events, int f6Index)
    {
        var text = ReadCommentBlockText(events, f6Index);
        return text.Contains("REVERB SEND", StringComparison.Ordinal);
    }

    private static bool IsChorusCommentBlock(IReadOnlyList<RcpEvent> events, int f6Index)
    {
        var text = ReadCommentBlockText(events, f6Index);
        return text.Contains("CHORUS SEND", StringComparison.Ordinal);
    }

    private static string ReadCommentBlockText(IReadOnlyList<RcpEvent> events, int f6Index)
    {
        var start = f6Index + 1;
        if (start >= events.Count || events[start].CommandOrNote != 0xF7)
        {
            return string.Empty;
        }

        var bytes = new List<byte>(32);
        var index = start;
        while (index < events.Count && events[index].CommandOrNote == 0xF7)
        {
            bytes.Add(ToByte(events[index].Param1));
            bytes.Add(ToByte(events[index].Param2));
            index++;
        }

        if (bytes.Count == 0)
        {
            return string.Empty;
        }

        return System.Text.Encoding.ASCII.GetString(bytes.ToArray()).ToUpperInvariant();
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

    private static int FindPrimaryLoopStartIndex(
        IReadOnlyList<RcpEvent> events,
        IReadOnlyList<int> measureStartIndices,
        IReadOnlyDictionary<int, int> eventIndexByTrackOffset,
        int trackHeaderSize,
        int trackEventSize,
        bool treatHighLoopCountAsInfinite)
    {
        var loopStack = new Stack<LoopScanFrame>();
        var repeatMeasureParentIp = -1;
        var ip = 0;
        var guard = 0;
        var guardLimit = Math.Max(events.Count * 64, 4096);

        while (ip >= 0 && ip < events.Count && guard < guardLimit)
        {
            guard++;
            var e = events[ip];
            switch (e.CommandOrNote)
            {
                case 0xF9:
                    loopStack.Push(new LoopScanFrame(ip + 1, repeatMeasureParentIp));
                    ip++;
                    break;
                case 0xF8:
                    if (loopStack.Count == 0)
                    {
                        ip++;
                        break;
                    }

                    var top = loopStack.Pop();
                    if (IsInfiniteLoopRepeatCount(e.DelayTicks, treatHighLoopCountAsInfinite))
                    {
                        return top.StartIndex;
                    }

                    var repeatCount = Math.Max(e.DelayTicks, 1);
                    if (top.Iteration + 1 < repeatCount)
                    {
                        top = top with { Iteration = top.Iteration + 1 };
                        loopStack.Push(top);
                        repeatMeasureParentIp = top.ParentIp;
                        ip = top.StartIndex;
                    }
                    else
                    {
                        repeatMeasureParentIp = top.ParentIp;
                        ip++;
                    }

                    break;
                case 0xFC:
                    if (repeatMeasureParentIp >= 0)
                    {
                        ip = repeatMeasureParentIp;
                        repeatMeasureParentIp = -1;
                        break;
                    }

                    var chainIp = ip;
                    var parentIp = -1;
                    for (var chainGuard = 0; chainGuard < 256; chainGuard++)
                    {
                        if (!TryResolveRepeatDestination(
                                events,
                                measureStartIndices,
                                eventIndexByTrackOffset,
                                trackHeaderSize,
                                trackEventSize,
                                chainIp,
                                out var measureId,
                                out var destination,
                                out var returnIp))
                        {
                            ip = chainIp + 1;
                            repeatMeasureParentIp = parentIp;
                            break;
                        }

                        if (measureId >= measureStartIndices.Count ||
                            destination < 0 ||
                            destination >= events.Count ||
                            destination == chainIp)
                        {
                            ip = returnIp;
                            repeatMeasureParentIp = parentIp;
                            break;
                        }

                        if (parentIp < 0)
                        {
                            parentIp = returnIp;
                        }

                        chainIp = destination;
                        if (events[chainIp].CommandOrNote != 0xFC)
                        {
                            ip = chainIp;
                            repeatMeasureParentIp = parentIp;
                            break;
                        }

                        if (chainGuard == 255)
                        {
                            ip = parentIp >= 0 ? parentIp : ip + 1;
                            repeatMeasureParentIp = -1;
                        }
                    }

                    break;
                case 0xFD:
                    if (repeatMeasureParentIp >= 0)
                    {
                        ip = repeatMeasureParentIp;
                        repeatMeasureParentIp = -1;
                    }
                    else
                    {
                        ip++;
                    }

                    break;
                case 0xFE:
                    return -1;
                default:
                    ip++;
                    break;
            }
        }

        return -1;
    }

    private static bool TryResolveRepeatDestination(
        IReadOnlyList<RcpEvent> events,
        IReadOnlyList<int> measureStartIndices,
        IReadOnlyDictionary<int, int> eventIndexByTrackOffset,
        int trackHeaderSize,
        int trackEventSize,
        int commandIp,
        out int measureId,
        out int destination,
        out int returnIp)
    {
        measureId = -1;
        destination = -1;
        returnIp = commandIp + 1;
        if (commandIp < 0 || commandIp >= events.Count)
        {
            return false;
        }

        var e = events[commandIp];
        if (e.CommandOrNote != 0xFC)
        {
            return false;
        }

        measureId = GetRepeatMeasureId(e);
        if (e.RawLength < 6)
        {
            var repeatOffset = ((e.Param1 & ~0x03) & 0xFF) | ((e.Param2 & 0xFF) << 8);
            if (repeatOffset >= trackHeaderSize && eventIndexByTrackOffset.TryGetValue(repeatOffset, out var fromOffset))
            {
                destination = fromOffset;
            }
            else if (repeatOffset >= trackHeaderSize &&
                     (repeatOffset - trackHeaderSize) % trackEventSize == 0)
            {
                var fromLinear = (repeatOffset - trackHeaderSize) / trackEventSize;
                if (fromLinear >= 0 && fromLinear < events.Count)
                {
                    destination = fromLinear;
                }
            }
        }
        else
        {
            var commandId = e.Param1;
            if (commandId >= 0x30)
            {
                var repeatOffsetLong = (long)trackHeaderSize + ((long)commandId - 0x30L) * trackEventSize;
                if (repeatOffsetLong >= 0 && repeatOffsetLong <= int.MaxValue)
                {
                    var repeatOffset = (int)repeatOffsetLong;
                    if (repeatOffset >= trackHeaderSize && eventIndexByTrackOffset.TryGetValue(repeatOffset, out var fromOffset))
                    {
                        destination = fromOffset;
                    }
                    else if (repeatOffset >= trackHeaderSize &&
                             (repeatOffset - trackHeaderSize) % trackEventSize == 0)
                    {
                        var fromLinear = (repeatOffset - trackHeaderSize) / trackEventSize;
                        if (fromLinear >= 0 && fromLinear < events.Count)
                        {
                            destination = fromLinear;
                        }
                    }
                }
            }
        }

        if (destination < 0 && measureId >= 0 && measureId < measureStartIndices.Count)
        {
            destination = measureStartIndices[measureId];
        }

        return true;
    }

    private static void AppendContinuationBytes(List<byte> raw, RcpEvent continuation, bool isG36Event)
    {
        if (!isG36Event)
        {
            raw.Add(ToByte(continuation.Param1));
            raw.Add(ToByte(continuation.Param2));
            return;
        }

        raw.Add(ToByte(continuation.Param2));
        raw.Add(ToByte(continuation.DelayTicks));
        raw.Add(ToByte(continuation.DelayTicks >> 8));
        raw.Add(ToByte(continuation.Param1));
        raw.Add(ToByte(continuation.Param1 >> 8));
    }

    private static ScheduledMidiEvent NewShortEvent(
        long tick,
        int status,
        int data1,
        int data2,
        int sourceTrackId = -1,
        int sourceEventIndex = -1,
        byte sourceCommand = 0)
    {
        return new ScheduledMidiEvent
        {
            Tick = tick,
            SourceTrackId = sourceTrackId,
            SourceEventIndex = sourceEventIndex,
            SourceCommand = sourceCommand,
            Packet = new MidiEventPacket
            {
                Kind = MidiMessageKind.Short,
                ShortMessage = PackShort((byte)status, ToByte(data1), ToByte(data2)),
                SysExData = null
            }
        };
    }

    private static ScheduledMidiEvent NewSysExEvent(
        long tick,
        byte[] data,
        int sourceTrackId = -1,
        int sourceEventIndex = -1,
        byte sourceCommand = 0)
    {
        return new ScheduledMidiEvent
        {
            Tick = tick,
            SourceTrackId = sourceTrackId,
            SourceEventIndex = sourceEventIndex,
            SourceCommand = sourceCommand,
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
    private readonly record struct LoopScanFrame(int StartIndex, int ParentIp, int Iteration = 0);
    private readonly record struct LoopTickFrame(int StartIndex, long StartTick, int ParentIp, int Iteration = 0);
    private readonly record struct TrackLoopAnalysis(int TrackId, bool HasInfiniteLoop, long LoopStartTick, long LoopTicks, long DurationTick);
    private readonly record struct ActiveNoteState(int OffEventIndex, long OffTick);
    private readonly record struct TempoModifierCommand(long Tick, int Ratio, int Gradation);
    private readonly record struct OrderedTempoModifier(TempoModifierCommand Command, int Order);

    private sealed class CommandAccumulator
    {
        public int Count { get; set; }
        public HashSet<int> Tracks { get; } = [];
    }
}
