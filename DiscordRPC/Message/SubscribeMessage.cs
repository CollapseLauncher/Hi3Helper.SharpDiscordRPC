using DiscordRPC.RPC.Payload;

namespace DiscordRPC.Message;

/// <summary>
/// Called as validation of subscribe
/// </summary>
public class SubscribeMessage : MessageBase
{
    /// <summary>
    /// The type of message received from discord
    /// </summary>
    public override MessageType Type => MessageType.Subscribe;

    /// <summary>
    /// The event that was subscribed too.
    /// </summary>
    public EventType Event { get; internal set; }

    internal SubscribeMessage(ServerEvent evt)
    {
        switch (evt)
        {
            case ServerEvent.ActivityJoin:
            case ServerEvent.Ready:
            case ServerEvent.Error:
            case ServerEvent.ActivitySpectate:
            default:
                Event = EventType.Join;
                break;

            case ServerEvent.ActivityJoinRequest:
                Event = EventType.JoinRequest;
                break;
        }
    }
}