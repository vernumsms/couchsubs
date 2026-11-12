// SPDX-License-Identifier: AGPL-3.0-only

namespace CouchSubs.RemotePlay.Tests;

public class ReconnectPolicyTests
{
    [Fact]
    public void BackoffDoublesUpToThirtySeconds()
    {
        var policy = new ReconnectPolicy();
        Assert.Equal([2, 4, 8, 16, 30, 30, 30], Enumerable.Range(0, 7).Select(_ => policy.NextDelay()));
    }

    [Fact]
    public void ResetStartsOver()
    {
        var policy = new ReconnectPolicy();
        policy.NextDelay();
        policy.NextDelay();
        policy.Reset();
        Assert.Equal(2, policy.NextDelay());
    }
}

public class ConsoleRelocationTests
{
    private static readonly RegisteredConsole Saved = new("192.168.1.15", "PS5-365", new byte[16], new byte[16]);

    [Fact]
    public void PicksSameNameAtNewAddress() =>
        Assert.Equal("192.168.1.6", StreamSession.PickMoved(Saved, [new("192.168.1.6", "PS5-365", false), new("192.168.1.9", "PS5-Diger", true)])?.Host);

    [Fact]
    public void PicksOnlyConsoleWhenNameChanged() =>
        Assert.Equal("192.168.1.6", StreamSession.PickMoved(Saved, [new("192.168.1.6", "Salon", true)])?.Host);

    [Fact]
    public void DoesNotGuessBetweenUnknownConsoles() =>
        Assert.Null(StreamSession.PickMoved(Saved, [new("192.168.1.6", "A", true), new("192.168.1.7", "B", true)]));

    [Fact]
    public void IgnoresSavedAddressAndEmptyScan()
    {
        Assert.Null(StreamSession.PickMoved(Saved, []));
        Assert.Null(StreamSession.PickMoved(Saved, [new("192.168.1.15", "PS5-365", true)]));
    }
}

public class RegisteredConsoleTests
{
    [Fact]
    public void WakeCredentialIsHexOfRegistKey()
    {
        var key = "1a2b3c4d"u8.ToArray().Concat(new byte[8]).ToArray();
        var console = new RegisteredConsole("192.168.1.253", "PS5", key, Enumerable.Repeat((byte)7, 16).ToArray());
        Assert.Equal(0x1a2b3c4dUL, console.WakeCredential);
    }
}

public class ConsoleSubnetTests
{
    [Fact]
    public void Slash24ListsOtherHosts()
    {
        var hosts = ConsoleDiscovery.Hosts(0xC0A8_010A, 0xFFFF_FF00); // 192.168.1.10/24
        Assert.Equal(253, hosts.Count);
        Assert.Equal(0xC0A8_0101u, hosts[0]);
        Assert.Equal(0xC0A8_01FEu, hosts[^1]);
        Assert.DoesNotContain(0xC0A8_010Au, hosts);
    }

    [Fact]
    public void Slash22IsTheLargestScannedNetwork()
    {
        Assert.Equal(1021, ConsoleDiscovery.Hosts(0xC0A8_0001, 0xFFFF_FC00).Count);
        Assert.Empty(ConsoleDiscovery.Hosts(0xC0A8_0001, 0xFFFF_F800));
    }

    [Fact]
    public void LargeNetworksAreSkipped()
    {
        Assert.Empty(ConsoleDiscovery.Hosts(0x0A00_0001, 0xFFFF_0000));
    }

    [Fact]
    public void AddressesConvertBothWays()
    {
        Assert.Equal(0xC0A8_010Au, ConsoleDiscovery.ToUInt32(System.Net.IPAddress.Parse("192.168.1.10")));
        Assert.Equal("192.168.1.254", ConsoleDiscovery.ToText(0xC0A8_01FE));
    }

    [Fact]
    public void LocalTargetsAlwaysIncludeBroadcast()
    {
        var targets = ConsoleDiscovery.LocalTargets();
        Assert.Contains("255.255.255.255", targets);
        Assert.Equal(targets.Count, targets.Distinct().Count());
        Assert.DoesNotContain("127.0.0.1", targets);
    }
}

public class DisconnectReasonTests
{
    [Fact]
    public void QuitReasonsMapToUserFacingCases()
    {
        Assert.Equal(new DisconnectReason(DisconnectKind.InUse), DisconnectReasons.FromNative(NativeMethods.QuitInUse, null));
        Assert.Equal(new DisconnectReason(DisconnectKind.Stopped), DisconnectReasons.FromNative(NativeMethods.QuitStopped, null));
        Assert.Equal(new DisconnectReason(DisconnectKind.Refused), DisconnectReasons.FromNative(NativeMethods.QuitRefused, "ignored"));
        Assert.Equal(new DisconnectReason(DisconnectKind.Network), DisconnectReasons.FromNative(NativeMethods.QuitNetwork, null));
        Assert.Equal(new DisconnectReason(DisconnectKind.Other, "Version Mismatch"),
                     DisconnectReasons.FromNative(NativeMethods.QuitOther, "Version Mismatch"));
    }
}

public class RegistrationReasonTests
{
    [Fact]
    public void InvalidPsnIdMeansWrongAccount()
    {
        Assert.Equal(RegistrationFailure.WrongAccount, Registration.FailureFrom(0x80108b02));
        Assert.Equal(RegistrationFailure.Failed, Registration.FailureFrom(0x80108b09));
        Assert.Equal(RegistrationFailure.Failed, Registration.FailureFrom(null));
    }

    [Fact]
    public async Task AccountIdMustBeEightBytes()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Registration.RegisterAsync("192.168.1.2", new byte[4], 12345678));
    }
}

public class NativeHelperTests
{
    [Fact]
    public void PaddedCopiesPrefixAndFillsZeros()
    {
        Assert.Equal(new byte[] { 1, 2, 0, 0 }, NativeMethods.Padded([1, 2], 4));
        Assert.Equal(new byte[] { 1, 2 }, NativeMethods.Padded([1, 2, 3], 2));
    }

    [Fact]
    public unsafe void FixedStringStopsAtZero()
    {
        var bytes = "PS5-123\0garbage"u8.ToArray();
        fixed (byte* pointer = bytes)
            Assert.Equal("PS5-123", NativeMethods.FixedString(pointer, bytes.Length));
    }
}
