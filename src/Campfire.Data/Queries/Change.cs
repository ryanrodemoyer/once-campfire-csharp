namespace Campfire.Data.Queries;

// A new value for an attribute that may itself be null: `Change<string?>?` is null when the
// attribute isn't being assigned and `new(null)` when it is being set to NULL.
public readonly record struct Change<T>(T Value);
