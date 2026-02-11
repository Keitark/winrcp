using RcpPlayer.Core.Playback;
using Windows.Devices.Enumeration;
using Windows.Devices.Midi;
using Windows.Storage.Streams;

namespace RcpPlayer.App.Midi;

public sealed class WinRtMidiOutput : IMidiOutput
{
    private IMidiOutPort? _port;

    public async Task<IReadOnlyList<MidiEndpointInfo>> GetEndpointsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var selector = MidiOutPort.GetDeviceSelector();
        var deviceInfos = await DeviceInformation.FindAllAsync(selector);
        return deviceInfos.Select(d => new MidiEndpointInfo { Id = d.Id, Name = d.Name }).ToList();
    }

    public async Task OpenAsync(string endpointId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpointId);
        cancellationToken.ThrowIfCancellationRequested();

        await CloseAsync();

        _port = await MidiOutPort.FromIdAsync(endpointId)
            ?? throw new InvalidOperationException("Could not open the selected MIDI endpoint.");
    }

    public Task SendShortAsync(uint shortMessage, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureOpen();

        var status = (byte)(shortMessage & 0xFF);
        var data1 = (byte)((shortMessage >> 8) & 0x7F);
        var data2 = (byte)((shortMessage >> 16) & 0x7F);
        var channel = (byte)(status & 0x0F);
        var statusKind = status & 0xF0;

        IMidiMessage? message = statusKind switch
        {
            0x80 => new MidiNoteOffMessage(channel, data1, data2),
            0x90 => new MidiNoteOnMessage(channel, data1, data2),
            0xA0 => new MidiPolyphonicKeyPressureMessage(channel, data1, data2),
            0xB0 => new MidiControlChangeMessage(channel, data1, data2),
            0xC0 => new MidiProgramChangeMessage(channel, data1),
            0xD0 => new MidiChannelPressureMessage(channel, data1),
            0xE0 => new MidiPitchBendChangeMessage(channel, (ushort)((data2 << 7) | data1)),
            _ => null
        };

        if (message is not null)
        {
            _port!.SendMessage(message);
        }

        return Task.CompletedTask;
    }

    public Task SendSysExAsync(byte[] data, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureOpen();

        if (data.Length == 0)
        {
            return Task.CompletedTask;
        }

        using var writer = new DataWriter();
        writer.WriteBytes(data);
        _port!.SendMessage(new MidiSystemExclusiveMessage(writer.DetachBuffer()));
        return Task.CompletedTask;
    }

    public Task CloseAsync()
    {
        if (_port is IDisposable disposable)
        {
            disposable.Dispose();
        }
        _port = null;
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await CloseAsync();
    }

    private void EnsureOpen()
    {
        if (_port is null)
        {
            throw new InvalidOperationException("MIDI output is not open.");
        }
    }
}
