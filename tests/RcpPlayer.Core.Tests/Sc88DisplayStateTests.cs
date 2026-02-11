using System.Text;
using RcpPlayer.Core.Playback;

namespace RcpPlayer.Core.Tests;

public sealed class Sc88DisplayStateTests
{
    [Fact]
    public void TryApplySysEx_DisplayText_UpdatesTwoLines()
    {
        var state = new Sc88DisplayState();
        var now = new DateTimeOffset(2026, 2, 11, 0, 0, 0, TimeSpan.Zero);
        var data = BuildRolandDt1(0x10, 0x00, 0x00, Encoding.ASCII.GetBytes("HELLO SC88"));

        var handled = state.TryApplySysEx(data, now, out var summary);

        Assert.True(handled);
        Assert.Contains("text", summary, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("HELLO SC88", state.DisplayLine1);
        Assert.Equal(40, state.DisplayLine1.Length);
        Assert.Equal(40, state.DisplayLine2.Length);
    }

    [Fact]
    public void TryApplySysEx_DotPageAndDisplayTime_FollowsPageTimer()
    {
        var state = new Sc88DisplayState();
        var now = new DateTimeOffset(2026, 2, 11, 1, 0, 0, TimeSpan.Zero);

        var setTime = BuildRolandDt1(0x10, 0x20, 0x01, [0x03]); // 1440ms
        var dotPayload = new byte[64];
        dotPayload[0] = 0x1F;
        dotPayload[16] = 0x1F;
        dotPayload[32] = 0x1F;
        dotPayload[48] = 0x10;
        var setPage1Bits = BuildRolandDt1(0x10, 0x01, 0x00, dotPayload);

        Assert.True(state.TryApplySysEx(setTime, now, out _));
        Assert.True(state.TryApplySysEx(setPage1Bits, now, out _));
        Assert.Equal(TimeSpan.FromMilliseconds(1440), state.GetConfiguredDisplayDuration());

        var rowsActive = state.GetCurrentRows(new double[16], now.AddMilliseconds(500), out var activePage);
        Assert.Equal(1, activePage);
        Assert.Equal((ushort)0xFFFF, rowsActive[0]);

        var rowsExpired = state.GetCurrentRows(new double[16], now.AddMilliseconds(1500), out activePage);
        Assert.Equal(0, activePage);
        Assert.All(rowsExpired, row => Assert.Equal((ushort)0, row));
    }

    [Fact]
    public void TryApplySysEx_InvalidChecksum_IsRejected()
    {
        var state = new Sc88DisplayState();
        var now = new DateTimeOffset(2026, 2, 11, 2, 0, 0, TimeSpan.Zero);
        var valid = BuildRolandDt1(0x10, 0x20, 0x01, [0x05]);
        var invalid = (byte[])valid.Clone();
        invalid[^2] ^= 0x01;

        var handled = state.TryApplySysEx(invalid, now, out _);

        Assert.False(handled);
    }

    [Fact]
    public void GetDisplayLine1_LongTitle_HoldsBeforeLoopRestart()
    {
        var state = new Sc88DisplayState();
        var now = new DateTimeOffset(2026, 2, 11, 3, 0, 0, TimeSpan.Zero);
        const string title = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789ABCDEFGHIJ";
        state.SetDisplayedText(title, "READY", now);

        var startDelay = TimeSpan.FromMilliseconds(900);
        var step = TimeSpan.FromMilliseconds(220);
        var hold = TimeSpan.FromMilliseconds(500);
        var scrollSpan = TimeSpan.FromTicks((title.Length + 3) * step.Ticks); // + ScrollGap

        var holdA = state.GetDisplayLine1(now + startDelay + scrollSpan + TimeSpan.FromMilliseconds(100));
        var holdB = state.GetDisplayLine1(now + startDelay + scrollSpan + TimeSpan.FromMilliseconds(400));
        var afterRestart = state.GetDisplayLine1(now + startDelay + scrollSpan + hold + TimeSpan.FromMilliseconds(500));

        Assert.Equal(holdA, holdB);
        Assert.NotEqual(holdA, afterRestart);
        Assert.Equal(40, holdA.Length);
    }

    [Fact]
    public void GetDisplayLine1_CanDisableScrollAndResetToInitial()
    {
        var state = new Sc88DisplayState();
        var now = new DateTimeOffset(2026, 2, 11, 4, 0, 0, TimeSpan.Zero);
        const string title = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789ABCDEFGHIJ";
        state.SetDisplayedText(title, "READY", now);

        var scrolled = state.GetDisplayLine1(now + TimeSpan.FromSeconds(8), allowScroll: true);
        var frozen = state.GetDisplayLine1(now + TimeSpan.FromSeconds(8), allowScroll: false);
        state.ResetLine1Scroll(now + TimeSpan.FromSeconds(8));
        var reset = state.GetDisplayLine1(now + TimeSpan.FromSeconds(8), allowScroll: true);

        Assert.NotEqual(frozen, scrolled);
        Assert.Equal(title[..40], frozen);
        Assert.Equal(title[..40], reset);
    }

    private static byte[] BuildRolandDt1(byte addrH, byte addrM, byte addrL, byte[] payload)
    {
        var data = new byte[10 + payload.Length];
        data[0] = 0xF0;
        data[1] = 0x41;
        data[2] = 0x10;
        data[3] = 0x45;
        data[4] = 0x12;
        data[5] = addrH;
        data[6] = addrM;
        data[7] = addrL;
        payload.CopyTo(data, 8);

        var sum = addrH + addrM + addrL;
        foreach (var b in payload)
        {
            sum += b;
        }

        data[^2] = (byte)((128 - (sum & 0x7F)) & 0x7F);
        data[^1] = 0xF7;
        return data;
    }
}
