using Cus.Core.Hosting;

namespace Cus.Core.Tests;

public class CusOptionsTests
{
    [Fact]
    public void Defaults_UseDefaultProfileRealClipboardAndNoPort()
    {
        var options = CusOptions.Parse([]);

        Assert.Equal(CusOptions.DefaultProfile, options.Profile);
        Assert.Null(options.Port);
        Assert.Equal(ClipboardMode.Real, options.ClipboardMode);
        Assert.Empty(options.Peers);
    }

    [Fact]
    public void Parse_ReadsAllOptions_ForASecondInstanceOnTheSamePc()
    {
        var options = CusOptions.Parse(
            ["--profile", "bob", "--port", "23702", "--clipboard", "simulated", "--peer", "127.0.0.1:23700", "--peer", "pc-luca"]);

        Assert.Equal("bob", options.Profile);
        Assert.Equal(23702, options.Port);
        Assert.Equal(ClipboardMode.Simulated, options.ClipboardMode);
        Assert.Equal(
            [new PeerEndpoint("127.0.0.1", 23700), new PeerEndpoint("pc-luca", NetworkDefaults.DefaultPort)],
            options.Peers);
    }

    [Fact]
    public void Parse_ReadsNameBindAndDiscovery()
    {
        var options = CusOptions.Parse(["--name", "Portatile di Andrea", "--bind", "127.0.0.1", "--no-discovery"]);

        Assert.Equal("Portatile di Andrea", options.DisplayName);
        Assert.Equal(System.Net.IPAddress.Loopback, options.BindAddress);
        Assert.False(options.EnableDiscovery);
        Assert.True(CusOptions.Parse([]).EnableDiscovery);
    }

    [Theory]
    [InlineData("--unknown")]
    [InlineData("--profile")]
    [InlineData("--profile", "../escape")]
    [InlineData("--port", "0")]
    [InlineData("--port", "65536")]
    [InlineData("--port", "-1")]
    [InlineData("--clipboard", "fake")]
    [InlineData("--bind", "non-un-ip")]
    [InlineData("--name", "")]
    public void Parse_RejectsInvalidArguments(params string[] args)
    {
        Assert.Throws<ArgumentException>(() => CusOptions.Parse(args));
    }

    [Theory]
    [InlineData("192.168.1.10", "192.168.1.10", 23700)]
    [InlineData("192.168.1.10:23705", "192.168.1.10", 23705)]
    [InlineData("pc-luca.local", "pc-luca.local", 23700)]
    [InlineData("[::1]", "::1", 23700)]
    [InlineData("[fe80::1]:23701", "fe80::1", 23701)]
    public void PeerEndpoint_ParsesHostAndOptionalPort(string input, string host, int port)
    {
        Assert.Equal(new PeerEndpoint(host, port), PeerEndpoint.Parse(input));
    }

    [Theory]
    [InlineData("")]
    [InlineData("::1")]
    [InlineData("[::1")]
    [InlineData(":23700")]
    [InlineData("host:abc")]
    public void PeerEndpoint_RejectsInvalidInput(string input)
    {
        Assert.Throws<ArgumentException>(() => PeerEndpoint.Parse(input));
    }
}
