using Avalonia.Controls;
using Cus.Core.Abstractions;
using Cus.Core.Friends;
using Cus.Core.Node;

namespace Cus.UI;

public partial class MainWindow : Window
{
    // ================================================================ sessione

    private void WireSessionPage(CusNode node)
    {
        VisibleToggle.Click += async (_, _) => await RunFrom(VisibleToggle, () => node.SetVisibleAsync(!node.IsVisible));
        SensitiveToggle.Click += async (_, _) =>
            await RunFrom(SensitiveToggle, () => node.SetSensitiveForAllAsync(node.Session?.SensitiveForAll != true));
        TransfersButton.Click += (_, _) => TransfersWindow.ShowFor(node);
        LeaveButton.Click += async (_, _) => await RunFrom(LeaveButton, node.LeaveSessionAsync);
    }

    private void RefreshSession()
    {
        if (_model.Node is not { } node)
            return;

        var session = node.Session;
        if (session is null)
        {
            LcdState.Text = Loc.T("common.waiting");
            LcdParticipants.Text = Loc.T("main.offerAsHostOrJoinNetwork");
        }
        else
        {
            LcdState.Text = session.Role == SessionRole.Host
                ? (session.IsSharing ? Loc.T("main.sharedHost") : Loc.T("main.visible"))
                : session.MyDirection == ShareDirection.ReadOnly ? Loc.T("main.readOnly") : Loc.T("main.shared");
            Skin.SetRich(LcdParticipants, session.IsSharing
                ? string.Join("  ", session.Participants.Select(p => $"{AvaloniaOverlayIndicator.Glyph(p).Glyph} {p.DisplayName}{(p.IsSelf ? Icons.CaretLeft : "")}"))
                : session.IsVisible
                    ? Loc.T("main.waitingForSomeoneToAskTo")
                    : Loc.T("main.nobodyElseInTheSession"));
        }

        RefreshRadar(node, node.Peers);
        var host = session?.Role == SessionRole.Host;
        PageNetworkButton.Content = session is null ? Loc.T("common.network") : Loc.T("main.session");
        VisibleToggle.IsVisible = host;
        VisibleToggle.Classes.Set("active", session?.IsVisible == true);
        Skin.SetContent(VisibleToggle, session?.IsVisible == true
            ? Loc.T("main.visibleYesIcon", Icons.Eye)
            : Loc.T("main.visibleNo", Icons.EyeSlash));
        SensitiveToggle.IsVisible = host && session!.IsSharing;
        SensitiveToggle.Classes.Set("active", session?.SensitiveForAll == true);
        Skin.SetContent(LeaveButton, host ? Loc.T("main.endSessionIcon", Icons.Stop) : Loc.T("main.leave", Icons.Stop));
        RequestsTitle.IsVisible = host;

        if (!FriendsPageShown)
            ShowNetworkOrSession();
        RefreshRequests();
        RefreshParticipants();
        RefreshTransfersButton();
    }

    private void RefreshRequests()
    {
        if (_model.Node is not { } node)
            return;
        RequestsPanel.Children.Clear();
        if (node.Session?.Role != SessionRole.Host)
            return;

        var requests = node.JoinRequests;
        if (requests.Count == 0)
        {
            RequestsPanel.Children.Add(Hint(node.IsVisible
                ? Loc.T("main.noPendingRequests")
                : Loc.T("main.youAreHiddenNobodyCanAsk")));
            return;
        }

        foreach (var request in requests)
        {
            var bidirectional = RowButton(Icons.Bidirectional, Loc.T("main.letInWithTwoWayAccess"));
            var readOnly = RowButton(Icons.ArrowDown, Loc.T("main.letInReadOnly"));
            var reject = RowButton(Icons.Xmark, Loc.T("main.reject"));
            bidirectional.Click += (_, _) => node.AnswerJoinRequest(request.RequestId, ConsentDecision.Accept);
            readOnly.Click += (_, _) => node.AnswerJoinRequest(request.RequestId, ConsentDecision.AcceptReadOnly);
            reject.Click += (_, _) => node.AnswerJoinRequest(request.RequestId, ConsentDecision.Reject);
            RequestsPanel.Children.Add(Row(
                $"{(request.LooksLikeFriend is null ? "?" : Icons.Warning)} {request.DisplayName}{(request.UserName.Length > 0 ? $" ({request.UserName})" : "")}",
                request.LooksLikeFriend is { } imitated
                    ? Loc.T("main.notYourFriendWithFingerprint", imitated, request.Fingerprint)
                    : Loc.T("main.fingerprintLower", request.Fingerprint),
                highlight: false,
                [bidirectional, readOnly, reject]));
        }
    }

    private void RefreshParticipants()
    {
        if (_model.Node is not { } node)
            return;
        ParticipantsPanel.Children.Clear();
        if (node.Session is not { } session)
            return;

        var host = session.Role == SessionRole.Host;
        foreach (var participant in session.Participants)
        {
            var (glyph, role) = AvaloniaOverlayIndicator.Glyph(participant);
            var buttons = new List<Control>();
            var friend = node.Friends.Get(participant.DeviceId);

            if (participant.IsSelf && !host && session.MyGranted == ShareDirection.Bidirectional)
            {
                // Il membro con accesso bidirezionale passa a sola lettura (e torna) con un tasto.
                var readOnly = session.MyDirection == ShareDirection.ReadOnly;
                var toggle = RowButton(readOnly ? Icons.ArrowDown : Icons.Bidirectional, readOnly
                    ? Loc.T("main.youAreReadOnlyGoBack")
                    : Loc.T("main.switchToReadOnlyYourCopies"));
                toggle.Click += async (_, _) => await RunFrom(toggle, () => node.SetMyDirectionAsync(readOnly ? ShareDirection.Bidirectional : ShareDirection.ReadOnly));
                buttons.Add(toggle);
            }

            if (!participant.IsSelf)
            {
                if (host)
                {
                    // Un pulsante per membro che alterna bidirezionale e sola lettura.
                    var granted = participant.Granted;
                    var grant = RowButton(granted == ShareDirection.ReadOnly ? Icons.ArrowDown : Icons.Bidirectional,
                        granted == ShareDirection.ReadOnly
                            ? Loc.T("main.readOnlyClickToGiveTwo")
                            : Loc.T("main.twoWayClickToMakeRead"));
                    grant.Click += async (_, _) => await RunFrom(grant, () => node.SetMemberGrantAsync(participant.DeviceId,
                        granted == ShareDirection.ReadOnly ? ShareDirection.Bidirectional : ShareDirection.ReadOnly));
                    buttons.Add(grant);
                }

                var trusted = friend?.TrustedWithSensitive == true;
                var trust = RowButton(Icons.Key, trusted
                    ? Loc.T("main.trustedSeesThePasswordsYouCopy")
                    : Loc.T("main.clickToAlsoShowThemThe"));
                trust.Classes.Add("toggle");
                trust.Classes.Set("lit", trusted);
                trust.Click += (_, _) => node.SetSensitiveTrust(participant.DeviceId, participant.DisplayName, !trusted);
                buttons.Add(trust);

                if (host)
                {
                    var agreement = friend?.Agreement ?? AgreementLevel.None;
                    var pact = RowButton(agreement switch { AgreementLevel.Anytime => Icons.Infinity, AgreementLevel.AutoJoin => Icons.SignIn, _ => Loc.T("main.pact") },
                        Loc.T("main.proposeAMutualPactTheyMust"));
                    pact.Classes.Add("toggle");
                    pact.Classes.Set("lit", agreement != AgreementLevel.None);
                    pact.Flyout = PactMenu(node, participant.DeviceId, agreement);
                    buttons.Add(pact);
                }
            }

            var detail = participant.IsSelf ? Loc.T("common.you", role) : role;
            if (participant.IsHost && !participant.IsSelf && friend?.TrustedWithSensitive != true)
                detail += Loc.T("main.yourPasswordsDonTReachThem");
            ParticipantsPanel.Children.Add(Row($"{glyph} {participant.DisplayName}", detail, participant.IsSelf, buttons));
        }
    }

    private MenuFlyout PactMenu(CusNode node, string deviceId, AgreementLevel current)
    {
        var flyout = new MenuFlyout();
        void Add(string header, AgreementLevel level)
        {
            var item = new MenuItem { IsEnabled = level != current };
            Skin.SetHeader(item, header);
            item.Click += async (_, _) => await Run(async () =>
            {
                if (!await node.ProposeAgreementAsync(deviceId, level))
                    _model.Notifier?.Info(Loc.T("main.pactRejected"));
            });
            flyout.Items.Add(item);
        }

        Add(Loc.T("main.joinsWithoutConfirmationWhenYouOffer", Icons.SignIn), AgreementLevel.AutoJoin);
        Add(Loc.T("main.connectionAtAnyTime", Icons.Infinity), AgreementLevel.Anytime);
        if (current != AgreementLevel.None)
        {
            var revoke = new MenuItem();
            Skin.SetHeader(revoke, Loc.T("main.dissolveThePact", Icons.Xmark));
            revoke.Click += async (_, _) => await Run(() => node.RevokeAgreementAsync(deviceId));
            flyout.Items.Add(revoke);
        }

        return flyout;
    }

    private void RefreshTransfersButton()
    {
        if (_model.Node is not { } node)
            return;
        var transfers = node.Transfers;
        var active = transfers.Count(t => t.State == TransferState.Active);
        TransfersButton.IsVisible = transfers.Count > 0;
        Skin.SetContent(TransfersButton, active > 0
            ? Loc.T("main.transfersIconCount", Icons.Transfers, active)
            : Loc.T("main.transfersIcon", Icons.Transfers));
    }

    private void RefreshCurrent()
    {
        var current = _model.Node?.CurrentClipboard;
        if (current is null)
        {
            LcdCurrent.Text = "";
            LcdCurrentSize.Text = "";
            _currentFull = "";
            return;
        }

        var self = current.OriginDeviceId == _model.Node?.Identity.DeviceId;
        var title = current.Kind == ClipboardContentKind.Files && !current.IsSensitive
            ? DisplayText.MiddleEllipsis(DisplayText.Sanitize(current.Title, 260, ""), 26)
            : current.Title;
        string Line(string shownTitle) =>
            $"{(self ? Loc.T("main.you") : DisplayText.Sanitize(current.OriginName, 32))}: {(current.IsSensitive ? "••• " : "")}{shownTitle}{(current.IsShared ? "" : Loc.T("main.hereOnly"))}";
        LcdCurrent.Text = Line(title);
        _currentFull = current.IsSensitive ? LcdCurrent.Text : Line(DisplayText.Sanitize(current.Title, 4000, ""));
        LcdCurrentSize.Text = current.SizeBytes is { } size ? DisplayText.FormatSize(size) : "";
    }
}
