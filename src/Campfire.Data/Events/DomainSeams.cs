namespace Campfire.Data.Events;

// The three ways a domain change reaches outside the database, handed to Lifecycle together.
public sealed record DomainSeams(IBroadcaster Broadcaster, IJobQueue Jobs, IConnectionRevoker Connections);
