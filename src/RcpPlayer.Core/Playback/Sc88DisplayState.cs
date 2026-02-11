namespace RcpPlayer.Core.Playback;

public sealed class Sc88DisplayState
{
    private const int VisibleLineLength = 40;
    private const int TextLength = VisibleLineLength * 2;
    private const int RowCount = 16;
    private const int PartCount = 16;
    private const int DotPageCount = 10;
    private const string ScrollGap = "   ";
    private static readonly TimeSpan Line1ScrollStartDelay = TimeSpan.FromMilliseconds(900);
    private static readonly TimeSpan Line1ScrollStepInterval = TimeSpan.FromMilliseconds(220);
    private static readonly TimeSpan Line1ScrollLoopPause = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan[] DisplayDurations = BuildDisplayDurations();

    private readonly char[] _displayText = new char[TextLength];
    private readonly ushort[][] _dotPages = Enumerable.Range(0, DotPageCount)
        .Select(_ => new ushort[RowCount])
        .ToArray();

    private int _displayTime;
    private int _activeDisplayPage;
    private DateTimeOffset _activeDisplayUntilUtc;
    private string _line1Source = string.Empty;
    private DateTimeOffset _line1ScrollStartUtc;

    public Sc88DisplayState()
    {
        Reset();
    }

    public string DisplayLine1 => GetDisplayLine1(DateTimeOffset.UtcNow);

    public string DisplayLine2 => new(_displayText, VisibleLineLength, VisibleLineLength);

    public int DisplayTime => _displayTime;

    public void Reset()
    {
        Array.Fill(_displayText, ' ');
        foreach (var page in _dotPages)
        {
            Array.Fill(page, (ushort)0);
        }

        _displayTime = 6;
        _activeDisplayPage = 0;
        _activeDisplayUntilUtc = DateTimeOffset.MinValue;
        _line1Source = string.Empty;
        _line1ScrollStartUtc = DateTimeOffset.MinValue;
    }

    public void SetDisplayedText(string line1, string? line2 = null, DateTimeOffset? nowUtc = null)
    {
        line1 = NormalizeToSingleLine(line1);
        line2 = NormalizeToSingleLine(line2 ?? string.Empty);
        _line1Source = line1;
        _line1ScrollStartUtc = nowUtc ?? DateTimeOffset.UtcNow;

        Array.Fill(_displayText, ' ');
        CopyLine(line1, 0);
        CopyLine(line2, VisibleLineLength);
    }

    public string GetDisplayLine1(DateTimeOffset nowUtc)
    {
        return GetDisplayLine1(nowUtc, allowScroll: true);
    }

    public string GetDisplayLine1(DateTimeOffset nowUtc, bool allowScroll)
    {
        if (!allowScroll)
        {
            if (_line1Source.Length <= VisibleLineLength)
            {
                return new(_displayText, 0, VisibleLineLength);
            }

            return _line1Source[..VisibleLineLength];
        }

        if (_line1Source.Length <= VisibleLineLength)
        {
            return new(_displayText, 0, VisibleLineLength);
        }

        if (_line1ScrollStartUtc == DateTimeOffset.MinValue)
        {
            _line1ScrollStartUtc = nowUtc;
        }

        var elapsed = nowUtc - _line1ScrollStartUtc;
        if (elapsed <= Line1ScrollStartDelay)
        {
            return _line1Source[..VisibleLineLength];
        }

        var scrollElapsed = elapsed - Line1ScrollStartDelay;
        var scrollSource = _line1Source + ScrollGap;
        var stepTicks = Line1ScrollStepInterval.Ticks;
        var scrollTicks = scrollSource.Length * stepTicks;
        var cycleTicks = scrollTicks + Line1ScrollLoopPause.Ticks;
        var cycleElapsedTicks = scrollElapsed.Ticks % cycleTicks;
        if (cycleElapsedTicks < 0)
        {
            cycleElapsedTicks += cycleTicks;
        }

        var offset = cycleElapsedTicks >= scrollTicks
            ? scrollSource.Length - 1
            : (int)(cycleElapsedTicks / stepTicks);
        var wrapped = scrollSource + _line1Source;
        return wrapped.Substring(offset, VisibleLineLength);
    }

    public void ResetLine1Scroll(DateTimeOffset? nowUtc = null)
    {
        _line1ScrollStartUtc = nowUtc ?? DateTimeOffset.UtcNow;
    }

    public bool TryApplySysEx(byte[]? data, DateTimeOffset nowUtc, out string summary)
    {
        summary = string.Empty;

        if (data is null || data.Length < 11)
        {
            return false;
        }

        if (data[0] != 0xF0 || data[^1] != 0xF7)
        {
            return false;
        }

        if (data[1] != 0x41 || data[3] != 0x45 || data[4] != 0x12)
        {
            return false;
        }

        if (!RolandChecksumValid(data))
        {
            summary = "SC-88 display SysEx checksum error";
            return false;
        }

        var addrH = data[5];
        var addrM = data[6];
        var addrL = data[7];
        var payloadCount = data.Length - 10;
        var payload = payloadCount > 0
            ? data.AsSpan(8, payloadCount)
            : ReadOnlySpan<byte>.Empty;

        if (addrH == 0x10 && addrM == 0x00 && addrL == 0x00)
        {
            ApplyDisplayedLetter(payload, nowUtc);
            summary = "SC-88 display text updated";
            return true;
        }

        if (addrH == 0x10 && addrM >= 0x01 && addrM <= 0x05 && (addrL == 0x00 || addrL == 0x40))
        {
            if (payload.Length < 64)
            {
                return false;
            }

            var pageNo = (addrM - 1) * 2 + (addrL / 0x40);
            ApplyDotPage(pageNo, payload[..64]);
            if (pageNo == 0)
            {
                ApplyDisplayPageInternal(1, nowUtc);
            }

            summary = $"SC-88 dot page {pageNo + 1:00} updated";
            return true;
        }

        if (addrH == 0x10 && addrM == 0x20 && addrL == 0x00 && payload.Length >= 1)
        {
            var value = payload[0];
            if (value <= 10)
            {
                ApplyDisplayPageInternal(value, nowUtc);
                summary = value == 0
                    ? "SC-88 display page cleared"
                    : $"SC-88 display page set to {value:00}";
                return true;
            }

            return false;
        }

        if (addrH == 0x10 && addrM == 0x20 && addrL == 0x01 && payload.Length >= 1)
        {
            var value = payload[0];
            if (value <= 0x0F)
            {
                _displayTime = value;
                summary = $"SC-88 display time set to {GetConfiguredDisplayDuration().TotalMilliseconds:0}ms";
                return true;
            }

            return false;
        }

        return false;
    }

    public TimeSpan GetConfiguredDisplayDuration()
    {
        return DisplayDurations[Math.Clamp(_displayTime, 0, DisplayDurations.Length - 1)];
    }

    public int GetCurrentDisplayPage(DateTimeOffset nowUtc)
    {
        if (_activeDisplayPage == 0)
        {
            return 0;
        }

        if (nowUtc <= _activeDisplayUntilUtc)
        {
            return _activeDisplayPage;
        }

        _activeDisplayPage = 0;
        _activeDisplayUntilUtc = DateTimeOffset.MinValue;
        return 0;
    }

    public ushort[] GetCurrentRows(IReadOnlyList<double>? partLevels, DateTimeOffset nowUtc, out int activeDisplayPage)
    {
        activeDisplayPage = GetCurrentDisplayPage(nowUtc);
        if (activeDisplayPage == 0)
        {
            return BuildBarRows(partLevels);
        }

        return (ushort[])_dotPages[activeDisplayPage - 1].Clone();
    }

    private void ApplyDisplayPageInternal(int displayPage, DateTimeOffset nowUtc)
    {
        if (displayPage <= 0)
        {
            _activeDisplayPage = 0;
            _activeDisplayUntilUtc = DateTimeOffset.MinValue;
            return;
        }

        _activeDisplayPage = Math.Clamp(displayPage, 1, DotPageCount);
        _activeDisplayUntilUtc = nowUtc + GetConfiguredDisplayDuration();
    }

    private void ApplyDisplayedLetter(ReadOnlySpan<byte> payload, DateTimeOffset nowUtc)
    {
        var len = Math.Min(payload.Length, VisibleLineLength);
        var chars = new char[len];
        for (var i = 0; i < len; i++)
        {
            chars[i] = DecodeDisplayCharacter(payload[i]);
        }

        var text = new string(chars).TrimEnd();
        SetDisplayedText(text, string.Empty, nowUtc);
    }

    private void ApplyDotPage(int pageNo, ReadOnlySpan<byte> payload64)
    {
        if (pageNo < 0 || pageNo >= DotPageCount)
        {
            return;
        }

        var page = _dotPages[pageNo];
        for (var row = 0; row < RowCount; row++)
        {
            var d0 = payload64[row] & 0x1F;
            var d1 = payload64[row + 16] & 0x1F;
            var d2 = payload64[row + 32] & 0x1F;
            var d3 = payload64[row + 48] & 0x1F;
            page[row] = (ushort)((d0 << 11) | (d1 << 6) | (d2 << 1) | (d3 >> 4));
        }
    }

    private static bool RolandChecksumValid(ReadOnlySpan<byte> data)
    {
        var sum = 0;
        for (var i = 5; i < data.Length - 1; i++)
        {
            sum += data[i];
        }

        return (sum & 0x7F) == 0;
    }

    private static char DecodeDisplayCharacter(byte value)
    {
        return value switch
        {
            >= 0x20 and <= 0x7E => (char)value,
            0x7F => '<',
            _ => ' '
        };
    }

    private static ushort[] BuildBarRows(IReadOnlyList<double>? partLevels)
    {
        var rows = new ushort[RowCount];
        if (partLevels is null)
        {
            return rows;
        }

        for (var part = 0; part < Math.Min(partLevels.Count, PartCount); part++)
        {
            var clamped = Math.Clamp(partLevels[part], 0.0, 100.0);
            var height = (int)Math.Round(clamped * RowCount / 100.0, MidpointRounding.AwayFromZero);
            for (var y = 0; y < height; y++)
            {
                var row = RowCount - 1 - y;
                rows[row] |= (ushort)(1 << (15 - part));
            }
        }

        return rows;
    }

    private static TimeSpan[] BuildDisplayDurations()
    {
        return Enumerable.Range(0, 16)
            .Select(i => TimeSpan.FromMilliseconds(i * 480))
            .ToArray();
    }

    private void CopyLine(string value, int offset)
    {
        var len = Math.Min(VisibleLineLength, value.Length);
        for (var i = 0; i < len; i++)
        {
            _displayText[offset + i] = value[i];
        }
    }

    private static string NormalizeToSingleLine(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();
    }
}
