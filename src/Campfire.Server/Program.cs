using Campfire.Server.Cli;

namespace Campfire.Server;

public static class Program
{
    public static Task<int> Main(string[] args) =>
        Commands.RunAsync(args, ServerSettings.FromEnvironment(), Console.Out, Console.Error);
}
