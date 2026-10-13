using Campfire.Server.Cli;

namespace Campfire.Server;

public static class Program
{
    public static Task<int> Main(string[] args)
    {
        Environment.SetEnvironmentVariable("DOTNET_HOSTBUILDER__RELOADCONFIGONCHANGE", "false");
        Environment.SetEnvironmentVariable("DOTNET_USE_POLLING_FILE_WATCHER", "true");
        return Commands.RunAsync(args, ServerSettings.FromEnvironment(), Console.Out, Console.Error);
    }
}
