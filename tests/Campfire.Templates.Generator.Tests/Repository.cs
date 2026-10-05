namespace Campfire.Templates.Generator.Tests;

static class Repository
{
    public static string Root { get; } = FindRoot();

    public static string PathTo(string relative) => Path.Combine(Root, relative);

    static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Campfire.slnx")))
            {
                return dir.FullName;
            }
        }
        throw new InvalidOperationException("Campfire.slnx not found above " + AppContext.BaseDirectory);
    }
}
