using RcpPlayer.Core.Model;

namespace RcpPlayer.Core.Playback;

internal readonly record struct SequencedTempoModifier(long Tick, int Ratio, int Gradation);
internal readonly record struct SequencerTrackDebugState(
    int TrackId,
    string Name,
    bool IsActive,
    bool HasPlayedOnce,
    bool IsEnded,
    int EventPointer,
    long CurrentTick,
    int LoopDepth,
    int RepeatReturnDepth,
    int ActiveNoteCount,
    int PendingNoteOffCount,
    int ReadyMidiCount,
    int ReadyTempoCount,
    bool HasEmittedMusicalNote);

internal sealed class RcpRealtimeSequencer
{
    private readonly List<TrackRuntimeState> _tracks;

    public RcpRealtimeSequencer(
        RcpSong song,
        bool ignoreMutedTracks = true,
        bool mergeActiveSameNote = true,
        bool keepBoundaryActiveSameNote = false,
        bool emitEmptySysEx = false,
        bool emitPendingNoteOffsAfterTrackEnd = true,
        bool treatVelocityZeroNoteAsNoteOff = false,
        bool freezeInfiniteTracksAfterFirstCycle = false,
        bool treatHighLoopCountAsInfinite = true,
        bool rcpcvCompatTerminateDrumSetupAtReverbComment = false,
        bool rcpcvCompatBarDelayBeforeVelocityZeroNote = false,
        bool rcpcvCompatLimitSysExPayloadTo255 = false,
        bool rcpcvCompatDropFollowingSysExAfterLongPayload = false,
        bool rcdStrictMode = false,
        IReadOnlyDictionary<int, int>? infiniteLoopRepeatOverrides = null)
    {
        ArgumentNullException.ThrowIfNull(song);
        var targetTracks = song.Tracks
            .Where(t => !ignoreMutedTracks || !t.IsMuted)
            .ToList();

        _tracks = targetTracks
            .Select(t => new TrackRuntimeState(
                song,
                t,
                t.Events.Count > 0,
                mergeActiveSameNote,
                keepBoundaryActiveSameNote,
                emitEmptySysEx,
                emitPendingNoteOffsAfterTrackEnd,
                treatVelocityZeroNoteAsNoteOff,
                freezeInfiniteTracksAfterFirstCycle,
                treatHighLoopCountAsInfinite,
                rcpcvCompatTerminateDrumSetupAtReverbComment,
                rcpcvCompatBarDelayBeforeVelocityZeroNote,
                rcpcvCompatLimitSysExPayloadTo255,
                rcpcvCompatDropFollowingSysExAfterLongPayload,
                rcdStrictMode,
                infiniteLoopRepeatOverrides))
            .ToList();
    }

    public bool TryDequeueNextTick(
        out long tick,
        out IReadOnlyList<ScheduledMidiEvent> midiEvents,
        out IReadOnlyList<SequencedTempoModifier> tempoModifiers)
    {
        while (true)
        {
            var found = false;
            var nextTick = long.MaxValue;
            foreach (var track in _tracks)
            {
                var candidate = track.PeekNextTick();
                if (!candidate.HasValue)
                {
                    continue;
                }

                if (!found || candidate.Value < nextTick)
                {
                    found = true;
                    nextTick = candidate.Value;
                }
            }

            if (!found)
            {
                tick = 0;
                midiEvents = [];
                tempoModifiers = [];
                return false;
            }

            var midiBatch = new List<ScheduledMidiEvent>(64);
            var tempoBatch = new List<SequencedTempoModifier>(8);
            foreach (var track in _tracks)
            {
                track.DequeueAtTick(nextTick, midiBatch, tempoBatch);
            }

            if (midiBatch.Count == 0 && tempoBatch.Count == 0)
            {
                continue;
            }

            tick = nextTick;
            midiEvents = midiBatch;
            tempoModifiers = tempoBatch;
            return true;
        }
    }

    public bool TryDequeueNextTickEvents(out long tick, out IReadOnlyList<ScheduledMidiEvent> events)
    {
        var ok = TryDequeueNextTick(out tick, out var midiEvents, out _);
        events = midiEvents;
        return ok;
    }

    public bool AllActiveTracksPlayedOnce()
    {
        var hasActive = false;
        foreach (var track in _tracks)
        {
            if (!track.IsActive)
            {
                continue;
            }

            hasActive = true;
            if (!track.HasPlayedOnce)
            {
                return false;
            }

            // Do not stop while note lifecycle is still in flight.
            if (track.HasPendingNoteLifecycle)
            {
                return false;
            }
        }

        return hasActive;
    }

    public bool AllActiveTracksPlayedOnceIgnorePending()
    {
        var hasActive = false;
        foreach (var track in _tracks)
        {
            if (!track.IsActive)
            {
                continue;
            }

            hasActive = true;
            if (!track.HasPlayedOnce)
            {
                return false;
            }
        }

        return hasActive;
    }

    public bool HasAnyActiveInfiniteTrack()
    {
        return _tracks.Any(track => track.IsActive && track.IsInfiniteLoopTrack);
    }

    public bool AllActiveInfiniteTracksPlayedOnce()
    {
        var hasActiveInfinite = false;
        foreach (var track in _tracks)
        {
            if (!track.IsActive || !track.IsInfiniteLoopTrack)
            {
                // In mixed songs (infinite + finite tracks), do not stop early while
                // finite tracks still have note lifecycle or have not completed once.
                if (track.IsActive)
                {
                    if (!track.HasPlayedOnce || !track.IsEnded)
                    {
                        return false;
                    }

                    if (track.HasPendingNoteLifecycle)
                    {
                        return false;
                    }
                }

                continue;
            }

            hasActiveInfinite = true;
            if (!track.HasPlayedOnce)
            {
                return false;
            }

            if (!track.IsEnded)
            {
                return false;
            }

            // Do not stop while note lifecycle is still in flight on infinite tracks.
            if (track.HasPendingNoteLifecycle)
            {
                return false;
            }
        }

        return hasActiveInfinite;
    }

    public IReadOnlyList<SequencerTrackDebugState> GetTrackDebugStates()
    {
        return _tracks
            .Select(t => t.ToDebugState())
            .ToList();
    }

    private sealed class TrackRuntimeState
    {
        private const int MaxInterpreterStepsPerPump = 200_000;
        private const int MaxInterpreterStepsPerSameTickFrame = 20_000;

        private readonly RcpSong _song;
        private readonly int _trackId;
        private readonly string _trackName;
        private readonly IReadOnlyList<RcpEvent> _events;
        private readonly IReadOnlyList<int> _measureStartIndices;
        private readonly Dictionary<int, int> _eventIndexByTrackOffset;
        private readonly int _trackHeaderSize;
        private readonly int _trackEventSize;
        private readonly int _primaryLoopStartIndex;
        private readonly bool _mergeActiveSameNote;
        private readonly bool _keepBoundaryActiveSameNote;
        private readonly bool _emitEmptySysEx;
        private readonly bool _emitPendingNoteOffsAfterTrackEnd;
        private readonly bool _treatVelocityZeroNoteAsNoteOff;
        private readonly bool _freezeInfiniteTracksAfterFirstCycle;
        private readonly bool _treatHighLoopCountAsInfinite;
        private readonly bool _rcpcvCompatTerminateDrumSetupAtReverbComment;
        private readonly bool _rcpcvCompatBarDelayBeforeVelocityZeroNote;
        private readonly bool _rcpcvCompatLimitSysExPayloadTo255;
        private readonly bool _rcpcvCompatDropFollowingSysExAfterLongPayload;
        private readonly bool _rcdStrictMode;
        private readonly int _infiniteLoopRepeatCount;
        private readonly Stack<LoopFrame> _loops = new();
        private int? _repeatMeasureParentIp;
        private readonly Dictionary<int, ActiveNoteState> _activeNotes = new();
        private readonly PriorityQueue<PendingNoteOff, (long Tick, long Sequence)> _pendingNoteOffs = new();
        private readonly PriorityQueue<ScheduledMidiEvent, (long Tick, long Sequence)> _readyEvents = new();
        private readonly PriorityQueue<SequencedTempoModifier, (long Tick, long Sequence)> _readyTempoModifiers = new();
        private readonly HashSet<long> _seenLoopStateKeys = new();

        private long _nextSequence;
        private long _tick;
        private int _ip;
        private int _channel;
        private int _startTick;
        private int _trackTransposition;
        private bool _midiEnabled;
        private bool _ended;
        private bool _hasEmittedMusicalNote;
        private bool _rcpcvDrumSetupDetected;
        private bool _rcpcvDropNextDrumSetupDataEntry;
        private int _rcpcvDrumSetupReverbCount;
        private int _rcpcvPreviousBarDelay;
        private bool _rcpcvLongSysExSeen;
        private int _measureCount = 1;

        private int _rolandDev = 0x10;
        private int _rolandModel = 0x16;
        private int _rolandBaseH = 0x10;
        private int _rolandBaseM;

        private int _yamahaDev = 0x10;
        private int _yamahaModel = 0x4C;
        private int _yamahaBaseH;
        private int _yamahaBaseM;

        public TrackRuntimeState(
            RcpSong song,
            RcpTrack track,
            bool isActive,
            bool mergeActiveSameNote,
            bool keepBoundaryActiveSameNote,
            bool emitEmptySysEx,
            bool emitPendingNoteOffsAfterTrackEnd,
            bool treatVelocityZeroNoteAsNoteOff,
            bool freezeInfiniteTracksAfterFirstCycle,
            bool treatHighLoopCountAsInfinite,
            bool rcpcvCompatTerminateDrumSetupAtReverbComment,
            bool rcpcvCompatBarDelayBeforeVelocityZeroNote,
            bool rcpcvCompatLimitSysExPayloadTo255,
            bool rcpcvCompatDropFollowingSysExAfterLongPayload,
            bool rcdStrictMode,
            IReadOnlyDictionary<int, int>? infiniteLoopRepeatOverrides)
        {
            _song = song;
            _trackId = track.TrackId;
            _trackName = track.Name;
            _events = track.Events;
            _measureStartIndices = BuildMeasureStartIndices(_events);
            _trackHeaderSize = GetTrackHeaderSize(song.Format);
            _trackEventSize = GetDefaultEventSize(song.Format);
            _eventIndexByTrackOffset = BuildTrackOffsetLookup(_events, _trackHeaderSize, _trackEventSize);
            _primaryLoopStartIndex = FindPrimaryLoopStartIndex();
            _mergeActiveSameNote = mergeActiveSameNote;
            _keepBoundaryActiveSameNote = keepBoundaryActiveSameNote;
            _emitEmptySysEx = emitEmptySysEx;
            _emitPendingNoteOffsAfterTrackEnd = emitPendingNoteOffsAfterTrackEnd;
            _treatVelocityZeroNoteAsNoteOff = treatVelocityZeroNoteAsNoteOff;
            _freezeInfiniteTracksAfterFirstCycle = freezeInfiniteTracksAfterFirstCycle;
            _treatHighLoopCountAsInfinite = treatHighLoopCountAsInfinite;
            _rcpcvCompatTerminateDrumSetupAtReverbComment = rcpcvCompatTerminateDrumSetupAtReverbComment;
            _rcpcvCompatBarDelayBeforeVelocityZeroNote = rcpcvCompatBarDelayBeforeVelocityZeroNote;
            _rcpcvCompatLimitSysExPayloadTo255 = rcpcvCompatLimitSysExPayloadTo255;
            _rcpcvCompatDropFollowingSysExAfterLongPayload = rcpcvCompatDropFollowingSysExAfterLongPayload;
            _rcdStrictMode = rcdStrictMode;
            _infiniteLoopRepeatCount = infiniteLoopRepeatOverrides is not null &&
                                       infiniteLoopRepeatOverrides.TryGetValue(track.TrackId, out var repeatCount)
                ? Math.Max(repeatCount, 1)
                : 0;
            _channel = ClampChannel(track.DefaultChannel);
            _startTick = track.StartTick;
            _trackTransposition = track.TrackTransposition;
            // rcpcv still emits MIDI for tracks flagged as "dummy channel" in some files.
            // Start enabled and let explicit E6 channel-state commands control suppression.
            _midiEnabled = true;
            if (_startTick > 0)
            {
                _tick = _startTick;
                _startTick = 0;
            }

            IsActive = isActive;
            IsInfiniteLoopTrack = _events.Any(
                e => e.CommandOrNote == 0xF8 && IsInfiniteLoopRepeatCount(e.DelayTicks, _treatHighLoopCountAsInfinite));
            HasPlayedOnce = !IsActive;
        }

        public bool IsActive { get; }
        public bool IsInfiniteLoopTrack { get; }
        public bool IsEnded => _ended;
        public bool HasPendingNoteLifecycle => (_mergeActiveSameNote && _activeNotes.Count > 0) || _pendingNoteOffs.Count > 0;

        public bool HasPlayedOnce { get; private set; }

        public SequencerTrackDebugState ToDebugState()
        {
            return new SequencerTrackDebugState(
                TrackId: _trackId,
                Name: _trackName,
                IsActive: IsActive,
                HasPlayedOnce: HasPlayedOnce,
                IsEnded: _ended,
                EventPointer: _ip,
                CurrentTick: _tick,
                LoopDepth: _loops.Count,
                RepeatReturnDepth: _repeatMeasureParentIp.HasValue ? 1 : 0,
                ActiveNoteCount: _activeNotes.Count,
                PendingNoteOffCount: _pendingNoteOffs.Count,
                ReadyMidiCount: _readyEvents.Count,
                ReadyTempoCount: _readyTempoModifiers.Count,
                HasEmittedMusicalNote: _hasEmittedMusicalNote);
        }

        public long? PeekNextTick()
        {
            EnsureReadyEvents();
            var hasMidi = _readyEvents.TryPeek(out _, out var midiPriority);
            var hasTempo = _readyTempoModifiers.TryPeek(out _, out var tempoPriority);

            if (hasMidi && hasTempo)
            {
                return Math.Min(midiPriority.Tick, tempoPriority.Tick);
            }

            if (hasMidi)
            {
                return midiPriority.Tick;
            }

            if (hasTempo)
            {
                return tempoPriority.Tick;
            }

            return null;
        }

        public void DequeueAtTick(long tick, List<ScheduledMidiEvent> midiSink, List<SequencedTempoModifier> tempoSink)
        {
            EnsureReadyEvents();

            while (_readyEvents.TryPeek(out var e, out var priority) && priority.Tick == tick)
            {
                _readyEvents.Dequeue();
                midiSink.Add(e);
            }

            while (_readyTempoModifiers.TryPeek(out var t, out var priority) && priority.Tick == tick)
            {
                _readyTempoModifiers.Dequeue();
                tempoSink.Add(t);
            }
        }

        private void EnsureReadyEvents()
        {
            if ((_readyEvents.Count > 0 || _readyTempoModifiers.Count > 0) || _ended)
            {
                return;
            }

            if (_freezeInfiniteTracksAfterFirstCycle && IsInfiniteLoopTrack && HasPlayedOnce)
            {
                // Drain pending note-offs deterministically while frozen.
                // Stale queued entries can remain after same-note merges; if we flush only
                // at the current tick they can block later valid note-offs forever.
                FlushPendingNoteOffsBeforeCurrentTick();
                if (_pendingNoteOffs.TryPeek(out _, out var priority))
                {
                    if (priority.Tick > _tick)
                    {
                        _tick = priority.Tick;
                    }

                    FlushPendingNoteOffsAtTick(priority.Tick);
                }
                else
                {
                    _ended = true;
                }

                return;
            }

            var safetySteps = 0;
            while (_readyEvents.Count == 0 && _readyTempoModifiers.Count == 0 && !_ended)
            {
                safetySteps++;
                if (safetySteps > MaxInterpreterStepsPerPump)
                {
                    MarkPlayedOnce();
                    _ended = true;
                    break;
                }

                FlushPendingNoteOffsBeforeCurrentTick();
                if (_readyEvents.Count > 0 || _readyTempoModifiers.Count > 0)
                {
                    break;
                }

                if (_ip < 0 || _ip >= _events.Count)
                {
                    if (!_emitPendingNoteOffsAfterTrackEnd)
                    {
                        _activeNotes.Clear();
                        _pendingNoteOffs.Clear();
                        MarkPlayedOnce();
                        _ended = true;
                        break;
                    }

                    if (TryAdvanceTickToEarliestPendingNoteOff())
                    {
                        FlushPendingNoteOffsAtTick(_tick);
                        continue;
                    }

                    MarkPlayedOnce();
                    _ended = true;
                    break;
                }

                var frameTick = _tick;
                var frameSteps = 0;
                while (!_ended && _ip >= 0 && _ip < _events.Count && _tick == frameTick)
                {
                    frameSteps++;
                    if (frameSteps > MaxInterpreterStepsPerSameTickFrame)
                    {
                        MarkPlayedOnce();
                        _ended = true;
                        break;
                    }

                    ProcessCurrentEvent();
                }

                FlushPendingNoteOffsAtTick(frameTick);
            }
        }

        private bool TryAdvanceTickToEarliestPendingNoteOff()
        {
            if (!_pendingNoteOffs.TryPeek(out _, out var priority))
            {
                return false;
            }

            if (priority.Tick > _tick)
            {
                _tick = priority.Tick;
            }

            return true;
        }

        private void ProcessCurrentEvent()
        {
            var e = _events[_ip];
            var cmd = e.CommandOrNote;
            var rcpcvBarDelayForThisEvent = _rcpcvPreviousBarDelay;
            _rcpcvPreviousBarDelay = 0;
            var isVelocityZeroNote = cmd < 0x80 && _mergeActiveSameNote && ToByte(e.Param2) == 0;
            if (!_keepBoundaryActiveSameNote && !isVelocityZeroNote)
            {
                // Reference conversion mode: emit note-offs due at this tick before
                // handling any command at the same tick.
                FlushPendingNoteOffsAtTick(_tick);
            }

            if (cmd < 0x80)
            {
                PruneExpiredActiveNotes();
                EmitNoteEvent(e, rcpcvBarDelayForThisEvent);
                if (!_keepBoundaryActiveSameNote && isVelocityZeroNote)
                {
                    // For velocity=0 note-on events, let same-tick boundary notes be
                    // updated first, then flush note-offs at this tick.
                    FlushPendingNoteOffsAtTick(_tick);
                }
                AdvanceByDelay(e.DelayTicks);
                _ip++;
                return;
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
                    if (_midiEnabled && index < _song.UserExclusives.Count)
                    {
                        var body = _song.UserExclusives[index].DataWithoutLeadingF0;
                        var ex = ExpandSysExTemplate(body, e.Param1 & 0xFF, e.Param2 & 0xFF, _channel);
                        if (_emitEmptySysEx ? ex.Length >= 2 : ex.Length > 2)
                        {
                            EnqueueReadySysEx(_tick, ex);
                        }
                    }

                    AdvanceByDelay(e.DelayTicks);
                    _ip++;
                    break;
                }
                case 0x98:
                {
                    var raw = new List<byte>();
                    var isG36Event = e.RawLength >= 6;
                    var localIp = _ip + 1;
                    while (localIp < _events.Count && _events[localIp].CommandOrNote == 0xF7)
                    {
                        AppendContinuationBytes(raw, _events[localIp], isG36Event);
                        localIp++;
                    }

                    if (_midiEnabled && (!_rcpcvCompatDropFollowingSysExAfterLongPayload || !_rcpcvLongSysExSeen))
                    {
                        var bytes = raw.ToArray();
                        var ex = ExpandSysExTemplate(bytes, e.Param1 & 0xFF, e.Param2 & 0xFF, _channel);
                        if (_rcpcvCompatDropFollowingSysExAfterLongPayload && ex.Length > 256)
                        {
                            _rcpcvLongSysExSeen = true;
                        }

                        if (_emitEmptySysEx ? ex.Length >= 2 : ex.Length > 2)
                        {
                            EnqueueReadySysEx(_tick, ex);
                        }
                    }

                    AdvanceByDelay(e.DelayTicks);
                    _ip = localIp;
                    break;
                }
                case 0x99:
                    AdvanceByDelay(e.DelayTicks);
                    _ip = SkipContinuationEvents(_events, _ip + 1);
                    break;
                case 0xE1:
                    if (_midiEnabled)
                    {
                        EnqueueReadyShort(_tick, 0xB0 | _channel, 32, e.Param2);
                        EnqueueReadyShort(_tick, 0xC0 | _channel, e.Param1, 0);
                    }

                    AdvanceByDelay(e.DelayTicks);
                    _ip++;
                    break;
                case 0xE2:
                    if (_midiEnabled)
                    {
                        EnqueueReadyShort(_tick, 0xB0 | _channel, 0, e.Param2);
                        EnqueueReadyShort(_tick, 0xB0 | _channel, 32, 0);
                        EnqueueReadyShort(_tick, 0xC0 | _channel, e.Param1, 0);
                    }

                    AdvanceByDelay(e.DelayTicks);
                    _ip++;
                    break;
                case 0xE5:
                    AdvanceByDelay(e.DelayTicks);
                    _ip++;
                    break;
                case 0xEA:
                    if (_midiEnabled)
                    {
                        EnqueueReadyShort(_tick, 0xD0 | _channel, e.Param1, 0);
                    }

                    AdvanceByDelay(e.DelayTicks);
                    _ip++;
                    break;
                case 0xEB:
                    if (_rcpcvCompatTerminateDrumSetupAtReverbComment &&
                        _channel is 9 or 10 &&
                        _rcpcvDropNextDrumSetupDataEntry &&
                        e.Param1 == 6)
                    {
                        _rcpcvDropNextDrumSetupDataEntry = false;
                        AdvanceByDelay(e.DelayTicks);
                        _ip++;
                        break;
                    }

                    if (_midiEnabled)
                    {
                        EnqueueReadyShort(_tick, 0xB0 | _channel, e.Param1, e.Param2);
                    }

                    AdvanceByDelay(e.DelayTicks);
                    _ip++;
                    break;
                case 0xEC:
                    if (_midiEnabled)
                    {
                        if (e.Param1 < 0x80)
                        {
                            EnqueueReadyShort(_tick, 0xC0 | _channel, e.Param1, 0);
                        }
                        else if (!_rcdStrictMode && e.Param1 < 0xC0 && _channel is >= 1 and < 9)
                        {
                            EnqueueReadySysEx(_tick, BuildMt32PatchChangeSysEx(_channel, e.Param1));
                        }
                    }

                    AdvanceByDelay(e.DelayTicks);
                    _ip++;
                    break;
                case 0xED:
                    if (_midiEnabled)
                    {
                        EnqueueReadyShort(_tick, 0xA0 | _channel, e.Param1, e.Param2);
                    }

                    AdvanceByDelay(e.DelayTicks);
                    _ip++;
                    break;
                case 0xEE:
                    if (_midiEnabled)
                    {
                        EnqueueReadyShort(_tick, 0xE0 | _channel, e.Param1, e.Param2, _ip, e.CommandOrNote);
                    }

                    AdvanceByDelay(e.DelayTicks);
                    _ip++;
                    break;
                case 0xE6:
                    DecodeCommandChannelState(e.Param1, _rcdStrictMode, out _midiEnabled, out _channel);
                    AdvanceByDelay(e.DelayTicks);
                    _ip++;
                    break;
                case 0xE7:
                    EnqueueTempoModifier(_tick, Math.Max(1, e.Param1), 0);
                    AdvanceByDelay(e.DelayTicks);
                    _ip++;
                    break;
                case 0xC0:
                    if (_midiEnabled)
                    {
                        EnqueueReadySysEx(_tick, [0xF0, 0x43, (byte)(0x10 + _channel), 0x08, ToByte(e.Param1), ToByte(e.Param2), 0xF7]);
                    }

                    AdvanceByDelay(e.DelayTicks);
                    _ip++;
                    break;
                case 0xC1:
                    if (_midiEnabled)
                    {
                        EnqueueReadySysEx(_tick, [0xF0, 0x43, (byte)(0x10 + _channel), 0x00, ToByte(e.Param1), ToByte(e.Param2), 0xF7]);
                    }

                    AdvanceByDelay(e.DelayTicks);
                    _ip++;
                    break;
                case 0xC2:
                    if (_midiEnabled)
                    {
                        EnqueueReadySysEx(_tick, [0xF0, 0x43, (byte)(0x10 + _channel), 0x04, ToByte(e.Param1), ToByte(e.Param2), 0xF7]);
                    }

                    AdvanceByDelay(e.DelayTicks);
                    _ip++;
                    break;
                case 0xC3:
                    if (_midiEnabled)
                    {
                        EnqueueReadySysEx(_tick, [0xF0, 0x43, (byte)(0x10 + _channel), 0x11, ToByte(e.Param1), ToByte(e.Param2), 0xF7]);
                    }

                    AdvanceByDelay(e.DelayTicks);
                    _ip++;
                    break;
                case 0xC5:
                    if (_midiEnabled)
                    {
                        EnqueueReadySysEx(_tick, [0xF0, 0x43, (byte)(0x10 + _channel), 0x15, ToByte(e.Param1), ToByte(e.Param2), 0xF7]);
                    }

                    AdvanceByDelay(e.DelayTicks);
                    _ip++;
                    break;
                case 0xC6:
                    if (_midiEnabled)
                    {
                        EnqueueReadySysEx(_tick, [0xF0, 0x43, 0x75, (byte)_channel, 0x10, ToByte(e.Param1), ToByte(e.Param2), 0xF7]);
                    }

                    AdvanceByDelay(e.DelayTicks);
                    _ip++;
                    break;
                case 0xC7:
                    if (_midiEnabled)
                    {
                        EnqueueReadySysEx(_tick, [0xF0, 0x43, (byte)(0x10 + _channel), 0x12, ToByte(e.Param1), ToByte(e.Param2), 0xF7]);
                    }

                    AdvanceByDelay(e.DelayTicks);
                    _ip++;
                    break;
                case 0xC8:
                    if (_midiEnabled)
                    {
                        EnqueueReadySysEx(_tick, [0xF0, 0x43, (byte)(0x10 + _channel), 0x13, ToByte(e.Param1), ToByte(e.Param2), 0xF7]);
                    }

                    AdvanceByDelay(e.DelayTicks);
                    _ip++;
                    break;
                case 0xC9:
                    if (_midiEnabled)
                    {
                        EnqueueReadySysEx(_tick, [0xF0, 0x43, (byte)(0x10 + _channel), 0x10, ToByte(e.Param1), ToByte(e.Param2), 0xF7]);
                    }

                    AdvanceByDelay(e.DelayTicks);
                    _ip++;
                    break;
                case 0xCA:
                    if (_midiEnabled)
                    {
                        EnqueueReadySysEx(_tick, [0xF0, 0x43, (byte)(0x10 + _channel), 0x10, 0x7B, ToByte(e.Param1), ToByte(e.Param2), 0xF7]);
                    }

                    AdvanceByDelay(e.DelayTicks);
                    _ip++;
                    break;
                case 0xCB:
                    if (_midiEnabled)
                    {
                        EnqueueReadySysEx(_tick, [0xF0, 0x43, (byte)(0x10 + _channel), 0x10, 0x7C, ToByte(e.Param1), ToByte(e.Param2), 0xF7]);
                    }

                    AdvanceByDelay(e.DelayTicks);
                    _ip++;
                    break;
                case 0xCC:
                    if (_midiEnabled)
                    {
                        EnqueueReadySysEx(_tick, [0xF0, 0x43, (byte)(0x10 + _channel), 0x1B, ToByte(e.Param1), ToByte(e.Param2), 0xF7]);
                    }

                    AdvanceByDelay(e.DelayTicks);
                    _ip++;
                    break;
                case 0xCD:
                    if (_midiEnabled)
                    {
                        EnqueueReadySysEx(_tick, [0xF0, 0x43, (byte)(0x10 + _channel), 0x18, ToByte(e.Param1), ToByte(e.Param2), 0xF7]);
                    }

                    AdvanceByDelay(e.DelayTicks);
                    _ip++;
                    break;
                case 0xCE:
                    if (_midiEnabled)
                    {
                        EnqueueReadySysEx(_tick, [0xF0, 0x43, (byte)(0x10 + _channel), 0x19, ToByte(e.Param1), ToByte(e.Param2), 0xF7]);
                    }

                    AdvanceByDelay(e.DelayTicks);
                    _ip++;
                    break;
                case 0xCF:
                    if (_midiEnabled)
                    {
                        EnqueueReadySysEx(_tick, [0xF0, 0x43, (byte)(0x10 + _channel), 0x1A, ToByte(e.Param1), ToByte(e.Param2), 0xF7]);
                    }

                    AdvanceByDelay(e.DelayTicks);
                    _ip++;
                    break;
                case 0xD0:
                    _yamahaBaseH = e.Param1 & 0xFF;
                    _yamahaBaseM = e.Param2 & 0xFF;
                    AdvanceByDelay(e.DelayTicks);
                    _ip++;
                    break;
                case 0xD1:
                    _yamahaDev = e.Param1 & 0xFF;
                    _yamahaModel = e.Param2 & 0xFF;
                    AdvanceByDelay(e.DelayTicks);
                    _ip++;
                    break;
                case 0xD2:
                    if (_midiEnabled)
                    {
                        EnqueueReadySysEx(_tick, new byte[]
                        {
                            0xF0, 0x43, (byte)_yamahaDev, (byte)_yamahaModel,
                            (byte)_yamahaBaseH, (byte)_yamahaBaseM, ToByte(e.Param1), ToByte(e.Param2), 0xF7
                        });
                    }

                    AdvanceByDelay(e.DelayTicks);
                    _ip++;
                    break;
                case 0xD3:
                    if (_midiEnabled)
                    {
                        EnqueueReadySysEx(_tick, new byte[]
                        {
                            0xF0, 0x43, 0x10, 0x4C,
                            (byte)_yamahaBaseH, (byte)_yamahaBaseM, ToByte(e.Param1), ToByte(e.Param2), 0xF7
                        });
                    }

                    AdvanceByDelay(e.DelayTicks);
                    _ip++;
                    break;
                case 0xDC:
                    if (_midiEnabled)
                    {
                        EnqueueReadySysEx(_tick, [0xF0, 0x41, 0x32, (byte)_channel, ToByte(e.Param1), ToByte(e.Param2), 0xF7]);
                    }

                    AdvanceByDelay(e.DelayTicks);
                    _ip++;
                    break;
                case 0xDD:
                    _rolandBaseH = e.Param1 & 0xFF;
                    _rolandBaseM = e.Param2 & 0xFF;
                    AdvanceByDelay(e.DelayTicks);
                    _ip++;
                    break;
                case 0xDE:
                {
                    var addrL = e.Param1 & 0xFF;
                    var parameter = e.Param2 & 0xFF;
                    var sum = _rolandBaseH + _rolandBaseM + addrL + parameter;
                    var check = (128 - (sum & 0x7F)) & 0x7F;
                    if (_midiEnabled &&
                        (!_rcpcvCompatDropFollowingSysExAfterLongPayload || !_rcpcvLongSysExSeen))
                    {
                        var ex = new byte[]
                        {
                            0xF0, 0x41, (byte)_rolandDev, (byte)_rolandModel, 0x12,
                            (byte)_rolandBaseH, (byte)_rolandBaseM, (byte)addrL, (byte)parameter, (byte)check, 0xF7
                        };
                        if (_rcpcvCompatDropFollowingSysExAfterLongPayload && ex.Length > 256)
                        {
                            _rcpcvLongSysExSeen = true;
                        }

                        EnqueueReadySysEx(_tick, ex);
                    }

                    AdvanceByDelay(e.DelayTicks);
                    _ip++;
                    break;
                }
                case 0xDF:
                    _rolandDev = e.Param1 & 0xFF;
                    _rolandModel = e.Param2 & 0xFF;
                    AdvanceByDelay(e.DelayTicks);
                    _ip++;
                    break;
                case 0xF9:
                    _loops.Push(new LoopFrame(_ip + 1, _repeatMeasureParentIp ?? -1));
                    _ip++;
                    break;
                case 0xF8:
                {
                    if (_loops.Count == 0)
                    {
                        _ip++;
                        break;
                    }

                    var top = _loops.Pop();
                    if (IsInfiniteLoopRepeatCount(e.DelayTicks, _treatHighLoopCountAsInfinite))
                    {
                        if (_infiniteLoopRepeatCount > 0)
                        {
                            var configuredRepeatCount = _infiniteLoopRepeatCount;
                            if (top.Iteration + 1 < configuredRepeatCount)
                            {
                                top = top with { Iteration = top.Iteration + 1 };
                                _loops.Push(top);
                                _repeatMeasureParentIp = top.ParentIp >= 0 ? top.ParentIp : null;
                                _ip = top.StartIndex;
                            }
                            else
                            {
                                _ip++;
                            }

                            break;
                        }

                        var loopCount = top.Iteration + 1;
                        top = top with { Iteration = loopCount };

                        _loops.Push(top);
                        _repeatMeasureParentIp = top.ParentIp >= 0 ? top.ParentIp : null;
                        TryMarkCycleCompletionOnBackwardJump(top.StartIndex);
                        _ip = top.StartIndex;
                        break;
                    }

                    var repeatCount = Math.Max(e.DelayTicks, 1);
                    if (top.Iteration + 1 < repeatCount)
                    {
                        top = top with { Iteration = top.Iteration + 1 };
                        _loops.Push(top);
                        _repeatMeasureParentIp = top.ParentIp >= 0 ? top.ParentIp : null;
                        TryMarkCycleCompletionOnBackwardJump(top.StartIndex);
                        _ip = top.StartIndex;
                    }
                    else
                    {
                        _ip++;
                        if (_rcpcvCompatBarDelayBeforeVelocityZeroNote)
                        {
                            _rcpcvPreviousBarDelay = Math.Max(e.DelayTicks, 0);
                        }
                    }

                    break;
                }
                case 0xFD:
                {
                    if (_rcpcvCompatTerminateDrumSetupAtReverbComment &&
                        _channel is 9 or 10 &&
                        _rcpcvDrumSetupDetected)
                    {
                        if (!_emitPendingNoteOffsAfterTrackEnd)
                        {
                            _activeNotes.Clear();
                            _pendingNoteOffs.Clear();
                        }

                        MarkPlayedOnce();
                        _ip = _events.Count;
                        _ended = true;
                        break;
                    }

                    if (_repeatMeasureParentIp.HasValue)
                    {
                        var destination = _repeatMeasureParentIp.Value;
                        _repeatMeasureParentIp = null;
                        if (destination >= 0 && destination < _events.Count)
                        {
                            if (destination < _ip)
                            {
                                TryMarkCycleCompletionOnBackwardJump(destination);
                            }

                            _ip = destination;
                        }
                        else
                        {
                            _ip++;
                        }
                    }
                    else
                    {
                        _ip++;
                    }

                    _measureCount = Math.Min(_measureCount + 1, short.MaxValue);
                    break;
                }
                case 0xFC:
                {
                    if (_repeatMeasureParentIp.HasValue)
                    {
                        var destination = _repeatMeasureParentIp.Value;
                        _repeatMeasureParentIp = null;
                        if (destination >= 0 && destination < _events.Count)
                        {
                            if (destination < _ip)
                            {
                                TryMarkCycleCompletionOnBackwardJump(destination);
                            }

                            _ip = destination;
                        }
                        else
                        {
                            _ip++;
                        }

                        break;
                    }

                    var commandIp = _ip;
                    var chainIp = commandIp;
                    var parentIp = -1;
                    for (var guard = 0; guard < 256; guard++)
                    {
                        if (!TryResolveRepeatDestination(chainIp, out var measureId, out var destination, out var returnIp))
                        {
                            _ip = chainIp + 1;
                            _repeatMeasureParentIp = parentIp >= 0 ? parentIp : null;
                            break;
                        }

                        // FC is only valid after the target measure is already known at runtime.
                            if (measureId > _measureCount ||
                                destination < 0 ||
                                destination >= _events.Count ||
                                destination == chainIp)
                        {
                            _ip = returnIp;
                            _repeatMeasureParentIp = parentIp >= 0 ? parentIp : null;
                            break;
                        }

                        if (parentIp < 0)
                        {
                            parentIp = returnIp;
                        }

                        if (destination < chainIp)
                        {
                            TryMarkCycleCompletionOnBackwardJump(destination);
                        }

                        chainIp = destination;
                        if (_events[chainIp].CommandOrNote != 0xFC)
                        {
                            _repeatMeasureParentIp = parentIp >= 0 ? parentIp : null;
                            _ip = chainIp;
                            break;
                        }

                        if (guard == 255)
                        {
                            _ip = parentIp >= 0 ? parentIp : commandIp + 1;
                            _repeatMeasureParentIp = null;
                        }
                    }

                    break;
                }
                case 0xF7:
                    _ip++;
                    break;
                case 0xF6:
                    if (_rcpcvCompatTerminateDrumSetupAtReverbComment && _channel is 9 or 10)
                    {
                        if (IsReverbCommentBlock(_events, _ip))
                        {
                            _rcpcvDrumSetupDetected = true;
                            _rcpcvDrumSetupReverbCount++;
                            if (_rcpcvDrumSetupReverbCount == 5)
                            {
                                _rcpcvDropNextDrumSetupDataEntry = true;
                            }
                        }
                        else if (_rcpcvDrumSetupDetected &&
                                 _rcpcvDrumSetupReverbCount >= 5 &&
                                 IsChorusCommentBlock(_events, _ip))
                        {
                            if (!_emitPendingNoteOffsAfterTrackEnd)
                            {
                                _activeNotes.Clear();
                                _pendingNoteOffs.Clear();
                            }

                            MarkPlayedOnce();
                            _ip = _events.Count;
                            _ended = true;
                            break;
                        }
                    }

                    _ip = SkipContinuationEvents(_events, _ip + 1);
                    break;
                case 0xF5:
                    _ip++;
                    break;
                case 0xFE:
                    if (!_emitPendingNoteOffsAfterTrackEnd)
                    {
                        _activeNotes.Clear();
                        _pendingNoteOffs.Clear();
                    }

                    MarkPlayedOnce();
                    _ip = _events.Count;
                    break;
                default:
                    if (cmd < 0xF0)
                    {
                        AdvanceByDelay(e.DelayTicks);
                    }

                    _ip++;
                    break;
            }
        }

        private void MarkPlayedOnce()
        {
            if (IsActive)
            {
                HasPlayedOnce = true;
            }
        }

        private void TryMarkCycleCompletionOnBackwardJump(int destinationIp)
        {
            // When finite repeat overrides are active for this infinite track, loop completion
            // should be driven by the explicit repeat count, not by heuristic cycle detection.
            if (_infiniteLoopRepeatCount > 0)
            {
                return;
            }

            if (!IsActive || !IsInfiniteLoopTrack || HasPlayedOnce || !_hasEmittedMusicalNote)
            {
                return;
            }

            // Only treat jumps to the primary infinite-loop start as cycle boundaries.
            // Backward FC jumps can occur inside the loop body and must not end the track.
            if (_primaryLoopStartIndex >= 0 && destinationIp != _primaryLoopStartIndex)
            {
                return;
            }

            if (destinationIp >= _ip)
            {
                return;
            }

            if (_activeNotes.Count > 0 || _pendingNoteOffs.Count > 0)
            {
                return;
            }

            var key = BuildLoopStateKey(destinationIp);
            if (_seenLoopStateKeys.Contains(key))
            {
                MarkPlayedOnce();
                return;
            }

            _seenLoopStateKeys.Add(key);
        }

        private long BuildLoopStateKey(int destinationIp)
        {
            var hash = new HashCode();
            hash.Add(destinationIp);
            hash.Add(_channel);
            hash.Add(_rolandDev);
            hash.Add(_rolandModel);
            hash.Add(_rolandBaseH);
            hash.Add(_rolandBaseM);
            hash.Add(_yamahaDev);
            hash.Add(_yamahaModel);
            hash.Add(_yamahaBaseH);
            hash.Add(_yamahaBaseM);
            hash.Add(_measureCount);
            hash.Add(_repeatMeasureParentIp ?? -1);

            hash.Add(_loops.Count);
            foreach (var item in _loops)
            {
                hash.Add(item.StartIndex);
                hash.Add(item.ParentIp);
                hash.Add(item.Iteration);
            }

            return hash.ToHashCode();
        }

        private bool TryResolveRepeatDestination(int commandIp, out int measureId, out int destinationIp, out int returnIp)
        {
            measureId = -1;
            destinationIp = -1;
            returnIp = commandIp + 1;

            if (commandIp < 0 || commandIp >= _events.Count)
            {
                return false;
            }

            var e = _events[commandIp];
            if (e.CommandOrNote != 0xFC)
            {
                return false;
            }

            measureId = GetRepeatMeasureId(e);
            if (e.RawLength < 6)
            {
                var repeatOffset = ((e.Param1 & ~0x03) & 0xFF) | ((e.Param2 & 0xFF) << 8);
                if (repeatOffset >= _trackHeaderSize && _eventIndexByTrackOffset.TryGetValue(repeatOffset, out var fromOffset))
                {
                    destinationIp = fromOffset;
                }
                else if (repeatOffset >= _trackHeaderSize &&
                         (repeatOffset - _trackHeaderSize) % _trackEventSize == 0)
                {
                    var fromLinear = (repeatOffset - _trackHeaderSize) / _trackEventSize;
                    if (fromLinear >= 0 && fromLinear < _events.Count)
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
                    var repeatOffsetLong = (long)_trackHeaderSize + ((long)commandId - 0x30L) * _trackEventSize;
                    if (repeatOffsetLong >= 0 && repeatOffsetLong <= int.MaxValue)
                    {
                        var repeatOffset = (int)repeatOffsetLong;
                        if (repeatOffset >= _trackHeaderSize && _eventIndexByTrackOffset.TryGetValue(repeatOffset, out var fromOffset))
                        {
                            destinationIp = fromOffset;
                        }
                        else if (repeatOffset >= _trackHeaderSize &&
                                 (repeatOffset - _trackHeaderSize) % _trackEventSize == 0)
                        {
                            var fromLinear = (repeatOffset - _trackHeaderSize) / _trackEventSize;
                            if (fromLinear >= 0 && fromLinear < _events.Count)
                            {
                                destinationIp = fromLinear;
                            }
                        }
                    }
                }
            }

            if (destinationIp < 0 && measureId >= 0 && measureId < _measureStartIndices.Count)
            {
                destinationIp = _measureStartIndices[measureId];
            }

            return true;
        }

        private int FindPrimaryLoopStartIndex()
        {
            var loopStack = new Stack<LoopScanFrame>();
            int? repeatMeasureParentIp = null;
            var ip = 0;
            var measureCount = 1;
            var guard = 0;
            var guardLimit = Math.Max(_events.Count * 64, 4096);

            while (ip >= 0 && ip < _events.Count && guard < guardLimit)
            {
                guard++;
                var e = _events[ip];
                switch (e.CommandOrNote)
                {
                    case 0xF9:
                        loopStack.Push(new LoopScanFrame(ip + 1, repeatMeasureParentIp ?? -1));
                        ip++;
                        break;
                    case 0xF8:
                        if (loopStack.Count == 0)
                        {
                            ip++;
                            break;
                        }

                        var top = loopStack.Pop();
                        if (IsInfiniteLoopRepeatCount(e.DelayTicks, _treatHighLoopCountAsInfinite))
                        {
                            return top.StartIndex;
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
                        for (var chainGuard = 0; chainGuard < 256; chainGuard++)
                        {
                            if (!TryResolveRepeatDestination(chainIp, out var measureId, out var destination, out var returnIp))
                            {
                                ip = chainIp + 1;
                                repeatMeasureParentIp = parentIp >= 0 ? parentIp : null;
                                break;
                            }

                            if (measureId > measureCount ||
                                destination < 0 ||
                                destination >= _events.Count ||
                                destination == chainIp)
                            {
                                ip = returnIp;
                                repeatMeasureParentIp = parentIp >= 0 ? parentIp : null;
                                break;
                            }

                            if (parentIp < 0)
                            {
                                parentIp = returnIp;
                            }

                            chainIp = destination;
                            if (_events[chainIp].CommandOrNote != 0xFC)
                            {
                                repeatMeasureParentIp = parentIp >= 0 ? parentIp : null;
                                ip = chainIp;
                                break;
                            }

                            if (chainGuard == 255)
                            {
                                ip = parentIp >= 0 ? parentIp : ip + 1;
                                repeatMeasureParentIp = null;
                            }
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
                        return -1;
                    default:
                        ip++;
                        break;
                }
            }

            return -1;
        }

        private void AdvanceByDelay(int delayTicks)
        {
            var delay = Math.Max(delayTicks, 0);
            if (_startTick < 0 && delay > 0)
            {
                _startTick += delay;
                if (_startTick < 0)
                {
                    return;
                }

                delay = _startTick;
                _startTick = 0;
            }

            _tick += delay;
        }

        private void EmitNoteEvent(RcpEvent e, int rcpcvBarDelayForThisEvent)
        {
            if (!_midiEnabled)
            {
                return;
            }

            var note = (byte)((e.CommandOrNote + _trackTransposition) & 0x7F);
            var velocity = ToByte(e.Param2);
            var gate = Math.Max(e.Param1, 0);

            var key = BuildActiveNoteKey(_channel, note);
            if (velocity <= 0)
            {
                if (!_treatVelocityZeroNoteAsNoteOff)
                {
                    return;
                }

                if (_mergeActiveSameNote && _activeNotes.TryGetValue(key, out var active))
                {
                    var velocityZeroOffTick = _tick;
                    if (_rcpcvCompatBarDelayBeforeVelocityZeroNote &&
                        rcpcvBarDelayForThisEvent > 0)
                    {
                        velocityZeroOffTick += rcpcvBarDelayForThisEvent;
                    }

                    // Velocity=0 note is treated as immediate note-off of the same pitch.
                    // Update pending off timing (shorten or extend) to this event tick.
                    if (velocityZeroOffTick != active.OffTick)
                    {
                        var version = active.Version + 1;
                        _activeNotes[key] = active with
                        {
                            OffTick = velocityZeroOffTick,
                            Version = version
                        };
                        EnqueuePendingNoteOff(active.Channel, note, velocityZeroOffTick, version);
                    }
                }
                return;
            }

            if (gate <= 0)
            {
                return;
            }

            _hasEmittedMusicalNote = true;
            var offTick = _tick + gate;
            if (_mergeActiveSameNote)
            {
                if (_activeNotes.TryGetValue(key, out var active))
                {
                    var version = active.Version + 1;
                    _activeNotes[key] = new ActiveNoteState(active.Channel, active.OnTick, offTick, version);
                    EnqueuePendingNoteOff(active.Channel, note, offTick, version);
                    return;
                }
            }

            EnqueueReadyShort(_tick, 0x90 | _channel, note, velocity);
            if (_mergeActiveSameNote)
            {
                _activeNotes[key] = new ActiveNoteState(_channel, _tick, offTick, 1);
            }

            EnqueuePendingNoteOff(_channel, note, offTick, 1);
        }

        private void EnqueuePendingNoteOff(int channel, int note, long tick, int version)
        {
            _pendingNoteOffs.Enqueue(
                new PendingNoteOff(channel, note, tick, version),
                (tick, _nextSequence++));
        }

        private void FlushPendingNoteOffsBeforeCurrentTick()
        {
            while (_pendingNoteOffs.TryPeek(out _, out var priority) && priority.Tick < _tick)
            {
                FlushPendingNoteOffsAtTick(priority.Tick);
            }
        }

        private void FlushPendingNoteOffsAtTick(long tick)
        {
            var batch = new List<(PendingNoteOff Candidate, long Sequence)>();
            while (_pendingNoteOffs.TryPeek(out var candidate, out var priority) && priority.Tick == tick)
            {
                _pendingNoteOffs.Dequeue();
                batch.Add((candidate, priority.Sequence));
            }

            if (batch.Count == 0)
            {
                return;
            }

            if (!_mergeActiveSameNote)
            {
                foreach (var item in batch.OrderBy(x => x.Sequence))
                {
                    EnqueueReadyShort(item.Candidate.Tick, 0x90 | item.Candidate.Channel, item.Candidate.Note, 0);
                }

                return;
            }

            var valid = new List<(PendingNoteOff Candidate, long OnTick, long Sequence)>(batch.Count);
            foreach (var item in batch)
            {
                var candidate = item.Candidate;
                var key = BuildActiveNoteKey(candidate.Channel, candidate.Note);
                if (!_activeNotes.TryGetValue(key, out var active))
                {
                    continue;
                }

                if (active.Channel != candidate.Channel || active.Version != candidate.Version || active.OffTick != candidate.Tick)
                {
                    continue;
                }

                valid.Add((candidate, active.OnTick, item.Sequence));
            }

            foreach (var item in valid.OrderBy(x => x.OnTick).ThenBy(x => x.Sequence))
            {
                _activeNotes.Remove(BuildActiveNoteKey(item.Candidate.Channel, item.Candidate.Note));
                EnqueueReadyShort(item.Candidate.Tick, 0x90 | item.Candidate.Channel, item.Candidate.Note, 0);
            }
        }

        private void PruneExpiredActiveNotes()
        {
            if (!_mergeActiveSameNote)
            {
                return;
            }

            if (_activeNotes.Count == 0)
            {
                return;
            }

            var expired = _activeNotes
                .Where(kvp => kvp.Value.OffTick < _tick)
                .Select(kvp => kvp.Key)
                .ToList();
            foreach (var key in expired)
            {
                _activeNotes.Remove(key);
            }
        }

        private void EnqueueTempoModifier(long tick, int ratio, int gradation)
        {
            _readyTempoModifiers.Enqueue(new SequencedTempoModifier(tick, ratio, gradation), (tick, _nextSequence++));
        }

        private void EnqueueReadyShort(long tick, int status, int data1, int data2, int sourceEventIndex = -1, byte sourceCommand = 0)
        {
            _readyEvents.Enqueue(
                new ScheduledMidiEvent
                {
                    Tick = tick,
                    SourceTrackId = _trackId,
                    SourceEventIndex = sourceEventIndex,
                    SourceCommand = sourceCommand,
                    Packet = new MidiEventPacket
                    {
                        Kind = MidiMessageKind.Short,
                        ShortMessage = PackShort((byte)status, ToByte(data1), ToByte(data2)),
                        SysExData = null
                    }
                },
                (tick, _nextSequence++));
        }

        private void EnqueueReadySysEx(long tick, byte[] data, int sourceEventIndex = -1, byte sourceCommand = 0)
        {
            if (_rcpcvCompatLimitSysExPayloadTo255)
            {
                data = CapSysExPayload(data, 255);
            }

            _readyEvents.Enqueue(
                new ScheduledMidiEvent
                {
                    Tick = tick,
                    SourceTrackId = _trackId,
                    SourceEventIndex = sourceEventIndex,
                    SourceCommand = sourceCommand,
                    Packet = new MidiEventPacket
                    {
                        Kind = MidiMessageKind.SysEx,
                        ShortMessage = 0,
                        SysExData = data
                    }
                },
                (tick, _nextSequence++));
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

    private static int GetTrackHeaderSize(RcpFormat format)
    {
        return format == RcpFormat.G36 ? 0x2E : 0x2C;
    }

    private static int GetDefaultEventSize(RcpFormat format)
    {
        return format == RcpFormat.G36 ? 6 : 4;
    }

    private static Dictionary<int, int> BuildTrackOffsetLookup(IReadOnlyList<RcpEvent> events, int headerSize, int defaultEventSize)
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

        private static int Clamp7Bit(int value)
        {
            return Math.Clamp(value, 0, 127);
        }

        private static bool IsInfiniteLoopRepeatCount(int repeatCount, bool treatHighLoopCountAsInfinite)
        {
            return repeatCount <= 0 || (treatHighLoopCountAsInfinite && repeatCount >= 0x7F);
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

    private static uint PackShort(byte status, byte data1, byte data2)
    {
        return (uint)(status | (data1 << 8) | (data2 << 16));
    }

    private readonly record struct LoopFrame(int StartIndex, int ParentIp, int Iteration = 0);
    private readonly record struct LoopScanFrame(int StartIndex, int ParentIp, int Iteration = 0);
    private readonly record struct PendingNoteOff(int Channel, int Note, long Tick, int Version);
    private readonly record struct ActiveNoteState(int Channel, long OnTick, long OffTick, int Version);
}
