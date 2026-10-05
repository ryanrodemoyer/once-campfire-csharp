using Campfire.Data.Sqlite;

namespace Campfire.Data.Tests.Sqlite;

public class StatementCacheTests
{
    [Fact]
    public void Same_sql_reuses_the_prepared_command_with_fresh_parameters()
    {
        using var file = new TestDatabase();
        using var session = new SqliteSession(SqliteConnections.Open(file.Path), 10);

        var first = session.Command("SELECT @a + @b", ("@a", 1), ("@b", 2));
        Assert.Equal(3L, first.ExecuteScalar());
        var second = session.Command("SELECT @a + @b", ("@a", 10), ("@b", 20));

        Assert.Same(first, second);
        Assert.Equal(30L, second.ExecuteScalar());
        Assert.Equal(1, session.CachedStatementCount);
    }

    [Fact]
    public void Least_recently_used_statement_is_evicted_at_capacity()
    {
        using var file = new TestDatabase();
        using var session = new SqliteSession(SqliteConnections.Open(file.Path), 2);

        var one = session.Command("SELECT 1");
        var two = session.Command("SELECT 2");
        Assert.Same(one, session.Command("SELECT 1"));
        session.Command("SELECT 3");

        Assert.Equal(2, session.CachedStatementCount);
        Assert.Same(one, session.Command("SELECT 1"));
        Assert.NotSame(two, session.Command("SELECT 2"));
    }

    [Fact]
    public void Null_parameters_bind_as_sql_null()
    {
        using var file = new TestDatabase();
        using var session = new SqliteSession(SqliteConnections.Open(file.Path), 10);

        Assert.Equal(1, session.Scalar<long>("SELECT @value IS NULL", ("@value", null)));
    }
}
