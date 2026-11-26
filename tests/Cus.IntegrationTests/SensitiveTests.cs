using Cus.Core.Node;

namespace Cus.IntegrationTests;

/// <summary>
/// Contenuti marcati sensibili (password): ognuno decide per le proprie copie. Vanno agli amici
/// affidabili e, per le copie dell'host, a tutti se l'host lo concede per la sessione.
/// </summary>
public sealed class SensitiveTests : IAsyncLifetime
{
    private readonly TestRoot _root = new();
    private readonly List<TestNode> _nodes = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var node in _nodes)
            await node.DisposeAsync();
        _root.Dispose();
    }

    [Fact]
    public async Task HostSensitiveCopy_ReachesOnlyTrustedMembers()
    {
        var (host, b, c) = await StartGroupAsync();

        host.CopySensitive("password-1");
        host.Copy("normale-1");
        await Eventually.TrueAsync(() => b.Text == "normale-1" && c.Text == "normale-1", "il testo normale arriva a tutti");
        Assert.DoesNotContain(b.ClipboardEvents, e => e.Content.Text == "password-1");
        Assert.False(host.Node.CurrentClipboard!.IsSensitive);

        host.Node.SetSensitiveTrust(b.Node.Identity.DeviceId, "b", trusted: true);
        host.CopySensitive("password-2");

        await Eventually.TrueAsync(() => b.Text == "password-2", "b, affidabile, riceve la password");
        Assert.True(b.Clipboard.Current!.IsMarkedSensitive);
        Assert.True(b.Node.CurrentClipboard!.IsSensitive);
        Assert.Equal("contenuto sensibile", b.Node.CurrentClipboard.Title);
        Assert.Null(b.Node.CurrentClipboard.SizeBytes);
        Assert.Equal("contenuto sensibile", b.Overlay.State?.Current?.Title);
        await Task.Delay(300);
        Assert.Equal("normale-1", c.Text);
        Assert.True(host.Node.CurrentClipboard!.IsShared);

        var history = await host.Node.GetHistoryAsync();
        Assert.DoesNotContain(history, h => h.Content.Text is "password-1" or "password-2");
    }

    [Fact]
    public async Task SensitiveForAll_IsTemporary_AndOnlyForTheHostsCopies()
    {
        var (host, b, c) = await StartGroupAsync();

        await host.Node.SetSensitiveForAllAsync(true);
        await Eventually.TrueAsync(() => c.Node.Session?.SensitiveForAll == true, "i membri sanno del permesso");
        host.CopySensitive("password-per-tutti");
        await Eventually.TrueAsync(() => b.Text == "password-per-tutti" && c.Text == "password-per-tutti", "tutti la ricevono");

        // Il permesso dell'host non vale per le copie dei membri.
        b.Node.SetSensitiveTrust(host.Node.Identity.DeviceId, "host", trusted: true);
        b.CopySensitive("password-di-b");
        await Eventually.TrueAsync(() => host.Text == "password-di-b", "l'host, affidabile per b, la riceve");
        await Task.Delay(300);
        Assert.Equal("password-per-tutti", c.Text);

        // A sessione finita il permesso non c'è più.
        await host.Node.LeaveSessionAsync();
        await host.ShowAsync();
        Assert.False(host.Node.Session!.SensitiveForAll);
    }

    [Fact]
    public async Task SensitiveForAll_AndHistory_DoNotPassToWhoJoinsAfterTheLastMemberLeft()
    {
        // L'host resta visibile mentre la sessione si svuota: chi entra dopo è in una sessione nuova.
        var host = await Start("host");
        var b = await Start("b");
        var c = await Start("c");
        await host.ShowAsync();
        Assert.Equal(JoinOutcome.Accepted, (await b.JoinAsync(host)).Outcome);
        await host.Node.SetSensitiveForAllAsync(true);
        host.Copy("solo per b");
        await Eventually.TrueAsync(() => b.Text == "solo per b", "b riceve la copia");
        var firstSession = host.Node.Session!.SessionId;

        await b.Node.LeaveSessionAsync();
        await Eventually.TrueAsync(() => host.Node.Session?.IsSharing == false, "b è uscito");
        Assert.True(host.Node.IsVisible);
        Assert.False(host.Node.Session!.SensitiveForAll);
        Assert.NotEqual(firstSession, host.Node.Session.SessionId);

        Assert.Equal(JoinOutcome.Accepted, (await c.JoinAsync(host)).Outcome);
        await Eventually.TrueAsync(() => host.Node.Session?.IsSharing == true, "c è dentro");
        Assert.Empty(await c.Node.GetHistoryAsync());

        host.CopySensitive("password-dopo");
        host.Copy("normale-dopo");
        await Eventually.TrueAsync(() => c.Text == "normale-dopo", "c riceve le copie normali");
        Assert.DoesNotContain(c.ClipboardEvents, e => e.Content.Text == "password-dopo");
    }

    [Fact]
    public async Task MemberSensitiveCopy_StaysHome_WhenTheMemberDoesNotTrustTheHost()
    {
        var (host, b, c) = await StartGroupAsync();
        b.Node.SetSensitiveTrust(c.Node.Identity.DeviceId, "c", trusted: true);

        b.CopySensitive("password-di-b");
        b.Copy("normale");

        await Eventually.TrueAsync(() => host.Text == "normale" && c.Text == "normale", "il testo normale arriva");
        Assert.DoesNotContain(host.ClipboardEvents, e => e.Content.Text == "password-di-b");
        Assert.DoesNotContain(c.ClipboardEvents, e => e.Content.Text == "password-di-b");
    }

    [Fact]
    public async Task MemberSensitiveCopy_GoesToTheHost_AndOnlyToMembersTheOriginTrusts()
    {
        var (host, b, c, d) = await StartGroupAsync(withFourth: true);
        b.Node.SetSensitiveTrust(host.Node.Identity.DeviceId, "host", trusted: true);
        b.Node.SetSensitiveTrust(c.Node.Identity.DeviceId, "c", trusted: true);

        b.CopySensitive("password-di-b");

        await Eventually.TrueAsync(() => host.Text == "password-di-b" && c.Text == "password-di-b", "host e c la ricevono");
        Assert.True(host.Clipboard.Current!.IsMarkedSensitive);
        Assert.True(host.Node.CurrentClipboard!.IsSensitive);
        Assert.Equal("b", host.Node.CurrentClipboard.OriginName);
        await Task.Delay(300);
        Assert.DoesNotContain(d!.ClipboardEvents, e => e.Content.Text == "password-di-b");
        Assert.DoesNotContain(await host.Node.GetHistoryAsync(), h => h.Content.Text == "password-di-b");
    }

    private async Task<(TestNode Host, TestNode B, TestNode C, TestNode? D)> StartGroupAsync(bool withFourth)
    {
        var host = await Start("host");
        await host.ShowAsync();
        var members = new List<TestNode>();
        string[] names = withFourth ? ["b", "c", "d"] : ["b", "c"];
        foreach (var name in names)
        {
            var member = await Start(name);
            await member.JoinAsync(host);
            members.Add(member);
        }

        await Eventually.TrueAsync(() => members.All(m => m.Node.Session?.Participants.Count == members.Count + 1), "tutti vedono tutti");
        return (host, members[0], members[1], withFourth ? members[2] : null);
    }

    private async Task<(TestNode Host, TestNode B, TestNode C)> StartGroupAsync()
    {
        var (host, b, c, _) = await StartGroupAsync(withFourth: false);
        return (host, b, c);
    }

    private async Task<TestNode> Start(string name)
    {
        var node = await TestNode.StartAsync(_root.Path, name);
        _nodes.Add(node);
        return node;
    }
}
