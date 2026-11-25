using Cus.Core.Abstractions;
using Cus.Platform.Simulated;

namespace Cus.Core.Tests;

public class SimulatedPlatformTests
{
    [Fact]
    public void Copy_NotifiesMonitor_OnlyAfterStart()
    {
        using var clipboard = new SimulatedClipboard();
        var received = new List<ClipboardContent>();
        clipboard.Changed += (_, e) => received.Add(e.Content);

        clipboard.Copy(ClipboardContent.FromText("prima di Start"));
        clipboard.Start();
        clipboard.Copy(ClipboardContent.FromText("dopo Start"));

        Assert.Equal(["dopo Start"], received.Select(c => c.Text));
        Assert.Equal("dopo Start", clipboard.Current?.Text);
    }

    [Fact]
    public async Task Write_AlsoNotifiesMonitor_LikeARealClipboard()
    {
        using var clipboard = new SimulatedClipboard();
        clipboard.Start();
        ClipboardContent? received = null;
        clipboard.Changed += (_, e) => received = e.Content;

        await clipboard.WriteAsync(ClipboardContent.FromText("dal peer"));

        Assert.Equal("dal peer", received?.Text);
    }

    [Fact]
    public void TwoInstances_DoNotShareContent()
    {
        using var alice = new SimulatedClipboard();
        using var bob = new SimulatedClipboard();

        alice.Copy(ClipboardContent.FromText("solo di alice"));

        Assert.Null(bob.Current);
    }

    [Fact]
    public void OverlayIndicator_RecordsStateAndClick()
    {
        using var overlay = new RecordingOverlayIndicator();
        var (leaveRequested, transfersRequested, directionRequested) = (false, false, false);
        overlay.LeaveRequested += (_, _) => leaveRequested = true;
        overlay.TransfersRequested += (_, _) => transfersRequested = true;
        overlay.DirectionToggleRequested += (_, _) => directionRequested = true;

        overlay.Show(new SessionIndicatorState(
            [new SessionParticipant("ID", "Andrea", ShareDirection.Bidirectional, ShareDirection.Bidirectional, IsHost: true, IsSelf: true)]));
        Assert.True(overlay.IsVisible);

        overlay.ClickTransfers();
        overlay.ClickDirection();
        overlay.Click();
        overlay.Hide();

        Assert.True(leaveRequested);
        Assert.True(transfersRequested);
        Assert.True(directionRequested);
        Assert.False(overlay.IsVisible);
    }

    [Fact]
    public void InMemorySecretStore_ReturnsCopies()
    {
        var store = new InMemorySecretStore();
        var secret = new byte[] { 1, 2, 3 };

        store.Save("db-key", secret);
        secret[0] = 9;

        Assert.Equal(new byte[] { 1, 2, 3 }, store.Load("db-key"));
        Assert.Null(store.Load("missing"));
    }
}
