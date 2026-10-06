namespace Campfire.Jobs.Runner;

public enum JobLogLevel
{
    Information,
    Warning,
    Error,
}

// Where the runner reports what Resque would log: performed and failed jobs, rejected enqueues
// and abandoned work at shutdown. The server adapts it to its logger.
public delegate void JobLog(JobLogLevel level, string message, Exception? exception);
