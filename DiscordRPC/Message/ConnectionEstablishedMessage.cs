using System;

namespace DiscordRPC.Message;

/// <summary>
/// The connection to the discord client was successful. This is called before <see cref="MessageType.Ready"/>.
/// </summary>
public class ConnectionEstablishedMessage : MessageBase
{
    /// <summary>
    /// The type of message received from discord
    /// </summary>
    public override MessageType Type => MessageType.ConnectionEstablished;

    /// <summary>
    /// The pipe we ended up connecting too
    /// </summary>
    [Obsolete("The connected pipe is not necessary information.")]
    public int ConnectedPipe { get; internal set; }
}