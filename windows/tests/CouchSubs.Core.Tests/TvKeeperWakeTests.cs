// SPDX-License-Identifier: AGPL-3.0-only

using CouchSubs.Core.LgTv;
using Xunit;

namespace CouchSubs.Core.Tests;

public class TvKeeperWakeTests
{
    private static readonly Dictionary<string, string> Params = new() { ["server"] = "http://1.2.3.4:8787" };

    private static Task<ITvControl> Unreachable() => throw new SsapException("cannot connect");

    [Fact]
    public async Task WakesSilentTvBeforeFirstConnection()
    {
        var wakes = 0;
        var keeper = new TvKeeper(Unreachable, "app", Params, appConnected: () => false, relaunchInterval: TimeSpan.Zero,
            restart: () => throw new HttpRequestException(), wake: () => { wakes++; return Task.CompletedTask; });
        await keeper.TickAsync();
        Assert.Equal(TvReach.Unreachable, keeper.Reach);
        Assert.Equal(1, wakes);
        await keeper.TickAsync(); // 60 sn dolmadan yeniden uyandırılmaz
        Assert.Equal(1, wakes);
    }

    [Fact]
    public async Task DoesNotWakeTvTurnedOffMidSessionUnlessAsked()
    {
        var connected = true;
        var wakes = 0;
        var keeper = new TvKeeper(Unreachable, "app", Params, appConnected: () => connected, relaunchInterval: TimeSpan.Zero,
            restart: () => throw new HttpRequestException(), wake: () => { wakes++; return Task.CompletedTask; });
        await keeper.TickAsync();
        Assert.Equal(TvReach.Connected, keeper.Reach);
        connected = false; // kullanıcı TV'yi kapattı
        await keeper.TickAsync();
        await keeper.TickAsync();
        Assert.Equal(TvReach.Unreachable, keeper.Reach);
        Assert.Equal(0, wakes);

        keeper.RepairNow(); // "TV'yi yeniden bağla"
        await keeper.TickAsync();
        Assert.Equal(1, wakes);
    }

    [Fact]
    public async Task RepairRestartsAppRightAway()
    {
        var restarts = 0;
        var keeper = new TvKeeper(Unreachable, "app", Params, appConnected: () => false, relaunchInterval: TimeSpan.FromMinutes(5),
            restart: () => { restarts++; return Task.CompletedTask; });
        await keeper.TickAsync(); // 1. deneme: uzaktan kumanda ile açma (başarısız)
        keeper.RepairNow();
        await keeper.TickAsync();
        Assert.Equal(1, restarts);
        Assert.Equal(TvReach.AppAway, keeper.Reach);
    }

    [Fact]
    public void MagicPacketRepeatsMac()
    {
        var mac = WakeOnLan.ParseMac("e8-aa-cb-d6-0f-80")!;
        var packet = WakeOnLan.MagicPacket(mac);
        Assert.Equal(102, packet.Length);
        Assert.All(packet[..6], b => Assert.Equal(0xFF, b));
        Assert.Equal(mac, packet[96..102]);
        Assert.Null(WakeOnLan.ParseMac("00:00:00:00:00:00"));
        Assert.Null(WakeOnLan.ParseMac("bozuk"));
    }
}
