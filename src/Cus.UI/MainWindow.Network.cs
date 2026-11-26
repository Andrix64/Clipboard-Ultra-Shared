using System.Text;
using Avalonia.Controls;
using Cus.Core.Friends;
using Cus.Core.Node;

namespace Cus.UI;

public partial class MainWindow : Window
{
    // ================================================================ rete

    private void WireNetworkPage(CusNode node)
    {
        OfferButton.Click += async (_, _) => await RunFrom(OfferButton, () => node.SetVisibleAsync(true));
        JoinAddressButton.Click += async (_, _) =>
        {
            if (ParseAddress(AddressInput.Text) is { } endpoint)
                await RunFrom(JoinAddressButton, () => Join(node, endpoint.Host, endpoint.Port, expectedDeviceId: null));
        };
        AddPeerButton.Click += (_, _) =>
        {
            if (ParseAddress(AddressInput.Text) is { } endpoint)
            {
                node.AddManualPeer(endpoint.ToString());
                AddressInput.Text = "";
            }
        };
    }

    private async Task Join(CusNode node, string host, int port, string? expectedDeviceId)
    {
        var result = await node.JoinAsync(host, port, expectedDeviceId);
        if (result.Outcome == JoinOutcome.Rejected)
            _model.Notifier?.Info(Loc.T("main.didNotLetYouIn", result.PeerName) + (result.Reason is null ? "." : $": {result.Reason}."));
    }

    private string _radarSignature = "";

    /// <summary>Radar: un pallino per ogni host visto in rete e uno, con la propria iniziale, per ogni persona collegata.</summary>
    private void RefreshRadar(CusNode node, IReadOnlyList<PeerView> peers)
    {
        var blips = new List<RadarBlip>();
        var seen = new HashSet<string>();
        if (node.Session is { } session)
        {
            foreach (var p in session.Participants.Where(p => !p.IsSelf))
                if (seen.Add(p.DeviceId))
                    blips.Add(new RadarBlip(p.DeviceId, Initial(p.DisplayName), Connected: true));
        }

        foreach (var peer in peers.Where(p => p.IsAnnounced))
            if (seen.Add(peer.DeviceId ?? peer.Address))
                blips.Add(new RadarBlip(peer.DeviceId ?? peer.Address, "", Connected: false));

        var signature = string.Join("|", blips.Select(b => $"{b.Key}:{b.Label}:{b.Connected}"));
        if (signature == _radarSignature)
            return;
        _radarSignature = signature;
        LcdRadar.Blips = blips;
    }

    private static string Initial(string name)
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0)
            return "?";
        // Il primo code point, non il primo char: un'emoji iniziale è una coppia surrogata.
        var first = trimmed.EnumerateRunes().First();
        return Rune.ToUpperInvariant(first).ToString();
    }

    private void RefreshPeers()
    {
        if (_model.Node is not { } node)
            return;
        var peers = node.Peers;
        RefreshRadar(node, peers);
        var signature =string.Join("\n", peers.Select(p => $"{p.DeviceId}|{p.DisplayName}|{p.Address}|{p.Participants}|{p.IsAnnounced}|{p.Agreement}|{p.IsManual}|{p.LooksLikeFriend}"));
        if (signature == _peersSignature)
            return;
        _peersSignature = signature;

        PeersPanel.Children.Clear();
        if (peers.Count == 0)
        {
            PeersPanel.Children.Add(Hint(Loc.T("main.noHostsOnTheNetworkWhoever")));
            return;
        }

        foreach (var peer in peers)
        {
            var who = peer.IsManual ? peer.Address : $"{peer.DisplayName}{(peer.UserName.Length > 0 ? $" ({peer.UserName})" : "")}";
            var detail = peer.IsAnnounced
                ? (peer.Participants <= 1
                    ? Loc.T("main.freeOfferingAsHost")
                    : Loc.T("main.peopleInSession", peer.Participants))
                : peer.IsManual ? Loc.T("main.savedAddress") : Loc.T("main.friendNotVisibleNow");
            var badges = (peer.LooksLikeFriend is not null ? $"{Icons.Warning} " : "")
                + (peer.Agreement switch { AgreementLevel.Anytime => $"{Icons.Infinity} ", AgreementLevel.AutoJoin => $"{Icons.SignIn} ", _ => "" })
                + (peer.DeviceId is { } id && node.Friends.TrustsWithSensitive(id) ? $"{Icons.Key} " : "");

            // Gli annunci non sono autenticati: per chi non è un amico conta l'impronta (verificata alla
            // connessione), non il nome, e un nome uguale a quello di un amico si segnala.
            if (peer.LooksLikeFriend is { } imitated)
                detail = Loc.T("main.notYourFriendSameNameDifferent", imitated);
            if (peer.Agreement == AgreementLevel.None && peer.Fingerprint is { } fingerprint)
                detail += Loc.T("main.fingerprintSuffix", fingerprint);

            var buttons = new List<Control>();
            if (peer.CanJoin)
            {
                var join = RowButton(Loc.T("common.join"), peer.Agreement == AgreementLevel.None
                    ? Loc.T("main.askToJoinTheHostDecides")
                    : Loc.T("main.joinWithoutConfirmationPact"));
                join.Click += async (_, _) => await RunFrom(join, async () =>
                {
                    if (peer.LooksLikeFriend is { } friendName && !await ConfirmDialog.ShowAsync(this, Loc.T("main.notYourFriend"),
                            Loc.T("main.hasTheSameNameAsYour", peer.DisplayName, friendName, peer.Fingerprint),
                            Loc.T("main.joinAnyway")))
                        return;
                    await Join(node, peer.Host, peer.Port, peer.DeviceId);
                });
                buttons.Add(join);
            }

            if (peer.IsManual)
            {
                var remove = RowButton(Icons.Xmark, Loc.T("main.removeFromTheList"));
                remove.Click += (_, _) => node.RemoveManualPeer(peer.Address);
                buttons.Add(remove);
            }

            PeersPanel.Children.Add(Row($"{badges}{who}", $"{detail} · {peer.Address}", highlight: false, buttons));
        }
    }
}
