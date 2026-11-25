using Cus.Core.Abstractions;
using Cus.Core.Friends;
using Cus.Core.Hosting;
using Cus.Core.Identity;
using Cus.Core.Storage;
using Cus.Platform.Simulated;

namespace Cus.Core.Tests;

public sealed class FriendStoreTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly CusDatabase _database;
    private readonly FriendStore _friends;
    private readonly string _luca = DeviceIdentity.Generate("luca", "u").DeviceId;

    public FriendStoreTests()
    {
        _database = CusDatabase.Open(new ProfileAppPaths("alice", _temp.Path), new InMemorySecretStore());
        _friends = new FriendStore(_database);
    }

    [Fact]
    public void Remove_DeletesTheFriend_AndNotifiesOnlyWhenSomethingWasThere()
    {
        _friends.SetAgreement(_luca, "Luca", AgreementLevel.Anytime);
        var changes = 0;
        _friends.Changed += (_, _) => changes++;

        _friends.Remove(_luca);
        _friends.Remove(_luca);

        Assert.Null(_friends.Get(_luca));
        Assert.Equal(1, changes);

        // Un indirizzo ricordato dopo la rimozione non riporta in vita l'amico.
        _friends.RememberEndpoint(_luca, "Luca", "192.168.1.30", 23700);
        Assert.Null(_friends.Get(_luca));
    }

    [Fact]
    public void LowerAgreement_NeverRaisesIt()
    {
        _friends.SetAgreement(_luca, "Luca", AgreementLevel.AutoJoin);

        Assert.False(_friends.LowerAgreement(_luca, AgreementLevel.Anytime));
        Assert.Equal(AgreementLevel.AutoJoin, _friends.AgreementWith(_luca));
        Assert.False(_friends.LowerAgreement(_luca, AgreementLevel.AutoJoin));
        Assert.True(_friends.LowerAgreement(_luca, AgreementLevel.None));
        Assert.Equal(AgreementLevel.None, _friends.AgreementWith(_luca));
    }

    [Fact]
    public void LowerAgreement_WithAnUnknownLevel_DropsIt()
    {
        _friends.SetAgreement(_luca, "Luca", AgreementLevel.Anytime);

        Assert.True(_friends.LowerAgreement(_luca, (AgreementLevel)99));

        Assert.Equal(AgreementLevel.None, _friends.AgreementWith(_luca));
    }

    [Fact]
    public void LowerAgreement_ForAStranger_CreatesNothing()
    {
        Assert.False(_friends.LowerAgreement(_luca, AgreementLevel.None));
        Assert.Empty(_friends.All);
    }

    [Fact]
    public void EmptyEntries_AreDeleted()
    {
        _friends.SetAgreement(_luca, "Luca", AgreementLevel.AutoJoin);
        _friends.SetSensitiveTrust(_luca, "Luca", trusted: true);

        _friends.SetAgreement(_luca, "Luca", AgreementLevel.None);
        Assert.True(_friends.TrustsWithSensitive(_luca));
        Assert.Single(_friends.All);

        _friends.SetSensitiveTrust(_luca, "Luca", trusted: false);
        Assert.Empty(_friends.All);
        Assert.Null(_database.GetFriend(_luca));
    }

    [Fact]
    public void RememberEndpoint_OnlyForExistingFriends()
    {
        _friends.RememberEndpoint(_luca, "Luca", "192.168.1.5", 23701);
        Assert.Empty(_friends.All);

        _friends.SetAgreement(_luca, "Luca", AgreementLevel.Anytime);
        _friends.RememberEndpoint(_luca, "Luca (nuovo nome)", "192.168.1.5", 23701);

        var friend = _friends.Get(_luca)!;
        Assert.Equal(new PeerEndpoint("192.168.1.5", 23701), friend.LastEndpoint);
        Assert.Equal("Luca (nuovo nome)", friend.DisplayName);
    }

    [Fact]
    public void Changes_RaiseTheEvent_OnlyWhenSomethingChanges()
    {
        var raised = 0;
        _friends.Changed += (_, _) => raised++;

        _friends.SetSensitiveTrust(_luca, "Luca", trusted: true);
        _friends.SetSensitiveTrust(_luca, "Luca", trusted: true);

        Assert.Equal(1, raised);
    }

    public void Dispose()
    {
        _database.Dispose();
        _temp.Dispose();
    }
}

public class ClipboardSummaryTests
{
    [Fact]
    public void Text_IsDescribedByItsLength()
    {
        var summary = ClipboardSummary.From(ClipboardContent.FromText("perché"), "ID", "Luca", isSensitive: false, isShared: true);

        Assert.Equal("testo di 6 caratteri", summary.Title);
        Assert.Equal(7, summary.SizeBytes); // "é" occupa due byte in UTF-8
        Assert.Equal("Luca", summary.OriginName);
        Assert.Equal(ClipboardContentKind.Text, summary.Kind);
    }

    [Fact]
    public void Files_AreDescribedByTheTopLevelName()
    {
        var single = ClipboardSummary.From(ClipboardContent.FromFiles([new ClipboardFileInfo("relazione.pdf", 100)]), "ID", "Luca", false, true);
        var folder = ClipboardSummary.From(ClipboardContent.FromFiles(
            [new ClipboardFileInfo("progetto/a.txt", 10), new ClipboardFileInfo("progetto/sub/b.txt", 5)]), "ID", "Luca", false, true);
        var many = ClipboardSummary.From(ClipboardContent.FromFiles(
            [new ClipboardFileInfo("a.txt", 1), new ClipboardFileInfo("b.txt", 2), new ClipboardFileInfo("c/d.txt", 3)]), "ID", "Luca", false, true);

        Assert.Equal("relazione.pdf", single.Title);
        Assert.Equal(100, single.SizeBytes);
        Assert.Equal("progetto", folder.Title);
        Assert.Equal(15, folder.SizeBytes);
        Assert.Equal("3 elementi", many.Title);
        Assert.Equal(6, many.SizeBytes);
    }

    [Fact]
    public void Sensitive_HidesTitleAndSize()
    {
        var summary = ClipboardSummary.From(ClipboardContent.FromText("password"), "ID", "Luca", isSensitive: true, isShared: false);

        Assert.Equal("contenuto sensibile", summary.Title);
        Assert.Null(summary.SizeBytes);
        Assert.True(summary.IsSensitive);
        Assert.False(summary.IsShared);
    }
}
