using System.Xml.Linq;

namespace Campfire.Web.Tests;

// Guards the one-way dependency order in plans/csharp-port.md#repository-layout:
// RailsCompat <- Data <- RichText/Storage <- Cable/Jobs <- Web <- Server.
public sealed class ProjectGraphTests
{
    static readonly Dictionary<string, string[]> Layout = new()
    {
        ["Campfire.RailsCompat"] = [],
        ["Campfire.Data"] = ["Campfire.RailsCompat"],
        ["Campfire.RichText"] = ["Campfire.Data"],
        ["Campfire.Storage"] = ["Campfire.Data"],
        ["Campfire.Cable"] = ["Campfire.RichText", "Campfire.Storage"],
        ["Campfire.Jobs"] = ["Campfire.RichText", "Campfire.Storage"],
        ["Campfire.Templates"] = [],
        ["Campfire.Templates.Generator"] = [],
        ["Campfire.Web"] = ["Campfire.Cable", "Campfire.Jobs", "Campfire.Templates", "Campfire.Templates.Generator"],
        ["Campfire.Server"] = ["Campfire.Web"],
    };

    [Fact]
    public void SourceProjectsMatchTheLayout()
    {
        var graph = ReadSourceGraph();

        Assert.Equal(Layout.Keys.Order(), graph.Keys.Order());
        foreach (var (project, references) in Layout)
        {
            Assert.Equal(references.Order(), graph[project].Order());
        }
    }

    [Fact]
    public void SourceGraphHasNoCycles()
    {
        var graph = ReadSourceGraph();
        var done = new HashSet<string>();
        var visiting = new HashSet<string>();

        foreach (var project in graph.Keys)
        {
            Visit(project);
        }

        void Visit(string project)
        {
            if (done.Contains(project))
            {
                return;
            }

            Assert.True(visiting.Add(project), $"Project reference cycle through {project}");
            foreach (var reference in graph[project])
            {
                Visit(reference);
            }

            visiting.Remove(project);
            done.Add(project);
        }
    }

    [Fact]
    public void EveryLibraryHasATestProject()
    {
        var root = RepositoryRoot();
        foreach (var project in Layout.Keys)
        {
            Assert.True(File.Exists(Path.Combine(root, "tests", $"{project}.Tests", $"{project}.Tests.csproj")), $"{project} has no test project");
        }
    }

    static Dictionary<string, string[]> ReadSourceGraph()
    {
        var src = Path.Combine(RepositoryRoot(), "src");
        return Directory.GetFiles(src, "*.csproj", SearchOption.AllDirectories).ToDictionary(
            path => Path.GetFileNameWithoutExtension(path),
            path => XDocument.Load(path).Descendants("ProjectReference")
                .Select(reference => Path.GetFileNameWithoutExtension(reference.Attribute("Include")!.Value))
                .ToArray());
    }

    static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Campfire.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Campfire.slnx not found above the test output directory");
    }
}
