using Campfire.Data.Queries;
using Campfire.Data.Records;
using Campfire.Data.Sqlite;

namespace Campfire.Data.Lifecycle;

// Boosts with their callbacks (reference/app/models/boost.rb): `belongs_to :message, touch:
// true` touches the message, and so its room, and re-indexes it after the commit.
public static class BoostLifecycle
{
    // `message.boosts.create!(content:)`, with Current.user as the booster.
    public static Boost Create(WriteTransaction transaction, Message message, long boosterId, string content, string plainTextBody, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(message);
        var boost = Boosts.Create(transaction.Session, message.Id, boosterId, content, now);
        MessageLifecycle.Touch(transaction, message, plainTextBody, now);
        return boost;
    }

    // `boost.destroy!`
    public static void Destroy(WriteTransaction transaction, Boost boost, Message message, string plainTextBody, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(boost);
        Boosts.Delete(transaction.Session, boost.Id);
        MessageLifecycle.Touch(transaction, message, plainTextBody, now);
    }
}
