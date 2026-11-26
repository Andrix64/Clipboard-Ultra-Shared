using Cus.App.Shared;

namespace Cus.App.Windows;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args) => AppHost.Run(args, new WindowsPlatform());
}
