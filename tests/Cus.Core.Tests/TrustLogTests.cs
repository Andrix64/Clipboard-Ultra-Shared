using Cus.Core.Hosting;
using Cus.Core.Identity;
using Cus.Core.Storage;
using Cus.Core.Trust;
using Cus.Platform.Simulated;

namespace Cus.Core.Tests;

public sealed class TrustLogTests : IDisposable
{
    private readonly TempDirectory _temp = new();

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

    [Fact]
    public void Merge_RevokedDeviceCannotRevokeOthers_EvenWithBackdatedEntries()
    {
        using var db = OpenDatabase();
        var log = new TrustLog(db, _a);
        log.Merge([Pair(_a, _b), Pair(_b, _a), Pair(_a, _c), Pair(_c, _a), Pair(_b, _c)]);
        log.Revoke(_c.Info);

        // C, già revocato, firma revoche (anche retrodatate) contro B, e un'identità nuova abbinata da lui.
        var fresh = DeviceIdentity.Generate("nuovo", "x");
        var backdated = TrustEntry.Create(_c, TrustEntryKind.Revoke, _b.Info, T0.AddDays(-1));
        log.Merge([backdated, Pair(_c, fresh), TrustEntry.Create(fresh, TrustEntryKind.Revoke, _b.Info, T0.AddDays(-1))]);

        Assert.True(log.State.IsTrusted(_b.DeviceId));
        Assert.False(log.State.IsRevoked(_b.DeviceId));
        Assert.False(log.State.IsGroupMember(fresh.DeviceId));
    }

    [Fact]
    public void Merge_RevokedDeviceCanStillRevokeItsRevoker_MutualRevocation()
    {
        using var db = OpenDatabase();
        var log = new TrustLog(db, _c);
        log.Merge([Pair(_c, _a), Pair(_a, _c), Pair(_a, _b), Pair(_b, _a)]);
        log.Merge([Revoke(_a, _b)]);
        log.Merge([TrustEntry.Create(_b, TrustEntryKind.Revoke, _a.Info, T0.AddMinutes(5))]);

        Assert.True(log.State.IsRevoked(_a.DeviceId));
        Assert.True(log.State.IsRevoked(_b.DeviceId));
    }

    [Fact]
    public void Merge_RevocationMadeBeforeTheActorWasRevoked_IsKeptInTheSameBatch()
    {
        // C revoca B (B rubato), poi A revoca C: chi riceve le due voci insieme le tiene entrambe.
        using var db = OpenDatabase();
        var log = new TrustLog(db, _a);
        log.Merge([Pair(_a, _b), Pair(_b, _a), Pair(_a, _c), Pair(_c, _a)]);
        log.Merge([Revoke(_a, _c), TrustEntry.Create(_c, TrustEntryKind.Revoke, _b.Info, T0)]);

        Assert.True(log.State.IsRevoked(_b.DeviceId));
        Assert.True(log.State.IsRevoked(_c.DeviceId));
    }

    [Fact]
    public void Merge_RejectsUndefinedKind_SoTheGenuineEntryIsNotShadowed()
    {
        using var db = OpenDatabase();
        var log = new TrustLog(db, _a);
        log.Merge([Pair(_a, _b), Pair(_b, _a)]);
        var genuine = TrustEntry.Create(_a, TrustEntryKind.Revoke, _b.Info, T0.AddMinutes(1));

        Assert.False(log.Merge([genuine with { Kind = (TrustEntryKind)99 }]));
        Assert.True(log.Merge([genuine]));
        Assert.True(log.State.IsRevoked(_b.DeviceId));
    }

    public void Dispose() => _temp.Dispose();

    private CusDatabase OpenDatabase() => CusDatabase.Open(new ProfileAppPaths("trust", _temp.Path), new InMemorySecretStore());

    private static string Describe(TrustState state) =>
        string.Join(",", state.Trusted.Keys.Order()) + "|" + string.Join(",", state.Revoked.Order()) + "|" + state.SelfRevoked;

    private static TrustEntry Pair(DeviceIdentity actor, DeviceIdentity subject) =>
        TrustEntry.Create(actor, TrustEntryKind.Pair, subject.Info, T0);

    private static TrustEntry Revoke(DeviceIdentity actor, DeviceIdentity subject) =>
        TrustEntry.Create(actor, TrustEntryKind.Revoke, subject.Info, T0.AddMinutes(1));
}
