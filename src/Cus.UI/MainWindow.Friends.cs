using Avalonia.Controls;
using Cus.Core.Friends;

namespace Cus.UI;

public partial class MainWindow : Window
{
    // ================================================================ amici

    private void RefreshFriends()
    {
        if (_model.Node is not { } node)
            return;
        FriendsPanel.Children.Clear();
        var friends = node.Friends.All;
        if (friends.Count == 0)
        {
            FriendsPanel.Children.Add(Hint(Loc.T("main.noFriendsInASessionThe", Icons.Key)));
            return;
        }

        foreach (var friend in friends)
        {
            var buttons = new List<Control>();
            var trust = RowButton(Icons.Key, friend.TrustedWithSensitive
                ? Loc.T("main.seesYourPasswordsClickToRemove")
                : Loc.T("main.clickToShowThemYourPasswords"));
            trust.Classes.Add("toggle");
                trust.Classes.Set("lit", friend.TrustedWithSensitive);
            trust.Click += (_, _) => node.SetSensitiveTrust(friend.DeviceId, friend.DisplayName, !friend.TrustedWithSensitive);
            buttons.Add(trust);

            if (friend.Agreement != AgreementLevel.None)
            {
                var revoke = RowButton(Loc.T("main.dissolve"), Loc.T("main.dissolvesThePactForBoth"));
                revoke.Click += async (_, _) => await RunFrom(revoke, async () =>
                {
                    if (await ConfirmDialog.ShowAsync(this, Loc.T("main.dissolveThePactTitle"),
                            Loc.T("main.dissolveThePactWithItEnds", friend.DisplayName),
                            Loc.T("main.dissolve")))
                        await node.RevokeAgreementAsync(friend.DeviceId);
                });
                buttons.Add(revoke);
            }

            var remove = RowButton(Icons.Xmark, Loc.T("main.removeFromFriendsPactAndTrust"));
            remove.Click += async (_, _) => await RunFrom(remove, () => node.RemoveFriendAsync(friend.DeviceId));
            buttons.Add(remove);

            var agreement = friend.Agreement switch
            {
                AgreementLevel.Anytime => Loc.T("main.connectsWheneverTheyWant", Icons.Infinity),
                AgreementLevel.AutoJoin => Loc.T("main.joinsWithoutConfirmation", Icons.SignIn),
                _ => Loc.T("main.noPact"),
            };
            var where = friend.LastEndpoint is { } endpoint ? $" · {endpoint}" : "";
            FriendsPanel.Children.Add(Row(friend.DisplayName, $"{agreement}{(friend.TrustedWithSensitive ? Loc.T("main.seesPasswords", Icons.Key) : "")}{where}", false, buttons,
                tip: Loc.T("main.fingerprintTip", friend.Fingerprint)));
        }
    }
}
