using Cus.Core.Hosting;

namespace Cus.Core.Tests;

public class ProfileAppPathsTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "cus-tests-root");

    [Fact]
    public void DefaultProfile_UsesRootDirectory()
    {
        var paths = new ProfileAppPaths(CusOptions.DefaultProfile, Root);

        Assert.Equal(Root, paths.DataDirectory);
        Assert.Equal(Path.Combine(Root, "cus.db"), paths.DatabasePath);
    }

    [Fact]
    public void NamedProfiles_HaveSeparateDirectories()
    {
        var alice = new ProfileAppPaths("alice", Root);
        var bob = new ProfileAppPaths("bob", Root);

        Assert.Equal(Path.Combine(Root, "profiles", "alice"), alice.DataDirectory);
        Assert.NotEqual(alice.DatabasePath, bob.DatabasePath);
    }

    [Fact]
    public void WithoutBaseDirectory_UsesLocalApplicationData()
    {
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CUS");

        Assert.Equal(expected, new ProfileAppPaths(CusOptions.DefaultProfile).DataDirectory);
    }

    [Fact]
    public void InvalidProfileName_IsRejected()
    {
        Assert.Throws<ArgumentException>(() => new ProfileAppPaths("..", Root));
    }
}
