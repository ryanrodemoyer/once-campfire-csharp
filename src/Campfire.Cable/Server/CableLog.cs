using Microsoft.Extensions.Logging;

namespace Campfire.Cable.Server;

/// <summary>What Action Cable logs, worded as Rails words it.</summary>
internal static partial class CableLog
{
    [LoggerMessage(Level = LogLevel.Debug, Message = "[ActionCable] Broadcasting to {Broadcasting}")]
    public static partial void Broadcasting(ILogger logger, string broadcasting);

    [LoggerMessage(Level = LogLevel.Error, Message = "Request origin not allowed: {Origin}")]
    public static partial void OriginNotAllowed(ILogger logger, string? origin);

    [LoggerMessage(Level = LogLevel.Error, Message = "An unauthorized connection attempt was rejected")]
    public static partial void Unauthorized(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Removing connection ({Identifier})")]
    public static partial void RemovingConnection(ILogger logger, string identifier);

    [LoggerMessage(Level = LogLevel.Error, Message = "Couldn't handle non-string message: Array")]
    public static partial void NonStringMessage(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not execute command from ({Message}): {Problem}")]
    public static partial void CouldNotExecute(ILogger logger, string message, string problem);

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not execute command from ({Message})")]
    public static partial void CommandFailed(ILogger logger, Exception error, string message);

    [LoggerMessage(Level = LogLevel.Error, Message = "Received unrecognized command in {Message}")]
    public static partial void UnrecognizedCommand(ILogger logger, string message);

    [LoggerMessage(Level = LogLevel.Error, Message = "Subscription class not found: {Channel}")]
    public static partial void ChannelNotFound(ILogger logger, string channel);

    [LoggerMessage(Level = LogLevel.Error, Message = "Unable to find subscription with identifier: {Identifier}")]
    public static partial void SubscriptionNotFound(ILogger logger, string? identifier);

    [LoggerMessage(Level = LogLevel.Error, Message = "Unable to process {Channel}#{Action}")]
    public static partial void UnableToProcess(ILogger logger, string channel, string action);

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not unsubscribe {Identifier}")]
    public static partial void UnsubscribeFailed(ILogger logger, Exception error, string identifier);
}
