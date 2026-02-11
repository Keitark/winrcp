using System.Collections.ObjectModel;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;
using RcpPlayer.App.Midi;
using RcpPlayer.Core.Model;
using RcpPlayer.Core.Parsing;
using RcpPlayer.Core.Playback;

namespace RcpPlayer.App;

public partial class MainWindow : Window
{
    private const int LcdSize = 16;
    private const int MaxEventLogEntries = 220;
    private const int EventLogFlushBatchSize = 48;
    private const int MaxUiEventsPerFrame = 32;
    // Realtime event log is enabled by default.
    // Set WINRCP_REALTIME_LOG=0 to disable when investigating performance.
    private static readonly bool EnableRealtimeEventLog =
        !string.Equals(Environment.GetEnvironmentVariable("WINRCP_REALTIME_LOG"), "0", StringComparison.Ordinal);
    private const bool SendPlaybackInitReset = true;
    private const double PianoRollTickScale = 1.40;
    private const double PianoRollNoteHeight = 9.0;
    private const double PianoRollLeftPadding = 42.0;
    private const double PianoRollTopPadding = 10.0;
    private const double LoadedFileScrollSpeedPerTick = 0.9;
    private const double LoadedFileScrollGap = 24.0;
    private const double LoadedFileScrollPauseMsDefault = 700.0;
    private static readonly string[] GmProgramNames =
    [
        "Acoustic Piano", "Bright Piano", "Electric Grand", "Honky-tonk", "Electric Piano 1", "Electric Piano 2", "Harpsichord", "Clavinet",
        "Celesta", "Glockenspiel", "Music Box", "Vibraphone", "Marimba", "Xylophone", "Tubular Bells", "Dulcimer",
        "Drawbar Organ", "Percussive Organ", "Rock Organ", "Church Organ", "Reed Organ", "Accordion", "Harmonica", "Tango Accordion",
        "Nylon Guitar", "Steel Guitar", "Jazz Guitar", "Clean Guitar", "Muted Guitar", "Overdrive Guitar", "Distortion Guitar", "Guitar Harmonics",
        "Acoustic Bass", "Finger Bass", "Pick Bass", "Fretless Bass", "Slap Bass 1", "Slap Bass 2", "Synth Bass 1", "Synth Bass 2",
        "Violin", "Viola", "Cello", "Contrabass", "Tremolo Strings", "Pizzicato Strings", "Orchestral Harp", "Timpani",
        "String Ensemble 1", "String Ensemble 2", "Synth Strings 1", "Synth Strings 2", "Choir Aahs", "Voice Oohs", "Synth Voice", "Orchestra Hit",
        "Trumpet", "Trombone", "Tuba", "Muted Trumpet", "French Horn", "Brass Section", "Synth Brass 1", "Synth Brass 2",
        "Soprano Sax", "Alto Sax", "Tenor Sax", "Baritone Sax", "Oboe", "English Horn", "Bassoon", "Clarinet",
        "Piccolo", "Flute", "Recorder", "Pan Flute", "Blown Bottle", "Shakuhachi", "Whistle", "Ocarina",
        "Square Lead", "Saw Lead", "Calliope Lead", "Chiff Lead", "Charang Lead", "Voice Lead", "Fifths Lead", "Bass+Lead",
        "New Age Pad", "Warm Pad", "Polysynth Pad", "Choir Pad", "Bowed Pad", "Metal Pad", "Halo Pad", "Sweep Pad",
        "Rain", "Soundtrack", "Crystal", "Atmosphere", "Brightness", "Goblins", "Echoes", "Sci-Fi",
        "Sitar", "Banjo", "Shamisen", "Koto", "Kalimba", "Bagpipe", "Fiddle", "Shanai",
        "Tinkle Bell", "Agogo", "Steel Drums", "Woodblock", "Taiko Drum", "Melodic Tom", "Synth Drum", "Reverse Cymbal",
        "Guitar Fret Noise", "Breath Noise", "Seashore", "Bird Tweet", "Telephone Ring", "Helicopter", "Applause", "Gunshot"
    ];

    private readonly RcpParser _parser = new();
    private readonly StandardMidiParser _standardMidiParser = new();
    private readonly RcpSequenceBuilder _builder = new();
    private readonly RcpPlaybackRunner _runner = new();
    private readonly IMidiOutput _midiOutput = new WinRtMidiOutput();
    private readonly Sc88DisplayState _lcdState = new();

    private readonly ObservableCollection<string> _eventLog = [];
    private readonly ConcurrentQueue<string> _pendingEventLog = [];
    private readonly ConcurrentQueue<ScheduledMidiEvent> _pendingUiEvents = [];
    private readonly double[] _partLevelTargets = new double[16];
    private readonly double[] _partLevels = new double[16];
    private readonly int[] _programByChannel = new int[16];
    private readonly int[] _partVolume = new int[16];
    private readonly int[] _partPan = new int[16];
    private readonly int[] _partReverb = new int[16];
    private readonly int[] _partChorus = new int[16];
    private readonly int[] _partKeyShift = new int[16];
    private readonly int[] _partDelay = new int[16];
    private readonly int[] _rpnMsb = new int[16];
    private readonly int[] _rpnLsb = new int[16];
    private readonly bool[] _partMuted = new bool[16];
    private readonly Border[] _lcdCells = new Border[LcdSize * LcdSize];
    private readonly bool[] _lcdCellState = new bool[LcdSize * LcdSize];
    private readonly DispatcherTimer _meterDecayTimer;
    private readonly DispatcherTimer _playbackUiTimer;
    private readonly DispatcherTimer _eventLogFlushTimer;
    private readonly DispatcherTimer _loadedFileScrollTimer;
    private readonly Stopwatch _playbackUiStopwatch = new();
    private readonly Brush _lcdOnBrush = new SolidColorBrush(Color.FromRgb(64, 48, 26));
    private readonly Brush _lcdOffBrush = new SolidColorBrush(Color.FromRgb(146, 122, 76));
    private readonly Brush _lcdGridBrush = new SolidColorBrush(Color.FromRgb(121, 99, 58));
    private readonly Brush[] _pianoRollChannelBrushes = CreatePianoRollChannelBrushes();
    private readonly Brush _pianoRollGridBrush = new SolidColorBrush(Color.FromRgb(209, 196, 166));
    private readonly Brush _pianoRollOctaveBrush = new SolidColorBrush(Color.FromRgb(187, 171, 133));
    private readonly Brush _pianoRollPlayheadBrush = new SolidColorBrush(Color.FromRgb(166, 33, 33));
    private readonly List<PianoRollNoteVisual> _pianoRollNoteVisuals = [];
    private readonly TranslateTransform _pianoRollAutoScrollTransform = new();
    private static readonly JsonSerializerOptions PreferencesJsonOptions = new() { WriteIndented = true };
    private readonly string _preferencesPath = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "winrcp", "settings.json");

    private RcpSong? _song;
    private RcpPlaybackPlan? _plan;
    private CancellationTokenSource? _playbackCts;
    private long _pianoRollTotalTicks;
    private double _pianoRollScrollTargetOffset;
    private double _pianoRollScrollCurrentOffset;
    private double _playheadProgressRatio;
    private bool _isPianoRollHardwareScrollEnabled;
    private bool _isPianoRollRenderingHooked;
    private double _playbackTotalMilliseconds;
    private long _playbackTotalTicksForUi;
    private long _playbackHintTick;
    private readonly List<PlaybackTempoSegment> _playbackTempoSegments = [];
    private int _logScrollSkipCounter;
    private string? _summaryFormatLabelOverride;
    private bool _isAllDisplayMode = true;
    private int _selectedPartIndex;
    private bool _isTitleScrollActive;
    private TaskCompletionSource<bool>? _playbackStoppedSignal;
    private AppPreferences _preferences = new();
    private bool _suppressEndpointSelectionPersistence;
    private double _loadedFileScrollOffset;
    private double _loadedFileScrollPauseMs;

    public MainWindow()
    {
        InitializeComponent();
        InitializeLcdMatrix();

        EventLogList.ItemsSource = _eventLog;
        _meterDecayTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(33)
        };
        _meterDecayTimer.Tick += (_, _) => DecayMeters();
        _meterDecayTimer.Start();

        _playbackUiTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        _playbackUiTimer.Tick += PlaybackUiTick;

        _eventLogFlushTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(50)
        };
        _eventLogFlushTimer.Tick += FlushPendingEventLog;
        _eventLogFlushTimer.Start();

        _loadedFileScrollTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(33)
        };
        _loadedFileScrollTimer.Tick += LoadedFileScrollTick;

        PianoRollCanvas.RenderTransform = _pianoRollAutoScrollTransform;
        PianoRollScroll.SizeChanged += PianoRollScrollSizeChanged;
        PianoRollScroll.ScrollChanged += PianoRollScrollScrollChanged;
        LcdMatrixGrid.SizeChanged += (_, _) => LayoutLcdMatrixCells();
        LoadedFileViewport.SizeChanged += (_, _) => RefreshLoadedFileScroll();
        LoadedFileText.SizeChanged += (_, _) => RefreshLoadedFileScroll();

        ResetDisplayState();
    }

    private void InitializeLcdMatrix()
    {
        if (_lcdOnBrush is Freezable on && on.CanFreeze)
        {
            on.Freeze();
        }

        if (_lcdOffBrush is Freezable off && off.CanFreeze)
        {
            off.Freeze();
        }

        if (_lcdGridBrush is Freezable grid && grid.CanFreeze)
        {
            grid.Freeze();
        }

        if (_pianoRollGridBrush is Freezable gridLine && gridLine.CanFreeze)
        {
            gridLine.Freeze();
        }

        if (_pianoRollOctaveBrush is Freezable octaveLine && octaveLine.CanFreeze)
        {
            octaveLine.Freeze();
        }

        if (_pianoRollPlayheadBrush is Freezable playhead && playhead.CanFreeze)
        {
            playhead.Freeze();
        }

        LcdMatrixGrid.Children.Clear();
        for (var i = 0; i < _lcdCells.Length; i++)
        {
            var cell = new Border
            {
                Background = _lcdOffBrush,
                BorderBrush = _lcdGridBrush,
                BorderThickness = new Thickness(0),
                Margin = new Thickness(0),
                CornerRadius = new CornerRadius(0),
                SnapsToDevicePixels = true,
                UseLayoutRounding = true
            };
            _lcdCells[i] = cell;
            _lcdCellState[i] = false;
            LcdMatrixGrid.Children.Add(cell);
        }

        LayoutLcdMatrixCells();
    }

    private void LayoutLcdMatrixCells()
    {
        if (_lcdCells.Length == 0)
        {
            return;
        }

        var width = LcdMatrixGrid.ActualWidth;
        var height = LcdMatrixGrid.ActualHeight;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        const double gapX = 1.0;
        const double gapY = 1.0;
        var usableWidth = Math.Max(1.0, Math.Round(width));
        var usableHeight = Math.Max(1.0, Math.Round(height));
        var cellWidth = Math.Max(1.0, Math.Floor((usableWidth - gapX * (LcdSize - 1)) / LcdSize));
        var cellHeight = Math.Max(1.0, Math.Floor((usableHeight - gapY * (LcdSize - 1)) / LcdSize));
        var totalUsedWidth = cellWidth * LcdSize + gapX * (LcdSize - 1);
        var totalUsedHeight = cellHeight * LcdSize + gapY * (LcdSize - 1);
        var offsetX = Math.Floor((usableWidth - totalUsedWidth) * 0.5);
        var offsetY = Math.Floor((usableHeight - totalUsedHeight) * 0.5);

        for (var row = 0; row < LcdSize; row++)
        {
            for (var col = 0; col < LcdSize; col++)
            {
                var index = row * LcdSize + col;
                var cell = _lcdCells[index];
                cell.Width = cellWidth;
                cell.Height = cellHeight;
                Canvas.SetLeft(cell, offsetX + col * (cellWidth + gapX));
                Canvas.SetTop(cell, offsetY + row * (cellHeight + gapY));
            }
        }
    }

    private async void WindowLoaded(object sender, RoutedEventArgs e)
    {
        LoadPreferences();
        await RefreshEndpointsAsync();
    }

    private async void WindowClosing(object? sender, CancelEventArgs e)
    {
        _meterDecayTimer.Stop();
        _playbackUiTimer.Stop();
        _eventLogFlushTimer.Stop();
        _loadedFileScrollTimer.Stop();
        StopPianoRollHardwareScroll(applyOffsetToScrollViewer: false);
        _playbackCts?.Cancel();
        await SendAllNotesOffForChannelsAsync(Enumerable.Range(0, 16));
        _playbackCts?.Dispose();
        await _midiOutput.CloseAsync();
        await _midiOutput.DisposeAsync();
    }

    private void TopDragRegionMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            WindowState = WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;
            return;
        }

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
        }
    }

    private void MinimizeWindowClick(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void CloseWindowClick(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private async Task RefreshEndpointsAsync()
    {
        StatusText.Text = "Refreshing MIDI endpoints...";
        try
        {
            var endpoints = await _midiOutput.GetEndpointsAsync();

            MidiEndpointInfo? preferredEndpoint = null;
            if (!string.IsNullOrWhiteSpace(_preferences.LastMidiEndpointId))
            {
                preferredEndpoint = endpoints.FirstOrDefault(e =>
                    string.Equals(e.Id, _preferences.LastMidiEndpointId, StringComparison.Ordinal));
            }

            _suppressEndpointSelectionPersistence = true;
            try
            {
                EndpointCombo.ItemsSource = endpoints;
                EndpointCombo.SelectedItem = preferredEndpoint ?? endpoints.FirstOrDefault();
            }
            finally
            {
                _suppressEndpointSelectionPersistence = false;
            }

            if (EndpointCombo.SelectedItem is MidiEndpointInfo selected)
            {
                _preferences.LastMidiEndpointId = selected.Id;
                SavePreferences();
            }

            StatusText.Text = endpoints.Count > 0
                ? $"Found {endpoints.Count} endpoint(s)."
                : "No MIDI output endpoint found.";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Endpoint refresh failed: {ex.Message}";
        }
    }

    private void EndpointComboSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEndpointSelectionPersistence)
        {
            return;
        }

        if (EndpointCombo.SelectedItem is not MidiEndpointInfo endpoint)
        {
            return;
        }

        if (string.Equals(_preferences.LastMidiEndpointId, endpoint.Id, StringComparison.Ordinal))
        {
            return;
        }

        _preferences.LastMidiEndpointId = endpoint.Id;
        SavePreferences();
    }

    private void LoadPreferences()
    {
        try
        {
            if (!File.Exists(_preferencesPath))
            {
                return;
            }

            var json = File.ReadAllText(_preferencesPath);
            var loaded = JsonSerializer.Deserialize<AppPreferences>(json);
            if (loaded is not null)
            {
                _preferences = loaded;
            }
        }
        catch (Exception)
        {
            _preferences = new AppPreferences();
        }
    }

    private void SavePreferences()
    {
        try
        {
            var directory = System.IO.Path.GetDirectoryName(_preferencesPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(_preferences, PreferencesJsonOptions);
            File.WriteAllText(_preferencesPath, json);
        }
        catch (Exception)
        {
        }
    }

    private async void OpenFileClick(object sender, RoutedEventArgs e)
    {
        await StopPlaybackIfRunningAsync();

        var dialog = new OpenFileDialog
        {
            Title = "Open Sequence File",
            Filter = "Sequence files (*.rcp;*.r36;*.g36;*.mcp;*.mid;*.midi)|*.rcp;*.r36;*.g36;*.mcp;*.mid;*.midi|Recomposer (*.rcp;*.r36;*.g36;*.mcp)|*.rcp;*.r36;*.g36;*.mcp|Standard MIDI (*.mid;*.midi)|*.mid;*.midi|All files (*.*)|*.*"
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            var data = await File.ReadAllBytesAsync(dialog.FileName);
            var isStandardMidi = StandardMidiParser.LooksLikeStandardMidi(data);
            if (isStandardMidi)
            {
                var midiSong = _standardMidiParser.Parse(data);
                _song = BuildUiSongFromStandardMidi(midiSong, dialog.FileName);
                _plan = midiSong.PlaybackPlan;
                _summaryFormatLabelOverride = "SMF";
                TrackList.ItemsSource = midiSong.Tracks.Select(t =>
                    $"{t.TrackId:00}  CH{t.DefaultChannel + 1:00}  {(t.IsMuted ? "[MUTE]" : "      ")}  {t.Name}");
            }
            else
            {
                _song = _parser.Parse(data);
                _plan = _builder.Build(_song);
                _summaryFormatLabelOverride = null;
                TrackList.ItemsSource = _song.Tracks.Select(t =>
                    $"{t.TrackId:00}  CH{t.DefaultChannel + 1:00}  {(t.IsMuted ? "[MUTE]" : "      ")}  {t.Name}");
            }

            ResetPartParameters();

            LoadedFileText.Text = dialog.FileName;
            RefreshLoadedFileScroll();
            var title = string.IsNullOrWhiteSpace(_song.Title) ? System.IO.Path.GetFileNameWithoutExtension(dialog.FileName) : _song.Title;
            var subtitle = "READY";
            if (!string.IsNullOrWhiteSpace(_song.Comment))
            {
                subtitle = _song.Comment
                    .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                    .FirstOrDefault() ?? subtitle;
            }

            _lcdState.SetDisplayedText(title, subtitle);
            _isTitleScrollActive = false;
            _lcdState.ResetLine1Scroll(DateTimeOffset.UtcNow);
            SyncLcdText();
            UpdateSummaryTexts();
            UpdatePartInfoPanel();
            LcdEventText.Text = "CH-- NOTE --- VEL ---";
            PlayProgress.Value = 0;
            ClearLog();
            LogLoadDiagnostics(isStandardMidi);
            ResetMeters();
            RenderPianoRoll();
            UpdatePianoRollPlayhead(0);
            RefreshLcdMatrix();

            StatusText.Text = isStandardMidi
                ? $"Loaded SMF: {_song.Tracks.Count} tracks, {_plan.MidiEvents.Count} events."
                : $"Loaded RCP: {_song.Tracks.Count} tracks, {_plan.MidiEvents.Count} events.";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Load failed: {ex.Message}";
        }
    }

    private async void PlayClick(object sender, RoutedEventArgs e)
    {
        if (_plan is null)
        {
            StatusText.Text = "Load an RCP/SMF file first.";
            return;
        }

        if (EndpointCombo.SelectedItem is not MidiEndpointInfo endpoint)
        {
            StatusText.Text = "Select a MIDI output endpoint.";
            return;
        }

        TogglePlaybackUi(isPlaying: true);
        _playbackCts = new CancellationTokenSource();
        _playbackStoppedSignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var progress = new Progress<double>(p =>
        {
            _ = p;
        });

        try
        {
            await _midiOutput.OpenAsync(endpoint.Id, _playbackCts.Token);
            if (SendPlaybackInitReset)
            {
                await SendPlaybackInitializationAsync(_playbackCts.Token);
            }
            _isTitleScrollActive = true;
            _lcdState.ResetLine1Scroll(DateTimeOffset.UtcNow);
            SyncLcdText();
            StartPlaybackUiAnimation();
            StatusText.Text = "Playing...";
            AppendLog($"-- PLAY START on {endpoint.Name} --");
            await _runner.PlayAsync(_plan, _midiOutput, _playbackCts.Token, progress, OnEventDispatched, ShouldSendPlaybackEvent, OnPlaybackRunDiagnostics);
            UpdatePlaybackUi(1.0);
            StatusText.Text = "Playback complete.";
            AppendLog("-- PLAY END --");
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Playback stopped.";
            AppendLog("-- PLAY STOPPED --");
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Playback error: {ex.Message}";
            AppendLog($"ERROR {ex.Message}");
        }
        finally
        {
            _playbackStoppedSignal?.TrySetResult(true);
            _isTitleScrollActive = false;
            _lcdState.ResetLine1Scroll(DateTimeOffset.UtcNow);
            SyncLcdText();
            StopPlaybackUiAnimation();
            _playbackCts.Dispose();
            _playbackCts = null;
            _playbackStoppedSignal = null;
            TogglePlaybackUi(isPlaying: false);
            await _midiOutput.CloseAsync();
            ResetMeters();
        }
    }

    private void StopClick(object sender, RoutedEventArgs e)
    {
        _isTitleScrollActive = false;
        _lcdState.ResetLine1Scroll(DateTimeOffset.UtcNow);
        SyncLcdText();
        _playbackCts?.Cancel();
    }

    private async Task StopPlaybackIfRunningAsync()
    {
        if (_playbackCts is null)
        {
            return;
        }

        StatusText.Text = "Stopping current playback...";
        var stoppedSignal = _playbackStoppedSignal;
        _playbackCts.Cancel();

        if (stoppedSignal is not null)
        {
            try
            {
                await stoppedSignal.Task;
            }
            catch (Exception)
            {
            }
        }
    }

    private void AllModeClick(object sender, RoutedEventArgs e)
    {
        _isAllDisplayMode = true;
        UpdatePartInfoPanel();
        UpdatePianoRollPartEmphasis();
    }

    private async void MuteClick(object sender, RoutedEventArgs e)
    {
        if (_isAllDisplayMode)
        {
            var muteAll = !_partMuted.All(v => v);
            for (var i = 0; i < _partMuted.Length; i++)
            {
                _partMuted[i] = muteAll;
                if (muteAll)
                {
                    _partLevelTargets[i] = 0;
                    _partLevels[i] = 0;
                }
            }

            LcdEventText.Text = muteAll ? "ALL PARTS MUTE ON" : "ALL PARTS MUTE OFF";
            if (muteAll)
            {
                await SendAllNotesOffForChannelsAsync(Enumerable.Range(0, 16));
            }
        }
        else
        {
            var part = _selectedPartIndex;
            _partMuted[part] = !_partMuted[part];
            if (_partMuted[part])
            {
                _partLevelTargets[part] = 0;
                _partLevels[part] = 0;
            }

            LcdEventText.Text = _partMuted[part]
                ? $"PART {part + 1:00} MUTE ON"
                : $"PART {part + 1:00} MUTE OFF";
            if (_partMuted[part])
            {
                await SendAllNotesOffForChannelsAsync([part]);
            }
        }

        UpdatePartInfoPanel();
        RefreshLcdMatrix();
    }

    private bool ShouldSendPlaybackEvent(ScheduledMidiEvent e)
    {
        if (e.Packet.Kind != MidiMessageKind.Short)
        {
            return true;
        }

        var shortMessage = e.Packet.ShortMessage;
        var status = (byte)(shortMessage & 0xFF);
        var kind = status & 0xF0;
        if (kind != 0x90)
        {
            return true;
        }

        var velocity = (int)((shortMessage >> 16) & 0x7F);
        if (velocity == 0)
        {
            return true;
        }

        var channel = status & 0x0F;
        return !_partMuted[channel];
    }

    private async Task SendAllNotesOffForChannelsAsync(IEnumerable<int> channels)
    {
        foreach (var channel in channels.Distinct())
        {
            var normalized = Math.Clamp(channel, 0, 15);
            var status = (byte)(0xB0 | normalized);
            var allSoundOff = (uint)(status | (120u << 8));
            var resetHold = (uint)(status | (64u << 8));
            var allNotesOff = (uint)(status | (123u << 8));

            try
            {
                await _midiOutput.SendShortAsync(allSoundOff, CancellationToken.None);
                await _midiOutput.SendShortAsync(resetHold, CancellationToken.None);
                await _midiOutput.SendShortAsync(allNotesOff, CancellationToken.None);
            }
            catch (Exception)
            {
            }
        }
    }

    private async Task SendPlaybackInitializationAsync(CancellationToken cancellationToken)
    {
        try
        {
            // GM System On: makes channel 10 rhythm on General MIDI devices.
            await _midiOutput.SendSysExAsync([0xF0, 0x7E, 0x7F, 0x09, 0x01, 0xF7], cancellationToken);
            await Task.Delay(20, cancellationToken);

            // Roland GS Reset: preferred for SC-88 style/GS playback.
            await _midiOutput.SendSysExAsync([0xF0, 0x41, 0x10, 0x42, 0x12, 0x40, 0x00, 0x7F, 0x00, 0x41, 0xF7], cancellationToken);
            await Task.Delay(60, cancellationToken);

            await SendAllNotesOffForChannelsAsync(Enumerable.Range(0, 16));
            AppendLog("-- INIT sent GM/GS reset for consistent instrument map --");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppendLog($"-- INIT reset warning: {ex.Message} --");
        }
    }

    private void PartNextClick(object sender, RoutedEventArgs e)
    {
        if (_isAllDisplayMode)
        {
            _isAllDisplayMode = false;
            _selectedPartIndex = 0;
        }
        else
        {
            _selectedPartIndex = (_selectedPartIndex + 1) % 16;
        }

        UpdatePartInfoPanel();
        UpdatePianoRollPartEmphasis();
    }

    private void PartPrevClick(object sender, RoutedEventArgs e)
    {
        if (_isAllDisplayMode)
        {
            _isAllDisplayMode = false;
            _selectedPartIndex = 15;
        }
        else
        {
            _selectedPartIndex = (_selectedPartIndex + 15) % 16;
        }

        UpdatePartInfoPanel();
        UpdatePianoRollPartEmphasis();
    }

    private void TogglePlaybackUi(bool isPlaying)
    {
        PlayButton.IsEnabled = !isPlaying;
        StopButton.IsEnabled = isPlaying;
        EndpointCombo.IsEnabled = !isPlaying;
    }

    private void OnEventDispatched(ScheduledMidiEvent e)
    {
        UpdatePlaybackHintTick(e.Tick);
        if (ShouldQueueUiEvent(e))
        {
            _pendingUiEvents.Enqueue(e);
        }
    }

    private static bool ShouldQueueUiEvent(ScheduledMidiEvent e)
    {
        if (e.Packet.Kind == MidiMessageKind.SysEx)
        {
            return true;
        }

        var message = e.Packet.ShortMessage;
        var status = (byte)(message & 0xFF);
        var kind = status & 0xF0;
        if (kind == 0x90)
        {
            var velocity = (int)((message >> 16) & 0x7F);
            return velocity > 0;
        }

        return kind is 0xB0 or 0xC0 or 0xE0;
    }

    private void UpdateDisplayFromEvent(ScheduledMidiEvent e)
    {
        if (e.Packet.Kind == MidiMessageKind.SysEx)
        {
            var handled = _lcdState.TryApplySysEx(e.Packet.SysExData, DateTimeOffset.UtcNow, out var summary);
            if (handled)
            {
                LcdEventText.Text = summary;
                SyncLcdText();
                RefreshLcdMatrix();
                AppendRealtimeEventLog($"[{e.Tick,7}] {summary}");
            }
            else if (!string.IsNullOrWhiteSpace(summary))
            {
                LcdEventText.Text = summary;
                AppendRealtimeEventLog($"[{e.Tick,7}] {summary}");
            }
            else
            {
                AppendRealtimeEventLog($"[{e.Tick,7}] SysEx len={e.Packet.SysExData?.Length ?? 0}");
            }
            return;
        }

        var message = e.Packet.ShortMessage;
        var status = (byte)(message & 0xFF);
        var data1 = (int)((message >> 8) & 0x7F);
        var data2 = (int)((message >> 16) & 0x7F);
        var channel = status & 0x0F;
        var kind = status & 0xF0;

        switch (kind)
        {
            case 0x90 when data2 > 0:
                if (!_partMuted[channel])
                {
                    _partLevelTargets[channel] = Math.Max(_partLevelTargets[channel], ScaleVelocity(data2));
                }
                LcdEventText.Text = $"CH{channel + 1:00} NOTE {data1:000} VEL {data2:000}";
                AppendRealtimeEventLog($"[{e.Tick,7}] CH{channel + 1:00} NOTEON  n={data1:000} v={data2:000}");
                break;
            case 0x80:
            case 0x90:
                _partLevelTargets[channel] = Math.Min(_partLevelTargets[channel], 8);
                LcdEventText.Text = $"CH{channel + 1:00} NOTEOFF {data1:000}";
                break;
            case 0xC0:
                _programByChannel[channel] = data1;
                LcdEventText.Text = $"CH{channel + 1:00} PROG {data1:000}";
                AppendRealtimeEventLog($"[{e.Tick,7}] CH{channel + 1:00} PROGRAM {data1:000}");
                break;
            case 0xB0:
                HandleControlChange(channel, data1, data2);
                LcdEventText.Text = $"CH{channel + 1:00} CC {data1:000} {data2:000}";
                AppendRealtimeEventLog($"[{e.Tick,7}] CH{channel + 1:00} CC {data1:000} {data2:000}");
                break;
            case 0xE0:
                LcdEventText.Text = $"CH{channel + 1:00} PITCH {(data2 << 7) | data1:00000}";
                AppendRealtimeEventLog($"[{e.Tick,7}] CH{channel + 1:00} PITCH {(data2 << 7) | data1:00000}");
                break;
        }

        UpdatePartInfoPanel();
        RefreshLcdMatrix();
    }

    private void DecayMeters()
    {
        const double decay = 2.3;
        for (var i = 0; i < _partLevels.Length; i++)
        {
            var current = _partLevels[i];
            current = Math.Max(0, current - decay);
            current = Math.Max(current, _partLevelTargets[i]);
            _partLevelTargets[i] = Math.Max(0, _partLevelTargets[i] - (decay * 0.7));
            _partLevels[i] = current;
        }

        // Keep the LCD title scroll smooth even when no MIDI events arrive.
        SyncLcdText();
        RefreshLcdMatrix();
    }

    private void HandleControlChange(int channel, int controller, int value)
    {
        switch (controller)
        {
            case 7:
                _partVolume[channel] = value;
                break;
            case 10:
                _partPan[channel] = value;
                break;
            case 91:
                _partReverb[channel] = value;
                break;
            case 93:
                _partChorus[channel] = value;
                break;
            case 94:
                _partDelay[channel] = value;
                break;
            case 100:
                _rpnLsb[channel] = value;
                break;
            case 101:
                _rpnMsb[channel] = value;
                break;
            case 6:
                if (_rpnMsb[channel] == 0 && _rpnLsb[channel] == 2)
                {
                    _partKeyShift[channel] = value - 64;
                }
                break;
        }
    }

    private void ResetPartParameters()
    {
        Array.Fill(_programByChannel, 0);
        Array.Fill(_partVolume, 100);
        Array.Fill(_partPan, 64);
        Array.Fill(_partReverb, 40);
        Array.Fill(_partChorus, 0);
        Array.Fill(_partKeyShift, 0);
        Array.Fill(_partDelay, 0);
        Array.Fill(_rpnMsb, -1);
        Array.Fill(_rpnLsb, -1);
        Array.Fill(_partMuted, false);
        _isAllDisplayMode = true;
        _selectedPartIndex = 0;
    }

    private void ResetDisplayState()
    {
        _lcdState.Reset();
        _isTitleScrollActive = false;
        _lcdState.ResetLine1Scroll(DateTimeOffset.UtcNow);
        ResetPartParameters();
        StopPlaybackUiAnimation();
        _summaryFormatLabelOverride = null;
        _lcdState.SetDisplayedText("NO SONG");
        SyncLcdText();
        UpdateSummaryTexts();
        UpdatePartInfoPanel();
        LcdEventText.Text = "CH-- NOTE --- VEL ---";
        LoadedFileText.Text = "No file selected";
        RefreshLoadedFileScroll();
        StatusText.Text = "Ready";
        PlayProgress.Value = 0;
        UpdatePianoRollPlayhead(0);
        TrackList.ItemsSource = null;
        ClearLog();
        ResetMeters();
        RenderPianoRoll();
        RefreshLcdMatrix();
    }

    private void ResetMeters()
    {
        Array.Fill(_partLevelTargets, 0);
        Array.Fill(_partLevels, 0);

        RefreshLcdMatrix();
    }

    private void AppendLog(string text)
    {
        _pendingEventLog.Enqueue(text);
    }

    private void AppendRealtimeEventLog(string text)
    {
        if (!EnableRealtimeEventLog)
        {
            return;
        }

        _pendingEventLog.Enqueue(text);
    }

    private void FlushPendingEventLog(object? sender, EventArgs e)
    {
        var hasAdded = false;
        var shouldForceScroll = false;
        var addedCount = 0;

        while (addedCount < EventLogFlushBatchSize && _pendingEventLog.TryDequeue(out var text))
        {
            _eventLog.Add(text);
            addedCount++;
            hasAdded = true;

            if (!shouldForceScroll)
            {
                shouldForceScroll = text.StartsWith("--", StringComparison.Ordinal) ||
                                    text.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase);
            }
        }

        if (!hasAdded)
        {
            return;
        }

        while (_eventLog.Count > MaxEventLogEntries)
        {
            _eventLog.RemoveAt(0);
        }

        if (_eventLog.Count == 0)
        {
            return;
        }

        var isLogTabActive = MainTabs.SelectedIndex == 2;
        if (shouldForceScroll || (isLogTabActive && ++_logScrollSkipCounter >= 4))
        {
            _logScrollSkipCounter = 0;
            EventLogList.ScrollIntoView(_eventLog[^1]);
        }
    }

    private void EventLogListPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0 || e.Key != Key.C)
        {
            return;
        }

        CopySelectedLogToClipboard(fallbackToAllWhenNoneSelected: true);
        e.Handled = true;
    }

    private void CopySelectedLogClick(object sender, RoutedEventArgs e)
    {
        CopySelectedLogToClipboard(fallbackToAllWhenNoneSelected: false);
    }

    private void CopyAllLogsClick(object sender, RoutedEventArgs e)
    {
        CopyAllLogsToClipboard();
    }

    private void CopySelectedLogToClipboard(bool fallbackToAllWhenNoneSelected)
    {
        var selectedLines = EventLogList.SelectedItems
            .Cast<object>()
            .Select(item => item?.ToString() ?? string.Empty)
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .ToList();

        if (selectedLines.Count == 0 && fallbackToAllWhenNoneSelected)
        {
            CopyAllLogsToClipboard();
            return;
        }

        if (selectedLines.Count == 0)
        {
            StatusText.Text = "No selected log entries to copy.";
            return;
        }

        CopyTextToClipboard(string.Join(Environment.NewLine, selectedLines), $"Copied {selectedLines.Count} selected log line(s).");
    }

    private void CopyAllLogsToClipboard()
    {
        if (_eventLog.Count == 0)
        {
            StatusText.Text = "No log entries to copy.";
            return;
        }

        CopyTextToClipboard(string.Join(Environment.NewLine, _eventLog), $"Copied {_eventLog.Count} log line(s).");
    }

    private void CopyTextToClipboard(string text, string successMessage)
    {
        try
        {
            Clipboard.SetText(text);
            StatusText.Text = successMessage;
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Copy failed: {ex.Message}";
        }
    }

    private void ClearLog()
    {
        _eventLog.Clear();
        while (_pendingEventLog.TryDequeue(out _))
        {
        }
        ClearPendingUiEvents();
        _logScrollSkipCounter = 0;
    }

    private void LogLoadDiagnostics(bool isStandardMidi)
    {
        if (_plan is null)
        {
            return;
        }

        AppendTempoMapDiagnostics();

        if (isStandardMidi)
        {
            AppendLog("-- SMF parser limits: format 2 and SMPTE time division are unsupported --");
            return;
        }

        var unsupported = _plan.BuildDiagnostics.UnsupportedCommands;
        var loopLimitTracks = _plan.BuildDiagnostics.LoopExpansionLimitTracks;
        if (unsupported.Count == 0 && loopLimitTracks.Count == 0)
        {
            AppendLog("-- RCP command support: no unsupported command detected --");
            return;
        }

        if (unsupported.Count > 0)
        {
            AppendLog($"-- WARNING unsupported RCP command types: {unsupported.Count} --");
            foreach (var item in unsupported.Take(16))
            {
                var trackText = item.Tracks.Count > 0
                    ? string.Join(",", item.Tracks.Take(8))
                    : "-";
                AppendLog($"UNSUPPORTED CMD 0x{item.Command:X2} count={item.Count} tracks={trackText}");
            }
        }

        if (loopLimitTracks.Count > 0)
        {
            var tracks = string.Join(",", loopLimitTracks.Take(16));
            AppendLog($"-- WARNING loop expansion capped on tracks: {tracks} --");
            AppendLog("-- INFO capped tracks are truncated to prevent infinite loop lockup --");
        }
    }

    private void OnPlaybackRunDiagnostics(PlaybackRunDiagnostics diagnostics)
    {
        AppendLog(
            $"-- PERF events={diagnostics.TotalEvents} short={diagnostics.ShortEventsSent} sysex={diagnostics.SysExEventsSent} filtered={diagnostics.FilteredEvents} maxPerTick={diagnostics.MaxEventsPerTick} --");
        AppendLog(
            $"-- PERF late>2ms={diagnostics.LateEventsOver2Ms} late>5ms={diagnostics.LateEventsOver5Ms} late>10ms={diagnostics.LateEventsOver10Ms} maxLate={diagnostics.MaxLateByMs:F2}ms --");
        AppendLog(
            $"-- PERF send>1ms={diagnostics.SendCallsOver1Ms} send>2ms={diagnostics.SendCallsOver2Ms} send>5ms={diagnostics.SendCallsOver5Ms} maxSend={diagnostics.MaxSendCallMs:F2}ms --");

        var sustainedUnderrun =
            diagnostics.LateEventsOver10Ms >= 4 ||
            diagnostics.LateEventsOver5Ms >= 80 ||
            diagnostics.MaxLateByMs > 30.0;

        if (sustainedUnderrun)
        {
            AppendLog("-- WARNING playback timing underrun detected (possible glitch cause) --");
        }
        else if (diagnostics.LateEventsOver10Ms > 0 || diagnostics.MaxLateByMs > 15.0)
        {
            AppendLog("-- INFO minor timing jitter detected (usually inaudible) --");
        }

        if (diagnostics.MaxEventsPerTick >= 64)
        {
            AppendLog("-- WARNING dense same-tick MIDI burst detected (possible endpoint overload) --");
        }

        if (diagnostics.SendCallsOver5Ms > 0 || diagnostics.MaxSendCallMs > 10.0)
        {
            AppendLog("-- WARNING MIDI endpoint send blocking detected (possible driver/device timing issue) --");
        }
        else if (diagnostics.SendCallsOver2Ms > 0)
        {
            AppendLog("-- INFO small endpoint send blocking observed --");
        }
    }

    private void AppendTempoMapDiagnostics()
    {
        if (_plan is null)
        {
            return;
        }

        var initial = Math.Max(_plan.InitialTempoBpm, 1.0);
        var tempoEvents = _plan.TempoEvents;
        var minTempo = initial;
        var maxTempo = initial;
        if (tempoEvents.Count > 0)
        {
            minTempo = Math.Min(minTempo, tempoEvents.Min(t => t.Bpm));
            maxTempo = Math.Max(maxTempo, tempoEvents.Max(t => t.Bpm));
        }

        AppendLog($"-- TEMPO map changes={tempoEvents.Count} initial={initial:F2} min={minTempo:F2} max={maxTempo:F2} --");

        if (_plan.MidiEvents.Count == 0)
        {
            return;
        }

        var totalTicks = _plan.MidiEvents[^1].Tick;
        var measureTicks = Math.Max(1, GetMeasureTicks());
        var measures = Math.Max(1.0, totalTicks / (double)measureTicks);
        var changesPerMeasure = tempoEvents.Count / measures;
        AppendLog($"-- TEMPO density={changesPerMeasure:F2} changes/measure --");

        if (changesPerMeasure > 0.50 || maxTempo - minTempo > 40.0)
        {
            AppendLog("-- INFO dense tempo map detected; audible tempo fluctuation may come from source data/command interpretation --");
        }
    }

    private void ClearPendingUiEvents()
    {
        while (_pendingUiEvents.TryDequeue(out _))
        {
        }
    }

    private static double ScaleVelocity(int velocity)
    {
        return Math.Clamp(velocity, 0, 127) / 127.0 * 100.0;
    }

    private void SyncLcdText()
    {
        LcdLine1Text.Text = _lcdState.GetDisplayLine1(DateTimeOffset.UtcNow, _isTitleScrollActive).TrimEnd();
        LcdLine2Text.Text = _lcdState.DisplayLine2.TrimEnd();
    }

    private void RefreshLcdMatrix()
    {
        var nowUtc = DateTimeOffset.UtcNow;
        var rows = _lcdState.GetCurrentRows(_partLevels, nowUtc, out _);
        for (var row = 0; row < LcdSize; row++)
        {
            var rowBits = rows[row];
            for (var col = 0; col < LcdSize; col++)
            {
                var index = row * LcdSize + col;
                var isOn = (rowBits & (1 << (15 - col))) != 0;
                if (_lcdCellState[index] == isOn)
                {
                    continue;
                }

                _lcdCellState[index] = isOn;
                _lcdCells[index].Background = isOn ? _lcdOnBrush : _lcdOffBrush;
            }
        }
    }

    private void RenderPianoRoll()
    {
        PianoRollCanvas.Children.Clear();
        _pianoRollNoteVisuals.Clear();
        PianoRollOverlayPlayhead.Visibility = Visibility.Collapsed;
        _pianoRollTotalTicks = 0;
        ResetPianoRollScroll();

        if (_plan is null || _plan.MidiEvents.Count == 0)
        {
            PianoRollCanvas.Width = 1200;
            PianoRollCanvas.Height = 520;
            return;
        }

        var notes = ExtractPianoRollNotes(_plan.MidiEvents);
        if (notes.Count == 0)
        {
            PianoRollCanvas.Width = 1200;
            PianoRollCanvas.Height = 520;
            return;
        }

        var minNote = Math.Max(0, notes.Min(n => n.Note));
        var maxNote = Math.Min(127, notes.Max(n => n.Note));
        var noteSpan = Math.Max(24, maxNote - minNote + 1);
        var width = Math.Max(1200.0, (_pianoRollTotalTicks * PianoRollTickScale) + PianoRollLeftPadding + 48.0);
        var height = PianoRollTopPadding * 2 + (noteSpan * PianoRollNoteHeight);

        PianoRollCanvas.Width = width;
        PianoRollCanvas.Height = height;

        for (var i = 0; i <= noteSpan; i++)
        {
            var note = maxNote - i;
            var y = PianoRollTopPadding + (i * PianoRollNoteHeight);
            var line = new Line
            {
                X1 = 0,
                X2 = width,
                Y1 = y,
                Y2 = y,
                Stroke = note % 12 == 0 ? _pianoRollOctaveBrush : _pianoRollGridBrush,
                StrokeThickness = note % 12 == 0 ? 1.1 : 0.7
            };
            PianoRollCanvas.Children.Add(line);
        }

        var measureTicks = GetMeasureTicks();
        if (measureTicks > 0)
        {
            for (var tick = 0L; tick <= _pianoRollTotalTicks; tick += measureTicks)
            {
                var x = PianoRollLeftPadding + tick * PianoRollTickScale;
                var line = new Line
                {
                    X1 = x,
                    X2 = x,
                    Y1 = 0,
                    Y2 = height,
                    Stroke = _pianoRollOctaveBrush,
                    StrokeThickness = 0.8
                };
                PianoRollCanvas.Children.Add(line);
            }
        }

        foreach (var note in notes)
        {
            var x = PianoRollLeftPadding + note.StartTick * PianoRollTickScale;
            var w = Math.Max(1.2, (note.EndTick - note.StartTick) * PianoRollTickScale);
            var row = maxNote - note.Note;
            var y = PianoRollTopPadding + row * PianoRollNoteHeight + 0.2;

            var rect = new Rectangle
            {
                Width = w,
                Height = Math.Max(1.2, PianoRollNoteHeight - 1.0),
                RadiusX = 0.8,
                RadiusY = 0.8,
                Fill = _pianoRollChannelBrushes[note.Channel % _pianoRollChannelBrushes.Length],
                Stroke = Brushes.Transparent
            };
            Canvas.SetLeft(rect, x);
            Canvas.SetTop(rect, y);
            PianoRollCanvas.Children.Add(rect);
            _pianoRollNoteVisuals.Add(new PianoRollNoteVisual(rect, note.Channel));
        }

        UpdatePianoRollPartEmphasis();
        PianoRollOverlayPlayhead.Fill = _pianoRollPlayheadBrush;
        PianoRollOverlayPlayhead.Visibility = Visibility.Visible;
        Canvas.SetTop(PianoRollOverlayPlayhead, 0);
        Canvas.SetLeft(PianoRollOverlayPlayhead, Math.Round(PianoRollLeftPadding));
        UpdatePianoRollOverlayBounds();
        CenterPianoRollVerticalDefault();
    }

    private void UpdatePianoRollPartEmphasis()
    {
        if (_pianoRollNoteVisuals.Count == 0)
        {
            return;
        }

        var hasSelectedPart = !_isAllDisplayMode;
        var selectedPart = Math.Clamp(_selectedPartIndex, 0, 15);

        foreach (var visual in _pianoRollNoteVisuals)
        {
            var isSelected = !hasSelectedPart || visual.Channel == selectedPart;
            visual.Shape.Opacity = isSelected ? 1.0 : 0.30;

            if (hasSelectedPart && visual.Channel == selectedPart)
            {
                visual.Shape.Stroke = Brushes.Transparent;
                visual.Shape.StrokeThickness = 0;
            }
            else
            {
                visual.Shape.Stroke = Brushes.Transparent;
                visual.Shape.StrokeThickness = 0;
            }
        }
    }

    private List<PianoRollNoteSegment> ExtractPianoRollNotes(IReadOnlyList<ScheduledMidiEvent> events)
    {
        var result = new List<PianoRollNoteSegment>(2048);
        var active = new Dictionary<(int Channel, int Note), Stack<long>>();
        _pianoRollTotalTicks = events.Count > 0 ? events[^1].Tick : 0;

        foreach (var e in events)
        {
            if (e.Packet.Kind != MidiMessageKind.Short)
            {
                continue;
            }

            var msg = e.Packet.ShortMessage;
            var status = (byte)(msg & 0xFF);
            var kind = status & 0xF0;
            var channel = status & 0x0F;
            var note = (int)((msg >> 8) & 0x7F);
            var velocity = (int)((msg >> 16) & 0x7F);

            if (kind == 0x90 && velocity > 0)
            {
                var key = (channel, note);
                if (!active.TryGetValue(key, out var stack))
                {
                    stack = [];
                    active[key] = stack;
                }

                stack.Push(e.Tick);
                continue;
            }

            if (kind != 0x80 && !(kind == 0x90 && velocity == 0))
            {
                continue;
            }

            var offKey = (channel, note);
            if (!active.TryGetValue(offKey, out var activeStack) || activeStack.Count == 0)
            {
                continue;
            }

            var start = activeStack.Pop();
            var end = Math.Max(start + 1, e.Tick);
            result.Add(new PianoRollNoteSegment(start, end, note, channel));
        }

        foreach (var pair in active)
        {
            while (pair.Value.Count > 0)
            {
                var start = pair.Value.Pop();
                var end = Math.Max(start + 1, _pianoRollTotalTicks);
                result.Add(new PianoRollNoteSegment(start, end, pair.Key.Note, pair.Key.Channel));
            }
        }

        return result;
    }

    private void UpdatePianoRollPlayhead(double progressRatio, bool autoScroll = true)
    {
        var clamped = Math.Clamp(progressRatio, 0.0, 1.0);
        _playheadProgressRatio = clamped;

        if (PianoRollOverlayPlayhead.Visibility != Visibility.Visible)
        {
            return;
        }

        var x = PianoRollLeftPadding + _pianoRollTotalTicks * clamped * PianoRollTickScale;

        if (PianoRollScroll.ViewportWidth <= 0)
        {
            return;
        }

        var target = Math.Max(0, x - PianoRollScroll.ViewportWidth * 0.35);
        if (_isPianoRollHardwareScrollEnabled && autoScroll)
        {
            _pianoRollScrollTargetOffset = target;
        }
        else if (autoScroll)
        {
            PianoRollScroll.ScrollToHorizontalOffset(Math.Round(target));
        }

        var effectiveOffset = _isPianoRollHardwareScrollEnabled
            ? _pianoRollScrollCurrentOffset
            : PianoRollScroll.HorizontalOffset;
        // Keep playhead anchored to content position; let it scroll out of view naturally.
        var overlayX = x - effectiveOffset;
        Canvas.SetLeft(PianoRollOverlayPlayhead, overlayX);
        UpdatePianoRollOverlayBounds();
    }

    private void PianoRollScrollSizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdatePianoRollOverlayBounds();

        if (_playbackUiStopwatch.IsRunning && _playbackTotalMilliseconds > 0)
        {
            UpdatePianoRollPlayhead(GetPlaybackProgressRatio());
            return;
        }

        if (PianoRollOverlayPlayhead.Visibility != Visibility.Visible)
        {
            return;
        }

        UpdatePianoRollPlayhead(_playheadProgressRatio, autoScroll: false);
    }

    private void PianoRollScrollScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_isPianoRollHardwareScrollEnabled)
        {
            return;
        }

        if (PianoRollOverlayPlayhead.Visibility != Visibility.Visible)
        {
            return;
        }

        if (Math.Abs(e.HorizontalChange) < double.Epsilon && Math.Abs(e.ViewportWidthChange) < double.Epsilon)
        {
            return;
        }

        UpdatePianoRollPlayhead(_playheadProgressRatio, autoScroll: false);
    }

    private void UpdatePianoRollOverlayBounds()
    {
        if (PianoRollOverlayPlayhead.Visibility != Visibility.Visible)
        {
            return;
        }

        var height = PianoRollScroll.ViewportHeight;
        if (height <= 0)
        {
            height = PianoRollScroll.ActualHeight;
        }
        if (height <= 0)
        {
            height = PianoRollOverlayCanvas.ActualHeight;
        }

        PianoRollOverlayPlayhead.Height = Math.Max(0.0, height);
        Canvas.SetTop(PianoRollOverlayPlayhead, 0.0);
    }

    private void ResetPianoRollScroll()
    {
        StopPianoRollHardwareScroll(applyOffsetToScrollViewer: false);
        _pianoRollScrollTargetOffset = 0;
        _pianoRollScrollCurrentOffset = 0;
        _pianoRollAutoScrollTransform.X = 0;
        PianoRollScroll.ScrollToHorizontalOffset(0);
        PianoRollScroll.ScrollToVerticalOffset(0);
    }

    private void CenterPianoRollVerticalDefault()
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (_isPianoRollHardwareScrollEnabled)
            {
                return;
            }

            var maxVertical = Math.Max(0.0, PianoRollScroll.ScrollableHeight);
            if (maxVertical <= 0.0)
            {
                return;
            }

            PianoRollScroll.ScrollToVerticalOffset(Math.Round(maxVertical * 0.5));
        }));
    }

    private void LoadedFileScrollTick(object? sender, EventArgs e)
    {
        var overflow = LoadedFileText.ActualWidth - LoadedFileViewport.ActualWidth;
        if (overflow <= 1.0)
        {
            _loadedFileScrollTimer.Stop();
            _loadedFileScrollOffset = 0;
            _loadedFileScrollPauseMs = 0;
            Canvas.SetLeft(LoadedFileText, 0);
            return;
        }

        if (_loadedFileScrollPauseMs > 0)
        {
            _loadedFileScrollPauseMs = Math.Max(0, _loadedFileScrollPauseMs - _loadedFileScrollTimer.Interval.TotalMilliseconds);
            return;
        }

        _loadedFileScrollOffset -= LoadedFileScrollSpeedPerTick;
        var wrapDistance = overflow + LoadedFileScrollGap;
        if (-_loadedFileScrollOffset >= wrapDistance)
        {
            _loadedFileScrollOffset = 0;
            _loadedFileScrollPauseMs = LoadedFileScrollPauseMsDefault;
        }

        Canvas.SetLeft(LoadedFileText, Math.Round(_loadedFileScrollOffset));
    }

    private void RefreshLoadedFileScroll()
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            _loadedFileScrollOffset = 0;
            _loadedFileScrollPauseMs = LoadedFileScrollPauseMsDefault;
            Canvas.SetLeft(LoadedFileText, 0);

            var overflow = LoadedFileText.ActualWidth - LoadedFileViewport.ActualWidth;
            if (overflow > 1.0)
            {
                if (!_loadedFileScrollTimer.IsEnabled)
                {
                    _loadedFileScrollTimer.Start();
                }
            }
            else
            {
                _loadedFileScrollTimer.Stop();
            }
        }));
    }

    private void StartPlaybackUiAnimation()
    {
        if (_plan is null)
        {
            return;
        }

        BuildPlaybackTempoTimeline(_plan);
        Interlocked.Exchange(ref _playbackHintTick, 0);
        _playbackUiStopwatch.Restart();
        StartPianoRollHardwareScroll();
        _playbackUiTimer.Start();
    }

    private void StopPlaybackUiAnimation()
    {
        _playbackUiTimer.Stop();
        StopPianoRollHardwareScroll(applyOffsetToScrollViewer: true);
        _playbackUiStopwatch.Reset();
        _playbackTotalMilliseconds = 0;
        _playbackTotalTicksForUi = 0;
        Interlocked.Exchange(ref _playbackHintTick, 0);
        _playbackTempoSegments.Clear();
        ClearPendingUiEvents();
    }

    private void StartPianoRollHardwareScroll()
    {
        if (_isPianoRollHardwareScrollEnabled)
        {
            return;
        }

        _isPianoRollHardwareScrollEnabled = true;
        _pianoRollScrollCurrentOffset = Math.Max(0.0, PianoRollScroll.HorizontalOffset);
        _pianoRollScrollTargetOffset = _pianoRollScrollCurrentOffset;
        _pianoRollAutoScrollTransform.X = 0;

        if (_isPianoRollRenderingHooked)
        {
            return;
        }

        CompositionTarget.Rendering += OnPianoRollRendering;
        _isPianoRollRenderingHooked = true;
    }

    private void StopPianoRollHardwareScroll(bool applyOffsetToScrollViewer)
    {
        if (_isPianoRollRenderingHooked)
        {
            CompositionTarget.Rendering -= OnPianoRollRendering;
            _isPianoRollRenderingHooked = false;
        }

        _isPianoRollHardwareScrollEnabled = false;

        if (applyOffsetToScrollViewer)
        {
            var maxOffset = Math.Max(0.0, PianoRollScroll.ScrollableWidth);
            var finalOffset = Math.Clamp(_pianoRollScrollCurrentOffset, 0.0, maxOffset);
            _pianoRollAutoScrollTransform.X = 0;
            PianoRollScroll.ScrollToHorizontalOffset(finalOffset);
            _pianoRollScrollCurrentOffset = finalOffset;
            _pianoRollScrollTargetOffset = finalOffset;
            return;
        }

        _pianoRollAutoScrollTransform.X = 0;
    }

    private void OnPianoRollRendering(object? sender, EventArgs e)
    {
        if (!_isPianoRollHardwareScrollEnabled)
        {
            return;
        }

        var viewportWidth = PianoRollScroll.ViewportWidth;
        if (viewportWidth <= 0)
        {
            return;
        }

        var maxOffset = Math.Max(0.0, PianoRollCanvas.Width - viewportWidth);
        _pianoRollScrollTargetOffset = Math.Clamp(_pianoRollScrollTargetOffset, 0.0, maxOffset);
        _pianoRollScrollCurrentOffset = _pianoRollScrollTargetOffset;

        // Mirror to scrollbar while compensating with render transform to avoid double-shift.
        if (Math.Abs(PianoRollScroll.HorizontalOffset - _pianoRollScrollCurrentOffset) >= 0.10)
        {
            PianoRollScroll.ScrollToHorizontalOffset(_pianoRollScrollCurrentOffset);
        }

        var viewportOffset = PianoRollScroll.HorizontalOffset;
        _pianoRollAutoScrollTransform.X = viewportOffset - _pianoRollScrollCurrentOffset;

        if (_playbackUiStopwatch.IsRunning && _playbackTotalMilliseconds > 0)
        {
            UpdatePianoRollPlayhead(GetPlaybackProgressRatio());
        }
    }

    private void PlaybackUiTick(object? sender, EventArgs e)
    {
        DrainPendingUiEvents();

        if (_playbackTotalMilliseconds <= 0)
        {
            return;
        }

        var progress = GetPlaybackProgressRatio();
        UpdatePlaybackUi(progress);
    }

    private void DrainPendingUiEvents()
    {
        var processed = 0;
        while (processed < MaxUiEventsPerFrame && _pendingUiEvents.TryDequeue(out var e))
        {
            UpdateDisplayFromEvent(e);
            processed++;
        }
    }

    private void UpdatePlaybackUi(double progressRatio)
    {
        var clamped = Math.Clamp(progressRatio, 0.0, 1.0);
        PlayProgress.Value = clamped * 100.0;
        if (!_isPianoRollHardwareScrollEnabled)
        {
            UpdatePianoRollPlayhead(clamped);
        }
    }

    private double GetPlaybackProgressRatio()
    {
        if (_playbackTotalTicksForUi <= 0)
        {
            return 1.0;
        }

        var elapsedMs = _playbackUiStopwatch.Elapsed.TotalMilliseconds;
        var tickFromTime = GetTickAtMilliseconds(elapsedMs);
        var resolvedTick = _isPianoRollHardwareScrollEnabled
            ? tickFromTime
            : Math.Max(tickFromTime, Interlocked.Read(ref _playbackHintTick));
        return Math.Clamp(resolvedTick / _playbackTotalTicksForUi, 0.0, 1.0);
    }

    private void BuildPlaybackTempoTimeline(RcpPlaybackPlan plan)
    {
        var events = plan.MidiEvents;
        if (events.Count == 0)
        {
            _playbackTempoSegments.Clear();
            _playbackTotalTicksForUi = 0;
            _playbackTotalMilliseconds = 0;
            return;
        }

        var totalTick = events[^1].Tick;
        var tempoEvents = plan.TempoEvents.OrderBy(t => t.Tick);
        var timeBase = Math.Max(plan.TimeBase, 1);

        _playbackTempoSegments.Clear();
        _playbackTotalTicksForUi = totalTick;
        var tick = 0L;
        var ms = 0.0;
        var bpm = Math.Max(plan.InitialTempoBpm, 1.0);

        foreach (var tempo in tempoEvents)
        {
            if (tempo.Tick < tick)
            {
                continue;
            }

            if (tempo.Tick > totalTick)
            {
                break;
            }

            if (tempo.Tick > tick)
            {
                _playbackTempoSegments.Add(new PlaybackTempoSegment(tick, ms, bpm));
                ms += (tempo.Tick - tick) * MsPerTick(bpm, timeBase);
                tick = tempo.Tick;
            }

            bpm = Math.Max(tempo.Bpm, 1.0);
        }

        _playbackTempoSegments.Add(new PlaybackTempoSegment(tick, ms, bpm));
        if (totalTick > tick)
        {
            ms += (totalTick - tick) * MsPerTick(bpm, timeBase);
        }

        _playbackTotalMilliseconds = Math.Max(ms, 1.0);
    }

    private static double MsPerTick(double bpm, int ppqn)
    {
        return 60000.0 / (bpm * Math.Max(ppqn, 1));
    }

    private void UpdatePlaybackHintTick(long tick)
    {
        while (true)
        {
            var current = Interlocked.Read(ref _playbackHintTick);
            if (tick <= current)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref _playbackHintTick, tick, current) == current)
            {
                return;
            }
        }
    }

    private double GetTickAtMilliseconds(double milliseconds)
    {
        if (_playbackTempoSegments.Count == 0 || _playbackTotalTicksForUi <= 0)
        {
            return 0.0;
        }

        var ms = Math.Max(0.0, milliseconds);
        var left = 0;
        var right = _playbackTempoSegments.Count - 1;
        var best = 0;

        while (left <= right)
        {
            var mid = (left + right) / 2;
            var value = _playbackTempoSegments[mid].StartMilliseconds;
            if (value <= ms)
            {
                best = mid;
                left = mid + 1;
            }
            else
            {
                right = mid - 1;
            }
        }

        var segment = _playbackTempoSegments[best];
        var localMs = Math.Max(0.0, ms - segment.StartMilliseconds);
        var tick = segment.StartTick + (localMs / MsPerTick(segment.Bpm, Math.Max(_plan?.TimeBase ?? 1, 1)));
        return Math.Clamp(tick, 0.0, _playbackTotalTicksForUi);
    }

    private long GetMeasureTicks()
    {
        if (_song is null)
        {
            return 0;
        }

        var beatDen = Math.Max(1, _song.BeatDenominator);
        var measure = _song.TimeBase * _song.BeatNumerator * (4.0 / beatDen);
        return Math.Max(1, (long)Math.Round(measure, MidpointRounding.AwayFromZero));
    }

    private void UpdateSummaryTexts()
    {
        if (_song is null)
        {
            LcdSummaryLeftText.Text = "RCP ---  ---/--";
            LcdSummaryRightText.Text = "TEMPO ---  TB ---";
            return;
        }

        var formatLabel = _summaryFormatLabelOverride ?? GetFormatLabel(_song.Format);
        LcdSummaryLeftText.Text = $"{formatLabel}  {_song.BeatNumerator}/{_song.BeatDenominator}";
        LcdSummaryRightText.Text = $"TEMPO {_song.TempoBpm:000}  TB {_song.TimeBase:000}";
    }

    private static RcpSong BuildUiSongFromStandardMidi(StandardMidiSong song, string fileName)
    {
        var title = string.IsNullOrWhiteSpace(song.Title)
            ? System.IO.Path.GetFileNameWithoutExtension(fileName)
            : song.Title;

        var tracks = song.Tracks.Select(t => new RcpTrack
        {
            TrackId = t.TrackId,
            Name = t.Name,
            DefaultChannel = t.DefaultChannel,
            IsMuted = t.IsMuted,
            Events = []
        }).ToList();

        return new RcpSong
        {
            Format = RcpFormat.RcpV2,
            Title = title,
            Comment = song.Comment,
            TimeBase = song.TimeBase,
            TempoBpm = song.TempoBpm,
            BeatNumerator = song.BeatNumerator,
            BeatDenominator = song.BeatDenominator,
            Cm6FileName = null,
            GsdAFileName = null,
            GsdBFileName = null,
            UserExclusives = [],
            Tracks = tracks
        };
    }

    private void UpdatePartInfoPanel()
    {
        if (_isAllDisplayMode)
        {
            LcdPartValueText.Text = "ALL";
            LcdInstrumentValueText.Text = "===";
            LcdInstrumentNameText.Text = "-SOUND Canvas-";

            LcdLevelValueText.Text = $"{GetAverage(_partVolume):000}";
            LcdPanValueText.Text = $"{GetAverage(_partPan):000}";
            LcdReverbValueText.Text = $"{GetAverage(_partReverb):000}";
            LcdChorusValueText.Text = $"{GetAverage(_partChorus):000}";
            LcdKeyShiftValueText.Text = FormatSigned(GetAverage(_partKeyShift));
            LcdDelayValueText.Text = $"{GetAverage(_partDelay):000}";
            return;
        }

        var part = Math.Clamp(_selectedPartIndex, 0, 15);
        var program = Math.Clamp(_programByChannel[part], 0, 127);
        var instrumentName = GetInstrumentName(program);

        LcdPartValueText.Text = $"{part + 1:00}";
        LcdInstrumentValueText.Text = $"{program + 1:000}";
        LcdInstrumentNameText.Text = _partMuted[part]
            ? $"{instrumentName} [MUTE]"
            : instrumentName;

        LcdLevelValueText.Text = $"{_partVolume[part]:000}";
        LcdPanValueText.Text = $"{_partPan[part]:000}";
        LcdReverbValueText.Text = $"{_partReverb[part]:000}";
        LcdChorusValueText.Text = $"{_partChorus[part]:000}";
        LcdKeyShiftValueText.Text = FormatSigned(_partKeyShift[part]);
        LcdDelayValueText.Text = $"{_partDelay[part]:000}";
    }

    private static string GetInstrumentName(int program)
    {
        if (program < 0 || program >= GmProgramNames.Length)
        {
            return "UNKNOWN";
        }

        return GmProgramNames[program];
    }

    private static int GetAverage(int[] values)
    {
        if (values.Length == 0)
        {
            return 0;
        }

        long total = 0;
        foreach (var value in values)
        {
            total += value;
        }

        return (int)Math.Clamp((int)Math.Round(total / (double)values.Length, MidpointRounding.AwayFromZero), -127, 127);
    }

    private static string FormatSigned(int value)
    {
        return value >= 0 ? $"+{value}" : value.ToString();
    }

    private static string GetFormatLabel(RcpFormat format)
    {
        return format switch
        {
            RcpFormat.RcpV2 => "RCP V2",
            RcpFormat.G36 => "RCP G36",
            _ => "RCP"
        };
    }

    private static Brush[] CreatePianoRollChannelBrushes()
    {
        var colors = new[]
        {
            Color.FromRgb(137, 79, 38), Color.FromRgb(156, 86, 44), Color.FromRgb(179, 95, 48), Color.FromRgb(193, 101, 52),
            Color.FromRgb(145, 83, 58), Color.FromRgb(168, 92, 62), Color.FromRgb(186, 103, 69), Color.FromRgb(206, 112, 73),
            Color.FromRgb(138, 88, 42), Color.FromRgb(162, 99, 47), Color.FromRgb(184, 108, 54), Color.FromRgb(201, 117, 61),
            Color.FromRgb(151, 86, 50), Color.FromRgb(173, 97, 58), Color.FromRgb(191, 108, 66), Color.FromRgb(212, 119, 75)
        };

        return colors.Select(c =>
        {
            var brush = new SolidColorBrush(c);
            if (brush.CanFreeze)
            {
                brush.Freeze();
            }

            return (Brush)brush;
        }).ToArray();
    }

    private sealed class AppPreferences
    {
        public string? LastMidiEndpointId { get; set; }
    }

    private readonly record struct PianoRollNoteVisual(Rectangle Shape, int Channel);
    private readonly record struct PianoRollNoteSegment(long StartTick, long EndTick, int Note, int Channel);
    private readonly record struct PlaybackTempoSegment(long StartTick, double StartMilliseconds, double Bpm);
}
