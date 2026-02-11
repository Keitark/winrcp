using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime;
using System.Threading;
using RcpPlayer.Core.Model;

namespace RcpPlayer.Core.Playback;

public sealed class RcpPlaybackRunner
{
    private const double DispatchLeadTimeMs = 2.0;

    public async Task PlayAsync(
        RcpPlaybackPlan plan,
        IMidiOutput output,
        CancellationToken cancellationToken,
        IProgress<double>? progress = null,
        Action<ScheduledMidiEvent>? onEventDispatched = null,
        Func<ScheduledMidiEvent, bool>? shouldSendEvent = null,
        Action<PlaybackRunDiagnostics>? onRunDiagnostics = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(output);

        var events = plan.MidiEvents;
        if (events.Count == 0)
        {
            progress?.Report(1.0);
            return;
        }

        await Task.Run(
            () => RunPlaybackLoop(plan, output, cancellationToken, progress, onEventDispatched, shouldSendEvent, onRunDiagnostics),
            CancellationToken.None).ConfigureAwait(false);
    }

    private static void RunPlaybackLoop(
        RcpPlaybackPlan plan,
        IMidiOutput output,
        CancellationToken cancellationToken,
        IProgress<double>? progress,
        Action<ScheduledMidiEvent>? onEventDispatched,
        Func<ScheduledMidiEvent, bool>? shouldSendEvent,
        Action<PlaybackRunDiagnostics>? onRunDiagnostics)
    {
        if (plan.SourceSong is { } sourceSong)
        {
            RunRealtimePlaybackLoop(
                sourceSong,
                plan,
                output,
                cancellationToken,
                progress,
                onEventDispatched,
                shouldSendEvent,
                onRunDiagnostics);
            return;
        }

        var events = plan.MidiEvents;
        var timeline = new TempoTimeline(plan.InitialTempoBpm, plan.TempoEvents, plan.TimeBase);
        var totalTicks = events[^1].Tick;
        var diagnostics = new PlaybackRunDiagnostics();
        using var timerResolution = HighResolutionTimerScope.Create(1);
        using var gcLatency = GcLatencyScope.TryEnterLowLatency();
        using var mmcss = MmcssScope.TryEnterProAudio();

        var thread = Thread.CurrentThread;
        var originalPriority = thread.Priority;
        try
        {
            thread.Priority = ThreadPriority.Highest;
        }
        catch (Exception)
        {
        }

        var startTimestamp = Stopwatch.GetTimestamp();

        try
        {
            var index = 0;
            while (index < events.Count)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var tick = events[index].Tick;
                var dueTimestamp = startTimestamp + timeline.TickToStopwatchTicks(tick);
                var dispatchTimestamp = dueTimestamp - MillisecondsToStopwatchTicks(DispatchLeadTimeMs);
                if (dispatchTimestamp < startTimestamp)
                {
                    dispatchTimestamp = startTimestamp;
                }

                WaitUntilDue(dispatchTimestamp, cancellationToken);

                var now = Stopwatch.GetTimestamp();
                var latenessTicks = Math.Max(0L, now - dueTimestamp);
                var latenessMs = StopwatchTicksToMilliseconds(latenessTicks);
                var tickEventCount = 0;

                while (index < events.Count && events[index].Tick == tick)
                {
                    var e = events[index];
                    diagnostics.TotalEvents++;
                    tickEventCount++;

                    var shouldSend = shouldSendEvent?.Invoke(e) ?? true;
                    if (shouldSend && e.Packet.Kind == MidiMessageKind.Short)
                    {
                        var sendMs = SendShortBlocking(output, e.Packet.ShortMessage, cancellationToken);
                        RecordSendTiming(diagnostics, sendMs);
                        diagnostics.ShortEventsSent++;
                    }
                    else if (shouldSend && e.Packet.SysExData is { Length: > 0 } sysEx)
                    {
                        var sendMs = SendSysExBlocking(output, sysEx, cancellationToken);
                        RecordSendTiming(diagnostics, sendMs);
                        diagnostics.SysExEventsSent++;
                    }
                    else if (!shouldSend)
                    {
                        diagnostics.FilteredEvents++;
                    }

                    if (latenessMs > 2.0)
                    {
                        diagnostics.LateEventsOver2Ms++;
                    }
                    if (latenessMs > 5.0)
                    {
                        diagnostics.LateEventsOver5Ms++;
                    }
                    if (latenessMs > 10.0)
                    {
                        diagnostics.LateEventsOver10Ms++;
                    }
                    diagnostics.MaxLateByMs = Math.Max(diagnostics.MaxLateByMs, latenessMs);

                    onEventDispatched?.Invoke(e);
                    index++;
                }

                diagnostics.MaxEventsPerTick = Math.Max(diagnostics.MaxEventsPerTick, tickEventCount);
            }
        }
        finally
        {
            try
            {
                SendAllNotesOffBlocking(output, CancellationToken.None);
            }
            catch (Exception)
            {
            }

            progress?.Report(1.0);
            onRunDiagnostics?.Invoke(diagnostics);
            try
            {
                thread.Priority = originalPriority;
            }
            catch (Exception)
            {
            }
        }
    }

    private static void RunRealtimePlaybackLoop(
        RcpSong song,
        RcpPlaybackPlan plan,
        IMidiOutput output,
        CancellationToken cancellationToken,
        IProgress<double>? progress,
        Action<ScheduledMidiEvent>? onEventDispatched,
        Func<ScheduledMidiEvent, bool>? shouldSendEvent,
        Action<PlaybackRunDiagnostics>? onRunDiagnostics)
    {
        var sequencer = new RcpRealtimeSequencer(song);
        var timeline = new TempoTimeline(plan.InitialTempoBpm, plan.TempoEvents, plan.TimeBase);
        var diagnostics = new PlaybackRunDiagnostics();
        var knownTotalTicks = plan.MidiEvents.Count > 0 ? plan.MidiEvents[^1].Tick : 0L;

        using var timerResolution = HighResolutionTimerScope.Create(1);
        using var gcLatency = GcLatencyScope.TryEnterLowLatency();
        using var mmcss = MmcssScope.TryEnterProAudio();

        var thread = Thread.CurrentThread;
        var originalPriority = thread.Priority;
        try
        {
            thread.Priority = ThreadPriority.Highest;
        }
        catch (Exception)
        {
        }

        var startTimestamp = Stopwatch.GetTimestamp();

        try
        {
            while (sequencer.TryDequeueNextTickEvents(out var tick, out var tickEvents))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var dueTimestamp = startTimestamp + timeline.TickToStopwatchTicks(tick);
                var dispatchTimestamp = dueTimestamp - MillisecondsToStopwatchTicks(DispatchLeadTimeMs);
                if (dispatchTimestamp < startTimestamp)
                {
                    dispatchTimestamp = startTimestamp;
                }

                WaitUntilDue(dispatchTimestamp, cancellationToken);

                var now = Stopwatch.GetTimestamp();
                var latenessTicks = Math.Max(0L, now - dueTimestamp);
                var latenessMs = StopwatchTicksToMilliseconds(latenessTicks);
                diagnostics.MaxEventsPerTick = Math.Max(diagnostics.MaxEventsPerTick, tickEvents.Count);

                foreach (var e in tickEvents)
                {
                    diagnostics.TotalEvents++;

                    var shouldSend = shouldSendEvent?.Invoke(e) ?? true;
                    if (shouldSend && e.Packet.Kind == MidiMessageKind.Short)
                    {
                        var sendMs = SendShortBlocking(output, e.Packet.ShortMessage, cancellationToken);
                        RecordSendTiming(diagnostics, sendMs);
                        diagnostics.ShortEventsSent++;
                    }
                    else if (shouldSend && e.Packet.SysExData is { Length: > 0 } sysEx)
                    {
                        var sendMs = SendSysExBlocking(output, sysEx, cancellationToken);
                        RecordSendTiming(diagnostics, sendMs);
                        diagnostics.SysExEventsSent++;
                    }
                    else if (!shouldSend)
                    {
                        diagnostics.FilteredEvents++;
                    }

                    if (latenessMs > 2.0)
                    {
                        diagnostics.LateEventsOver2Ms++;
                    }
                    if (latenessMs > 5.0)
                    {
                        diagnostics.LateEventsOver5Ms++;
                    }
                    if (latenessMs > 10.0)
                    {
                        diagnostics.LateEventsOver10Ms++;
                    }
                    diagnostics.MaxLateByMs = Math.Max(diagnostics.MaxLateByMs, latenessMs);

                    onEventDispatched?.Invoke(e);
                }

                if (knownTotalTicks > 0)
                {
                    progress?.Report(Math.Clamp(tick / (double)knownTotalTicks, 0.0, 0.999));
                }
            }
        }
        finally
        {
            try
            {
                SendAllNotesOffBlocking(output, CancellationToken.None);
            }
            catch (Exception)
            {
            }

            progress?.Report(1.0);
            onRunDiagnostics?.Invoke(diagnostics);
            try
            {
                thread.Priority = originalPriority;
            }
            catch (Exception)
            {
            }
        }
    }

    private static void SendAllNotesOffBlocking(IMidiOutput output, CancellationToken cancellationToken)
    {
        for (var channel = 0; channel < 16; channel++)
        {
            var status = (byte)(0xB0 | channel);
            var message = (uint)(status | (123 << 8));
            _ = SendShortBlocking(output, message, cancellationToken);
        }
    }

    private static double SendShortBlocking(IMidiOutput output, uint shortMessage, CancellationToken cancellationToken)
    {
        var start = Stopwatch.GetTimestamp();
        var task = output.SendShortAsync(shortMessage, cancellationToken);
        if (!task.IsCompletedSuccessfully)
        {
            task.GetAwaiter().GetResult();
        }

        var elapsed = Stopwatch.GetTimestamp() - start;
        return StopwatchTicksToMilliseconds(elapsed);
    }

    private static double SendSysExBlocking(IMidiOutput output, byte[] data, CancellationToken cancellationToken)
    {
        var start = Stopwatch.GetTimestamp();
        var task = output.SendSysExAsync(data, cancellationToken);
        if (!task.IsCompletedSuccessfully)
        {
            task.GetAwaiter().GetResult();
        }

        var elapsed = Stopwatch.GetTimestamp() - start;
        return StopwatchTicksToMilliseconds(elapsed);
    }

    private static void RecordSendTiming(PlaybackRunDiagnostics diagnostics, double sendMs)
    {
        diagnostics.MaxSendCallMs = Math.Max(diagnostics.MaxSendCallMs, sendMs);
        if (sendMs > 1.0)
        {
            diagnostics.SendCallsOver1Ms++;
        }
        if (sendMs > 2.0)
        {
            diagnostics.SendCallsOver2Ms++;
        }
        if (sendMs > 5.0)
        {
            diagnostics.SendCallsOver5Ms++;
        }
    }

    private static void WaitUntilDue(long dueTimestamp, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var now = Stopwatch.GetTimestamp();
            var remainingTicks = dueTimestamp - now;
            if (remainingTicks <= 0)
            {
                return;
            }

            var remainingMs = StopwatchTicksToMilliseconds(remainingTicks);
            if (remainingMs > 10.0)
            {
                var sleepMs = Math.Max(1, (int)Math.Floor(remainingMs - 1.5));
                Thread.Sleep(sleepMs);
                continue;
            }

            if (remainingMs > 2.0)
            {
                Thread.Sleep(1);
                continue;
            }

            if (remainingMs > 0.0)
            {
                // Low-CPU mode: avoid busy-spin close to deadline.
                Thread.Sleep(1);
                continue;
            }
        }
    }

    private static long MillisecondsToStopwatchTicks(double milliseconds)
    {
        return (long)Math.Round(milliseconds * Stopwatch.Frequency / 1000.0, MidpointRounding.AwayFromZero);
    }

    private static double StopwatchTicksToMilliseconds(long ticks)
    {
        return ticks * 1000.0 / Stopwatch.Frequency;
    }

    private readonly struct GcLatencyScope : IDisposable
    {
        private readonly GCLatencyMode _previousMode;
        private readonly bool _enabled;

        private GcLatencyScope(GCLatencyMode previousMode, bool enabled)
        {
            _previousMode = previousMode;
            _enabled = enabled;
        }

        public static GcLatencyScope TryEnterLowLatency()
        {
            try
            {
                var current = GCSettings.LatencyMode;
                if (current == GCLatencyMode.SustainedLowLatency)
                {
                    return new GcLatencyScope(current, false);
                }

                GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;
                return new GcLatencyScope(current, true);
            }
            catch (InvalidOperationException)
            {
                return default;
            }
        }

        public void Dispose()
        {
            if (!_enabled)
            {
                return;
            }

            try
            {
                GCSettings.LatencyMode = _previousMode;
            }
            catch (InvalidOperationException)
            {
            }
        }
    }

    private readonly struct HighResolutionTimerScope : IDisposable
    {
        private readonly bool _enabled;
        private readonly uint _period;

        private HighResolutionTimerScope(bool enabled, uint period)
        {
            _enabled = enabled;
            _period = period;
        }

        public static HighResolutionTimerScope Create(uint milliseconds)
        {
            if (!OperatingSystem.IsWindows())
            {
                return default;
            }

            try
            {
                var result = NativeMethods.TimeBeginPeriod(milliseconds);
                return new HighResolutionTimerScope(result == 0, milliseconds);
            }
            catch (DllNotFoundException)
            {
                return default;
            }
            catch (EntryPointNotFoundException)
            {
                return default;
            }
        }

        public void Dispose()
        {
            if (!_enabled || !OperatingSystem.IsWindows())
            {
                return;
            }

            try
            {
                _ = NativeMethods.TimeEndPeriod(_period);
            }
            catch (DllNotFoundException)
            {
            }
            catch (EntryPointNotFoundException)
            {
            }
        }

        private static class NativeMethods
        {
            [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
            public static extern uint TimeBeginPeriod(uint period);

            [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
            public static extern uint TimeEndPeriod(uint period);
        }
    }

    private readonly struct MmcssScope : IDisposable
    {
        private readonly nint _taskHandle;

        private MmcssScope(nint taskHandle)
        {
            _taskHandle = taskHandle;
        }

        public static MmcssScope TryEnterProAudio()
        {
            if (!OperatingSystem.IsWindows())
            {
                return default;
            }

            try
            {
                uint taskIndex = 0;
                var handle = NativeMethods.AvSetMmThreadCharacteristics("Pro Audio", ref taskIndex);
                if (handle == 0)
                {
                    return default;
                }

                _ = NativeMethods.AvSetMmThreadPriority(handle, AvrtPriority.High);
                return new MmcssScope(handle);
            }
            catch (DllNotFoundException)
            {
                return default;
            }
            catch (EntryPointNotFoundException)
            {
                return default;
            }
        }

        public void Dispose()
        {
            if (_taskHandle == 0 || !OperatingSystem.IsWindows())
            {
                return;
            }

            try
            {
                _ = NativeMethods.AvRevertMmThreadCharacteristics(_taskHandle);
            }
            catch (DllNotFoundException)
            {
            }
            catch (EntryPointNotFoundException)
            {
            }
        }

        private enum AvrtPriority
        {
            Low = -1,
            Normal = 0,
            High = 1,
            Critical = 2
        }

        private static class NativeMethods
        {
            [DllImport("avrt.dll", CharSet = CharSet.Unicode, EntryPoint = "AvSetMmThreadCharacteristicsW")]
            public static extern nint AvSetMmThreadCharacteristics(string taskName, ref uint taskIndex);

            [DllImport("avrt.dll", EntryPoint = "AvSetMmThreadPriority")]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool AvSetMmThreadPriority(nint avrtHandle, AvrtPriority priority);

            [DllImport("avrt.dll", EntryPoint = "AvRevertMmThreadCharacteristics")]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool AvRevertMmThreadCharacteristics(nint avrtHandle);
        }
    }

    private sealed class TempoTimeline
    {
        private readonly List<Segment> _segments = [];
        private readonly int _timeBase;

        public TempoTimeline(double initialTempoBpm, IReadOnlyList<TempoEvent> tempoEvents, int timeBase)
        {
            _timeBase = Math.Max(timeBase, 1);
            var sorted = tempoEvents.OrderBy(t => t.Tick).ToList();

            var tick = 0L;
            var ms = 0.0;
            var bpm = Math.Max(initialTempoBpm, 1.0);

            foreach (var t in sorted)
            {
                if (t.Tick < tick)
                {
                    continue;
                }

                if (t.Tick > tick)
                {
                    _segments.Add(new Segment(tick, ms, bpm));
                    ms += (t.Tick - tick) * MsPerTick(bpm, _timeBase);
                    tick = t.Tick;
                }

                bpm = Math.Max(t.Bpm, 1.0);
            }

            _segments.Add(new Segment(tick, ms, bpm));
        }

        public double TickToMilliseconds(long tick)
        {
            if (_segments.Count == 0)
            {
                return 0.0;
            }

            var index = FindSegmentIndex(tick);
            var seg = _segments[index];
            return seg.StartMilliseconds + (tick - seg.StartTick) * MsPerTick(seg.Bpm, _timeBase);
        }

        public long TickToStopwatchTicks(long tick)
        {
            return MillisecondsToStopwatchTicks(TickToMilliseconds(tick));
        }

        private int FindSegmentIndex(long tick)
        {
            var left = 0;
            var right = _segments.Count - 1;
            var best = 0;

            while (left <= right)
            {
                var mid = (left + right) / 2;
                var value = _segments[mid].StartTick;
                if (value <= tick)
                {
                    best = mid;
                    left = mid + 1;
                }
                else
                {
                    right = mid - 1;
                }
            }

            return best;
        }

        private static double MsPerTick(double bpm, int ppqn)
        {
            return 60000.0 / (bpm * ppqn);
        }

        private readonly record struct Segment(long StartTick, double StartMilliseconds, double Bpm);
    }
}
