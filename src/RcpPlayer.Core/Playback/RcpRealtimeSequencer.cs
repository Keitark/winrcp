using RcpPlayer.Core.Model;

namespace RcpPlayer.Core.Playback;

internal readonly record struct SequencedTempoModifier(long Tick, int Ratio, int Gradation);

internal sealed class RcpRealtimeSequencer
{
    private readonly List<TrackRuntimeState> _tracks;

    public RcpRealtimeSequencer(RcpSong song, bool ignoreMutedTracks = true)
    {
        ArgumentNullException.ThrowIfNull(song);
        _tracks = song.Tracks
            .Where(t => !ignoreMutedTracks || !t.IsMuted)
            .OrderBy(t => t.TrackId)
            .Select(t => new TrackRuntimeState(song, t))
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
        }

        return hasActive;
    }

    private sealed class TrackRuntimeState
    {
        private const int MaxInterpreterStepsPerPump = 200_000;
        private const int MaxInterpreterStepsPerSameTickFrame = 20_000;

        private readonly RcpSong _song;
        private readonly IReadOnlyList<RcpEvent> _events;
        private readonly IReadOnlyList<int> _measureStartIndices;
        private readonly Stack<LoopFrame> _loops = new();
        private readonly Stack<int> _repeatMeasureReturnIps = new();
        private readonly Dictionary<(int Channel, int Note), ActiveNoteState> _activeNotes = new();
        private readonly PriorityQueue<PendingNoteOff, (long Tick, long Sequence)> _pendingNoteOffs = new();
        private readonly PriorityQueue<ScheduledMidiEvent, (long Tick, long Sequence)> _readyEvents = new();
        private readonly PriorityQueue<SequencedTempoModifier, (long Tick, long Sequence)> _readyTempoModifiers = new();

        private long _nextSequence;
        private long _tick;
        private int _ip;
        private int _channel;
        private bool _ended;

        private int _rolandDev = 0x10;
        private int _rolandModel = 0x16;
        private int _rolandBaseH;
        private int _rolandBaseM = 0x10;

        private int _yamahaDev = 0x10;
        private int _yamahaModel = 0x4C;
        private int _yamahaBaseH;
        private int _yamahaBaseM;

        public TrackRuntimeState(RcpSong song, RcpTrack track)
        {
            _song = song;
            _events = track.Events;
            _measureStartIndices = BuildMeasureStartIndices(_events);
            _channel = ClampChannel(track.DefaultChannel);
            IsActive = _events.Count > 0;
            HasPlayedOnce = !IsActive;
        }

        public bool IsActive { get; }

        public bool HasPlayedOnce { get; private set; }

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

            if (cmd < 0x80)
            {
                PruneExpiredActiveNotes();
                EmitNoteEvent(e);
                _tick += Math.Max(e.DelayTicks, 0);
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
                    if (index < _song.UserExclusives.Count)
                    {
                        var body = _song.UserExclusives[index].DataWithoutLeadingF0;
                        var ex = ExpandSysExTemplate(body, Clamp7Bit(e.Param1), Clamp7Bit(e.Param2), _channel);
                        EnqueueReadySysEx(_tick, ex);
                    }

                    _tick += Math.Max(e.DelayTicks, 0);
                    _ip++;
                    break;
                }
                case 0x98:
                {
                    var raw = new List<byte> { (byte)Clamp7Bit(e.Param1), (byte)Clamp7Bit(e.Param2) };
                    var isG36Event = e.RawLength >= 6;
                    var localIp = _ip + 1;
                    while (localIp < _events.Count && _events[localIp].CommandOrNote == 0xF7)
                    {
                        AppendContinuationBytes(raw, _events[localIp], isG36Event);
                        localIp++;
                    }

                    var ex = ExpandSysExTemplate(raw.ToArray(), Clamp7Bit(e.Param1), Clamp7Bit(e.Param2), _channel);
                    EnqueueReadySysEx(_tick, ex);
                    _tick += Math.Max(e.DelayTicks, 0);
                    _ip = localIp;
                    break;
                }
                case 0x99:
                    _tick += Math.Max(e.DelayTicks, 0);
                    _ip = SkipContinuationEvents(_events, _ip + 1);
                    break;
                case 0xE1:
                    EnqueueReadyShort(_tick, 0xB0 | _channel, 32, Clamp7Bit(e.Param2));
                    EnqueueReadyShort(_tick, 0xC0 | _channel, Clamp7Bit(e.Param1), 0);
                    _tick += Math.Max(e.DelayTicks, 0);
                    _ip++;
                    break;
                case 0xE2:
                    EnqueueReadyShort(_tick, 0xB0 | _channel, 0, Clamp7Bit(e.Param2));
                    EnqueueReadyShort(_tick, 0xC0 | _channel, Clamp7Bit(e.Param1), 0);
                    _tick += Math.Max(e.DelayTicks, 0);
                    _ip++;
                    break;
                case 0xE5:
                    _tick += Math.Max(e.DelayTicks, 0);
                    _ip++;
                    break;
                case 0xEA:
                    EnqueueReadyShort(_tick, 0xD0 | _channel, Clamp7Bit(e.Param1), 0);
                    _tick += Math.Max(e.DelayTicks, 0);
                    _ip++;
                    break;
                case 0xEB:
                    EnqueueReadyShort(_tick, 0xB0 | _channel, Clamp7Bit(e.Param1), Clamp7Bit(e.Param2));
                    _tick += Math.Max(e.DelayTicks, 0);
                    _ip++;
                    break;
                case 0xEC:
                    EnqueueReadyShort(_tick, 0xC0 | _channel, Clamp7Bit(e.Param1), 0);
                    _tick += Math.Max(e.DelayTicks, 0);
                    _ip++;
                    break;
                case 0xED:
                    EnqueueReadyShort(_tick, 0xA0 | _channel, Clamp7Bit(e.Param1), Clamp7Bit(e.Param2));
                    _tick += Math.Max(e.DelayTicks, 0);
                    _ip++;
                    break;
                case 0xEE:
                    EnqueueReadyShort(_tick, 0xE0 | _channel, Clamp7Bit(e.Param1), Clamp7Bit(e.Param2));
                    _tick += Math.Max(e.DelayTicks, 0);
                    _ip++;
                    break;
                case 0xE6:
                    _channel = ClampChannel(NormalizeCommandChannel(e.Param1));
                    _tick += Math.Max(e.DelayTicks, 0);
                    _ip++;
                    break;
                case 0xE7:
                    EnqueueTempoModifier(_tick, Math.Max(1, e.Param1), Clamp8Bit(e.Param2));
                    _tick += Math.Max(e.DelayTicks, 0);
                    _ip++;
                    break;
                case 0xC0:
                    EnqueueReadySysEx(_tick, new byte[] { 0xF0, 0x43, (byte)(0x10 + _channel), 0x08, (byte)Clamp7Bit(e.Param1), (byte)Clamp7Bit(e.Param2), 0xF7 });
                    _tick += Math.Max(e.DelayTicks, 0);
                    _ip++;
                    break;
                case 0xC1:
                    EnqueueReadySysEx(_tick, new byte[] { 0xF0, 0x43, (byte)(0x10 + _channel), 0x00, (byte)Clamp7Bit(e.Param1), (byte)Clamp7Bit(e.Param2), 0xF7 });
                    _tick += Math.Max(e.DelayTicks, 0);
                    _ip++;
                    break;
                case 0xC2:
                    EnqueueReadySysEx(_tick, new byte[] { 0xF0, 0x43, (byte)(0x10 + _channel), 0x04, (byte)Clamp7Bit(e.Param1), (byte)Clamp7Bit(e.Param2), 0xF7 });
                    _tick += Math.Max(e.DelayTicks, 0);
                    _ip++;
                    break;
                case 0xC3:
                    EnqueueReadySysEx(_tick, new byte[] { 0xF0, 0x43, (byte)(0x10 + _channel), 0x11, (byte)Clamp7Bit(e.Param1), (byte)Clamp7Bit(e.Param2), 0xF7 });
                    _tick += Math.Max(e.DelayTicks, 0);
                    _ip++;
                    break;
                case 0xC5:
                    EnqueueReadySysEx(_tick, new byte[] { 0xF0, 0x43, (byte)(0x10 + _channel), 0x15, (byte)Clamp7Bit(e.Param1), (byte)Clamp7Bit(e.Param2), 0xF7 });
                    _tick += Math.Max(e.DelayTicks, 0);
                    _ip++;
                    break;
                case 0xC6:
                    EnqueueReadySysEx(_tick, new byte[] { 0xF0, 0x43, 0x75, (byte)_channel, 0x10, (byte)Clamp7Bit(e.Param1), (byte)Clamp7Bit(e.Param2), 0xF7 });
                    _tick += Math.Max(e.DelayTicks, 0);
                    _ip++;
                    break;
                case 0xC7:
                    EnqueueReadySysEx(_tick, new byte[] { 0xF0, 0x43, (byte)(0x10 + _channel), 0x12, (byte)Clamp7Bit(e.Param1), (byte)Clamp7Bit(e.Param2), 0xF7 });
                    _tick += Math.Max(e.DelayTicks, 0);
                    _ip++;
                    break;
                case 0xC8:
                    EnqueueReadySysEx(_tick, new byte[] { 0xF0, 0x43, (byte)(0x10 + _channel), 0x13, (byte)Clamp7Bit(e.Param1), (byte)Clamp7Bit(e.Param2), 0xF7 });
                    _tick += Math.Max(e.DelayTicks, 0);
                    _ip++;
                    break;
                case 0xC9:
                    EnqueueReadySysEx(_tick, new byte[] { 0xF0, 0x43, (byte)(0x10 + _channel), 0x10, (byte)Clamp7Bit(e.Param1), (byte)Clamp7Bit(e.Param2), 0xF7 });
                    _tick += Math.Max(e.DelayTicks, 0);
                    _ip++;
                    break;
                case 0xCA:
                    EnqueueReadySysEx(_tick, new byte[] { 0xF0, 0x43, (byte)(0x10 + _channel), 0x10, 0x7B, (byte)Clamp7Bit(e.Param1), (byte)Clamp7Bit(e.Param2), 0xF7 });
                    _tick += Math.Max(e.DelayTicks, 0);
                    _ip++;
                    break;
                case 0xCB:
                    EnqueueReadySysEx(_tick, new byte[] { 0xF0, 0x43, (byte)(0x10 + _channel), 0x10, 0x7C, (byte)Clamp7Bit(e.Param1), (byte)Clamp7Bit(e.Param2), 0xF7 });
                    _tick += Math.Max(e.DelayTicks, 0);
                    _ip++;
                    break;
                case 0xCC:
                    EnqueueReadySysEx(_tick, new byte[] { 0xF0, 0x43, (byte)(0x10 + _channel), 0x1B, (byte)Clamp7Bit(e.Param1), (byte)Clamp7Bit(e.Param2), 0xF7 });
                    _tick += Math.Max(e.DelayTicks, 0);
                    _ip++;
                    break;
                case 0xCD:
                    EnqueueReadySysEx(_tick, new byte[] { 0xF0, 0x43, (byte)(0x10 + _channel), 0x18, (byte)Clamp7Bit(e.Param1), (byte)Clamp7Bit(e.Param2), 0xF7 });
                    _tick += Math.Max(e.DelayTicks, 0);
                    _ip++;
                    break;
                case 0xCE:
                    EnqueueReadySysEx(_tick, new byte[] { 0xF0, 0x43, (byte)(0x10 + _channel), 0x19, (byte)Clamp7Bit(e.Param1), (byte)Clamp7Bit(e.Param2), 0xF7 });
                    _tick += Math.Max(e.DelayTicks, 0);
                    _ip++;
                    break;
                case 0xCF:
                    EnqueueReadySysEx(_tick, new byte[] { 0xF0, 0x43, (byte)(0x10 + _channel), 0x1A, (byte)Clamp7Bit(e.Param1), (byte)Clamp7Bit(e.Param2), 0xF7 });
                    _tick += Math.Max(e.DelayTicks, 0);
                    _ip++;
                    break;
                case 0xD0:
                    _yamahaBaseH = Clamp7Bit(e.Param1);
                    _yamahaBaseM = Clamp7Bit(e.Param2);
                    _tick += Math.Max(e.DelayTicks, 0);
                    _ip++;
                    break;
                case 0xD1:
                    _yamahaDev = Clamp7Bit(e.Param1);
                    _yamahaModel = Clamp7Bit(e.Param2);
                    _tick += Math.Max(e.DelayTicks, 0);
                    _ip++;
                    break;
                case 0xD2:
                    EnqueueReadySysEx(_tick, new byte[]
                    {
                        0xF0, 0x43, (byte)_yamahaDev, (byte)_yamahaModel,
                        (byte)_yamahaBaseH, (byte)_yamahaBaseM, (byte)Clamp7Bit(e.Param1), (byte)Clamp7Bit(e.Param2), 0xF7
                    });
                    _tick += Math.Max(e.DelayTicks, 0);
                    _ip++;
                    break;
                case 0xD3:
                    EnqueueReadySysEx(_tick, new byte[]
                    {
                        0xF0, 0x43, 0x10, 0x4C,
                        (byte)_yamahaBaseH, (byte)_yamahaBaseM, (byte)Clamp7Bit(e.Param1), (byte)Clamp7Bit(e.Param2), 0xF7
                    });
                    _tick += Math.Max(e.DelayTicks, 0);
                    _ip++;
                    break;
                case 0xDC:
                    EnqueueReadySysEx(_tick, new byte[] { 0xF0, 0x41, 0x32, (byte)_channel, (byte)Clamp7Bit(e.Param1), (byte)Clamp7Bit(e.Param2), 0xF7 });
                    _tick += Math.Max(e.DelayTicks, 0);
                    _ip++;
                    break;
                case 0xDD:
                    _rolandBaseH = Clamp7Bit(e.Param1);
                    _rolandBaseM = Clamp7Bit(e.Param2);
                    _tick += Math.Max(e.DelayTicks, 0);
                    _ip++;
                    break;
                case 0xDE:
                {
                    var addrL = Clamp7Bit(e.Param1);
                    var parameter = Clamp7Bit(e.Param2);
                    var sum = _rolandBaseH + _rolandBaseM + addrL + parameter;
                    var check = (128 - (sum & 0x7F)) & 0x7F;
                    EnqueueReadySysEx(_tick, new byte[]
                    {
                        0xF0, 0x41, (byte)_rolandDev, (byte)_rolandModel, 0x12,
                        (byte)_rolandBaseH, (byte)_rolandBaseM, (byte)addrL, (byte)parameter, (byte)check, 0xF7
                    });
                    _tick += Math.Max(e.DelayTicks, 0);
                    _ip++;
                    break;
                }
                case 0xDF:
                    _rolandDev = Clamp7Bit(e.Param1);
                    _rolandModel = Clamp7Bit(e.Param2);
                    _tick += Math.Max(e.DelayTicks, 0);
                    _ip++;
                    break;
                case 0xF9:
                    _loops.Push(new LoopFrame(_ip + 1));
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
                    if (e.DelayTicks <= 0)
                    {
                        MarkPlayedOnce();
                        _loops.Push(top);
                        _ip = top.StartIndex;
                        break;
                    }

                    var repeatCount = Math.Max(e.DelayTicks, 1);
                    if (top.Iteration + 1 < repeatCount)
                    {
                        top = top with { Iteration = top.Iteration + 1 };
                        _loops.Push(top);
                        _ip = top.StartIndex;
                    }
                    else
                    {
                        _ip++;
                    }

                    break;
                }
                case 0xFD:
                    _tick += Math.Max(e.DelayTicks, 0);
                    _ip = _repeatMeasureReturnIps.Count > 0
                        ? _repeatMeasureReturnIps.Pop()
                        : _ip + 1;
                    break;
                case 0xFC:
                {
                    var measureId = GetRepeatMeasureId(e);
                    if (measureId >= 0 && measureId < _measureStartIndices.Count)
                    {
                        var destination = _measureStartIndices[measureId];
                        if (destination >= 0 &&
                            destination < _events.Count &&
                            destination != _ip &&
                            _repeatMeasureReturnIps.Count < 64)
                        {
                            _repeatMeasureReturnIps.Push(_ip + 1);
                            _ip = destination;
                            break;
                        }
                    }

                    _ip++;
                    break;
                }
                case 0xF7:
                    _ip++;
                    break;
                case 0xF6:
                    _ip = SkipContinuationEvents(_events, _ip + 1);
                    break;
                case 0xF5:
                    _ip++;
                    break;
                case 0xFE:
                    MarkPlayedOnce();
                    _ip = _events.Count;
                    break;
                default:
                    if (cmd < 0xF0)
                    {
                        _tick += Math.Max(e.DelayTicks, 0);
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

        private void EmitNoteEvent(RcpEvent e)
        {
            var note = e.CommandOrNote;
            var velocity = Clamp7Bit(e.Param2);
            var gate = Math.Max(e.Param1, 0);
            if (gate <= 0)
            {
                return;
            }

            var key = (_channel, (int)note);
            var offTick = _tick + gate;
            if (_activeNotes.TryGetValue(key, out var active))
            {
                var version = active.Version + 1;
                _activeNotes[key] = new ActiveNoteState(offTick, version);
                EnqueuePendingNoteOff(_channel, note, offTick, version);
                return;
            }

            EnqueueReadyShort(_tick, 0x90 | _channel, note, velocity);
            _activeNotes[key] = new ActiveNoteState(offTick, 1);
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
            while (_pendingNoteOffs.TryPeek(out var candidate, out var priority) && priority.Tick < _tick)
            {
                _pendingNoteOffs.Dequeue();
                EmitPendingNoteOffIfStillActive(candidate);
            }
        }

        private void FlushPendingNoteOffsAtTick(long tick)
        {
            while (_pendingNoteOffs.TryPeek(out var candidate, out var priority) && priority.Tick == tick)
            {
                _pendingNoteOffs.Dequeue();
                EmitPendingNoteOffIfStillActive(candidate);
            }
        }

        private void EmitPendingNoteOffIfStillActive(PendingNoteOff candidate)
        {
            var key = (candidate.Channel, candidate.Note);
            if (!_activeNotes.TryGetValue(key, out var active))
            {
                return;
            }

            if (active.Version != candidate.Version || active.OffTick != candidate.Tick)
            {
                return;
            }

            _activeNotes.Remove(key);
            EnqueueReadyShort(candidate.Tick, 0x80 | candidate.Channel, candidate.Note, 0);
        }

        private void PruneExpiredActiveNotes()
        {
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

        private void EnqueueReadyShort(long tick, int status, int data1, int data2)
        {
            _readyEvents.Enqueue(
                new ScheduledMidiEvent
                {
                    Tick = tick,
                    Packet = new MidiEventPacket
                    {
                        Kind = MidiMessageKind.Short,
                        ShortMessage = PackShort((byte)status, (byte)Clamp7Bit(data1), (byte)Clamp7Bit(data2)),
                        SysExData = null
                    }
                },
                (tick, _nextSequence++));
        }

        private void EnqueueReadySysEx(long tick, byte[] data)
        {
            _readyEvents.Enqueue(
                new ScheduledMidiEvent
                {
                    Tick = tick,
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

    private static uint PackShort(byte status, byte data1, byte data2)
    {
        return (uint)(status | (data1 << 8) | (data2 << 16));
    }

    private readonly record struct LoopFrame(int StartIndex, int Iteration = 0);
    private readonly record struct PendingNoteOff(int Channel, int Note, long Tick, int Version);
    private readonly record struct ActiveNoteState(long OffTick, int Version);
}
