using System.Runtime.Versioning;
using Cus.App.Shared;

[assembly: SupportedOSPlatform("linux")]

namespace Cus.App.Linux;

internal static class Program
{
    public static int Main(string[] args) => AppHost.Run(args, new LinuxPlatform());
}
