using System;

namespace DiscordRPC.Exceptions;

/// <summary>
/// A InvalidConfigurationException is thrown when trying to perform an action that conflicts with the current configuration.
/// </summary>
public class InvalidConfigurationException : Exception
{
    internal InvalidConfigurationException(string message) : base(message) { }
}
