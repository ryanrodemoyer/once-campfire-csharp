using Campfire.Server.Cli;
using Microsoft.Data.Sqlite;

namespace Campfire.Server.Tests.Cli;

public sealed class CommandsTests : IDisposable
{
    readonly TestRoot root = new();
    readonly StringWriter output = new();
    readonly StringWriter error = new();

    public void Dispose()
    {
        output.Dispose();
        error.Dispose();
        root.Dispose();
    }

    Task<int> Run(ServerSettings settings, params string[] args) => Commands.RunAsync(args, settings, output, error);

    [Fact]
    public async Task Db_prepare_creates_the_database_under_storage()
    {
        Assert.Equal(0, await Run(root.Settings(), "db:prepare"));

        var database = System.IO.Path.Combine(root.Path, "storage", "db", "production.sqlite3");
        // Checked before any other connection opens the file and checkpoints it.
        Assert.False(File.Exists(database + "-wal"), "the WAL is checkpointed on exit");
        Assert.Equal(15, Count(database, "SELECT COUNT(*) FROM schema_migrations"));
        Assert.Equal(0, Count(database, "SELECT COUNT(*) FROM users"));
        Assert.Equal("", error.ToString());
    }

    [Fact]
    public async Task Db_prepare_leaves_the_seed_alone()
    {
        var database = root.CopySeed();
        var messages = Count(database, "SELECT COUNT(*) FROM messages");

        Assert.Equal(0, await Run(root.Settings(), "db:prepare"));

        Assert.True(messages > 0);
        Assert.Equal(messages, Count(database, "SELECT COUNT(*) FROM messages"));
    }

    [Fact]
    public async Task Db_prepare_refuses_a_database_with_missing_migrations()
    {
        var database = root.CopySeed();
        Execute(database, "DELETE FROM schema_migrations WHERE version = (SELECT MAX(version) FROM schema_migrations)");

        Assert.Equal(1, await Run(root.Settings(), "db:prepare"));

        Assert.StartsWith("campfire db:prepare: The database is missing migrations ", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Server_refuses_to_start_without_a_secret_key_base()
    {
        Assert.Equal(1, await Run(root.Settings()));

        Assert.Equal(
            "campfire server: Missing `secret_key_base` for 'production' environment, set this string with `bin/rails credentials:edit`",
            error.ToString().TrimEnd());
    }

    [Fact]
    public async Task Server_needs_the_built_assets()
    {
        Assert.Equal(1, await Run(root.Settings(("SECRET_KEY_BASE", "secret"))));

        Assert.Equal(
            $"campfire server: No assets in {System.IO.Path.Combine(root.Path, "artifacts", "assets")}: run bin/build-assets, or set CAMPFIRE_ASSETS_PATH",
            error.ToString().TrimEnd());
    }

    [Theory]
    [InlineData("console")]
    [InlineData("db:prepare", "extra")]
    [InlineData("backup", "extra")]
    public async Task Unknown_commands_print_the_usage(params string[] args)
    {
        Assert.Equal(64, await Run(root.Settings(), args));

        Assert.Equal(Commands.Usage, error.ToString().TrimEnd());
    }

    [Fact]
    public async Task Help_prints_the_usage()
    {
        Assert.Equal(0, await Run(root.Settings(), "--help"));

        Assert.Equal(Commands.Usage, output.ToString().TrimEnd());
    }

    static long Count(string database, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={database};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)command.ExecuteScalar()!;
    }

    static void Execute(string database, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={database};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
