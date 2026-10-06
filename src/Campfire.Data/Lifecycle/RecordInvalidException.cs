namespace Campfire.Data.Lifecycle;

// `ActiveRecord::RecordInvalid`: a `create!` or `update!` whose validations failed. Thrown inside
// a write, it rolls the transaction back.
public sealed class RecordInvalidException : Exception
{
    public RecordInvalidException()
    {
    }

    public RecordInvalidException(string message)
        : base(message)
    {
    }

    public RecordInvalidException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
