namespace Campfire.Web.Tests.ResponseCache;

/// <summary>
/// <c>budget</c> in <c>reference/app/models/response_cache.rb</c>: <c>ENV.fetch("CAMPFIRE_RESPONSE_CACHE_MB", 64).to_i</c> mebibytes, at least 0.
/// </summary>
public sealed class ResponseCacheBudgetTests
{
    const long mebibyte = 1024 * 1024;

    [Theory]
    [InlineData(null, 64)]
    [InlineData("", 0)]
    [InlineData("0", 0)]
    [InlineData("16", 16)]
    [InlineData("+7", 7)]
    [InlineData("12abc", 12)]
    [InlineData("abc", 0)]
    [InlineData("-5", 0)]
    public void The_variable_is_read_as_Ruby_reads_a_to_i(string? raw, long mebibytes) =>
        Assert.Equal(mebibytes * mebibyte, Campfire.Web.ResponseCache.ResponseCache.ParseBudget(raw));

    [Fact]
    public void The_override_replaces_the_variable_and_is_never_negative()
    {
        var cache = new Campfire.Web.ResponseCache.ResponseCache(() => "unused.sqlite3") { BudgetOverride = 4096 };
        Assert.Equal(4096, cache.Budget);

        cache.BudgetOverride = -1;
        Assert.Equal(0, cache.Budget);

        // No override: what the process was started with.
        cache.BudgetOverride = null;
        Assert.Equal(
            Campfire.Web.ResponseCache.ResponseCache.ParseBudget(Environment.GetEnvironmentVariable("CAMPFIRE_RESPONSE_CACHE_MB")),
            cache.Budget);
    }
}
