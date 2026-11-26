using Cus.Core.Abstractions;
using Cus.Core.Friends;
using Cus.Core.Net;

namespace Cus.Core.Node;

public sealed partial class CusNode
{
    private int _agreementPending;

    /// <summary>
    /// Host: propone un patto a un membro della sessione. Il membro deve accettarlo; da quel momento
    /// vale nei due sensi.
    /// </summary>
    /// <returns>True se il membro ha accettato.</returns>
    public async Task<bool> ProposeAgreementAsync(string deviceId, AgreementLevel level)
    {
        if (level is not (AgreementLevel.AutoJoin or AgreementLevel.Anytime))
            throw new ArgumentOutOfRangeException(nameof(level));
        if (_hostSession?.Members.TryGetValue(deviceId, out var member) != true)
            throw new CusOperationException(Loc.T("common.theDeviceIsNoLongerIn"));

        var connection = member!.Connection;
        var name = connection.Remote.DisplayName;
        Log(Loc.T("friends.pactProposedToWaitingForThe", DescribeAgreement(level), name));
        AgreementReplyMessage reply;
        try
        {
            reply = await connection.RequestAsync<AgreementReplyMessage>(
                id => new AgreementProposalMessage(id, level), _options.ConsentTimeout + TimeSpan.FromSeconds(15));
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException)
        {
            throw new CusOperationException(Loc.T("friends.didnTAnswerThePactProposal", name), ex);
        }

        if (!reply.Accepted)
        {
            Log(Loc.T("friends.declinedThePact", name));
            return false;
        }

        // Prima lo salva chi accetta, poi chi propone: se la conferma si perde, alla prossima
        // connessione i due livelli si allineano al più basso.
        Friends.SetAgreement(deviceId, name, level);
        RememberEndpoint(connection);
        Log(Loc.T("friends.pactWith", name, DescribeAgreement(level)));
        return true;
    }

    /// <summary>Membro: risponde a una proposta di patto dell'host, dopo averlo chiesto all'utente.</summary>
    private async Task HandleAgreementProposalAsync(PeerConnection connection, AgreementProposalMessage proposal)
    {
        var accepted = false;
        var remote = connection.Remote;
        if (Enum.IsDefined(proposal.Level) && proposal.Level != AgreementLevel.None
            && _memberSession?.Host == connection
            && Interlocked.CompareExchange(ref _agreementPending, 1, 0) == 0)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, connection.Lifetime);
                timeout.CancelAfter(_options.ConsentTimeout);
                accepted = await _consent.AskAgreementAsync(new AgreementRequest(remote.DisplayName, remote.Fingerprint, proposal.Level), timeout.Token);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"CUS: proposta di patto non mostrata: {ex}");
            }
            finally
            {
                Interlocked.Exchange(ref _agreementPending, 0);
            }
        }

        if (accepted && !connection.IsClosed)
        {
            Friends.SetAgreement(remote.DeviceId, remote.DisplayName, proposal.Level);
            RememberEndpoint(connection);
            Log(Loc.T("friends.pactWith", remote.DisplayName, DescribeAgreement(proposal.Level)));
        }

        try
        {
            await connection.SendAsync(new AgreementReplyMessage(proposal.RequestId, accepted && !connection.IsClosed));
        }
        catch (IOException)
        {
            // Host caduto: alla prossima connessione i livelli si allineano.
        }
    }

    /// <summary>
    /// Scioglie il patto con un amico. Cade per entrambi: lo si comunica subito se c'è una connessione
    /// o se l'ultimo indirizzo risponde, altrimenti alla prossima connessione tra i due.
    /// </summary>
    public async Task RevokeAgreementAsync(string deviceId)
    {
        if (Friends.Get(deviceId) is not { Agreement: not AgreementLevel.None } friend)
            return;
        Friends.SetAgreement(deviceId, friend.DisplayName, AgreementLevel.None);
        Log(Loc.T("friends.pactWithDissolved", friend.DisplayName));

        var live = _connections.Keys.FirstOrDefault(c => c.IsStarted && !c.IsClosed && c.Remote.DeviceId == deviceId);
        if (live is not null)
        {
            try
            {
                await live.SendAsync(new FriendSyncMessage(AgreementLevel.None));
                return;
            }
            catch (IOException)
            {
                // Si prova all'ultimo indirizzo.
            }
        }

        if (friend.LastEndpoint is { } endpoint)
        {
            RunInBackground(async () =>
            {
                try
                {
                    // Lo scambio iniziale porta il livello (ora nessuno): l'altro PC lo abbassa da sé.
                    var connection = await ConnectAsync(endpoint.Host, endpoint.Port, _lifetime.Token);
                    await connection.CloseAsync(WireTexts.PactDissolved.Text);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Trace.WriteLine($"CUS: {friend.DisplayName} non raggiungibile per sciogliere il patto: {ex.Message}");
                }
            });
        }
    }

    /// <summary>Marca un dispositivo come affidabile (vede i contenuti sensibili copiati qui) o lo toglie. Solo da questo lato.</summary>
    public void SetSensitiveTrust(string deviceId, string displayName, bool trusted)
    {
        Friends.SetSensitiveTrust(deviceId, DisplayText.Sanitize(displayName, 64), trusted);
        Log(trusted
            ? Loc.T("friends.willAlsoSeeTheSensitiveContent", displayName)
            : Loc.T("friends.willNoLongerSeeTheSensitive", displayName));
    }

    /// <summary>Toglie un amico: scioglie il patto (anche dall'altra parte) e la fiducia.</summary>
    public async Task RemoveFriendAsync(string deviceId)
    {
        await RevokeAgreementAsync(deviceId);
        Friends.Remove(deviceId);
    }

    /// <summary>Allinea il patto al livello dell'altro PC (mai verso l'alto).</summary>
    private void ApplyFriendSync(PeerConnection connection, AgreementLevel remote)
    {
        var before = Friends.AgreementWith(connection.Remote.DeviceId);
        if (!Friends.LowerAgreement(connection.Remote.DeviceId, remote))
            return;
        var name = connection.Remote.DisplayName;
        var now = Friends.AgreementWith(connection.Remote.DeviceId);
        var message = now == AgreementLevel.None
            ? Loc.T("friends.dissolvedThePact", name, DescribeAgreement(before))
            : Loc.T("friends.reducedThePactTo", name, DescribeAgreement(now));
        Log(message);
        _notifier.Info(message);
    }

    public static string DescribeAgreement(AgreementLevel level) => level switch
    {
        AgreementLevel.AutoJoin => Loc.T("friends.joinsWithoutConfirmation"),
        AgreementLevel.Anytime => Loc.T("friends.connectAtAnyTime"),
        _ => Loc.T("friends.none"),
    };
}
