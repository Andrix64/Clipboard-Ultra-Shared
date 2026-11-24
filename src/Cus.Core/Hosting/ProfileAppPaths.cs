using Cus.Core.Abstractions;

namespace Cus.Core.Hosting;

/// <summary>
/// Percorsi per profilo. Il profilo di default usa <c>&lt;LocalApplicationData&gt;/CUS</c>;
/// un profilo con nome usa <c>&lt;LocalApplicationData&gt;/CUS/profiles/&lt;nome&gt;</c>.
/// Profili diversi hanno DB, identità e chiavi separati: così più istanze possono girare
/// sullo stesso PC come se fossero dispositivi diversi.
/// </summary>
/// <remarks>
/// Si usa <see cref="Environment.SpecialFolder.LocalApplicationData"/> e non un percorso Windows
/// scritto a mano: su Linux diventa <c>~/.local/share</c> (o <c>$XDG_DATA_HOME</c>).
/// </remarks>
public sealed class ProfileAppPaths : IAppPaths
{
    public const string DatabaseFileName = "cus.db";

    public ProfileAppPaths(string profile, string? baseDirectory = null)
    {
        CusOptions.ValidateProfileName(profile);

        var root = baseDirectory
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CUS");

        DataDirectory = profile == CusOptions.DefaultProfile
            ? root
            : Path.Combine(root, "profiles", profile);
    }

    public string DataDirectory { get; }

    public string DatabasePath => Path.Combine(DataDirectory, DatabaseFileName);
}
