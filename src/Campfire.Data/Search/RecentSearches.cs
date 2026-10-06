using Campfire.Data.Queries;
using Campfire.Data.Records;
using Campfire.Data.Sqlite;

namespace Campfire.Data.Searching;

// A user's recent searches (reference/app/models/search.rb), as SearchesController records,
// lists and clears them.
public static class RecentSearches
{
    // `Current.user.searches.ordered`: newest first.
    public static List<Search> Ordered(SqliteSession session, long userId) => Searches.ForUserOrdered(session, userId);

    // `Current.user.searches.record(query)`: `find_or_create_by(query: query).touch`. Creating one
    // trims the user's searches to the ten most recent (`after_create :trim_recent_searches`).
    // Rails raises ActiveRecord::NotNullViolation for a null query (no `q` param); this throws
    // ArgumentNullException before writing anything.
    public static Search Record(WriteTransaction transaction, long userId, string query, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(query);
        var session = transaction.Session;
        var search = Searches.FindForUser(session, userId, query);
        if (search is null)
        {
            search = Searches.Create(session, userId, query, now);
            Searches.TrimRecent(session, userId);
        }
        Searches.Touch(session, search.Id, now);
        return search with { UpdatedAt = now };
    }

    // `Current.user.searches.destroy_all`
    public static void Clear(WriteTransaction transaction, long userId)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        Searches.DeleteForUser(transaction.Session, userId);
    }
}
