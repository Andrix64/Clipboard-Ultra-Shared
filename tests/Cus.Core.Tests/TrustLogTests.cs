using Cus.Core.Identity;
using Cus.Core.Trust;

namespace Cus.Core.Tests;

public class TrustLogTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 24, 10, 0, 0, TimeSpan.Zero);

    private readonly DeviceIdentity _a = DeviceIdentity.Generate("a", "u");
    private readonly DeviceIdentity _b = DeviceIdentity.Generate("b", "u");
    private readonly DeviceIdentity _c = DeviceIdentity.Generate("c", "u");
    private readonly DeviceIdentity _stranger = DeviceIdentity.Generate("estraneo", "x");

    [Fact]
    public void PairingClosure_TrustsDevicesPairedThroughOthers()
    {
        var state = TrustLog.Compute(_c.SignPublicKey, [Pair(_c, _a), Pair(_a, _c), Pair(_a, _b), Pair(_b, _a)]);

        Assert.True(state.IsTrusted(_a.DeviceId));
        Assert.True(state.IsTrusted(_b.DeviceId));
        Assert.False(state.IsTrusted(_c.DeviceId));
    }

    [Fact]
    public void Revocation_ByGroupMember_IsPermanent_AndStrangersCannotRevoke()
    {
        var entries = new List<TrustEntry>
        {
            Pair(_a, _b), Pair(_b, _a), Pair(_a, _c), Pair(_c, _a),
            Revoke(_stranger, _c),
            Revoke(_a, _b),
            Pair(_c, _b), // ri-abbinare la stessa chiave non annulla la revoca
        };

        var state = TrustLog.Compute(_c.SignPublicKey, entries);

        Assert.True(state.IsRevoked(_b.DeviceId));
        Assert.False(state.IsTrusted(_b.DeviceId));
        Assert.True(state.IsTrusted(_a.DeviceId));
        Assert.False(state.SelfRevoked);
    }

    [Fact]
    public void MutualRevocation_RevokesBoth_AndSelfRevocationIsDetected()
    {
        var entries = new List<TrustEntry> { Pair(_a, _b), Pair(_b, _a), Pair(_a, _c), Pair(_c, _a), Revoke(_a, _b), Revoke(_b, _a) };

        var fromC = TrustLog.Compute(_c.SignPublicKey, entries);
        var fromA = TrustLog.Compute(_a.SignPublicKey, entries);

        Assert.True(fromC.IsRevoked(_a.DeviceId) && fromC.IsRevoked(_b.DeviceId));
        Assert.Empty(fromC.Trusted);
        Assert.True(fromA.SelfRevoked);
    }

    [Fact]
    public void DevicesPairedByARevokedDevice_AreNoLongerTrustedThroughIt()
    {
        var entries = new List<TrustEntry> { Pair(_a, _b), Pair(_b, _a), Pair(_b, _stranger), Revoke(_a, _b) };

        var state = TrustLog.Compute(_a.SignPublicKey, entries);

        Assert.False(state.IsTrusted(_stranger.DeviceId));
    }

    [Fact]
    public void Result_IsIndependentOfOrder()
    {
        var entries = new List<TrustEntry>
        {
            Pair(_a, _b), Pair(_b, _a), Pair(_a, _c), Pair(_c, _a), Pair(_b, _c), Revoke(_c, _b), Revoke(_b, _c), Pair(_a, _stranger),
        };
        var expected = Describe(TrustLog.Compute(_a.SignPublicKey, entries));

        var random = new Random(42);
        for (var i = 0; i < 50; i++)
        {
            var shuffled = entries.OrderBy(_ => random.Next()).ToList();
            Assert.Equal(expected, Describe(TrustLog.Compute(_a.SignPublicKey, shuffled)));
        }
    }

    [Fact]
    public void ForgedSignature_IsIgnored()
    {
        var genuine = Revoke(_a, _b);
        var forged = genuine with { Signature = new byte[64] };

        var state = TrustLog.Compute(_a.SignPublicKey, [Pair(_a, _b), forged]);

        Assert.True(state.IsTrusted(_b.DeviceId));
        Assert.False(forged.HasValidSignature());
    }

    private static string Describe(TrustState state) =>
        string.Join(",", state.Trusted.Keys.Order()) + "|" + string.Join(",", state.Revoked.Order()) + "|" + state.SelfRevoked;

    private static TrustEntry Pair(DeviceIdentity actor, DeviceIdentity subject) =>
        TrustEntry.Create(actor, TrustEntryKind.Pair, subject.Info, T0);

    private static TrustEntry Revoke(DeviceIdentity actor, DeviceIdentity subject) =>
        TrustEntry.Create(actor, TrustEntryKind.Revoke, subject.Info, T0.AddMinutes(1));
}
