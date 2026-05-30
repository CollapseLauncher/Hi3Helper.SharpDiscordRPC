using DiscordRPC.RPC.Payload;

namespace DiscordRPC.Message;

/// <summary>
/// Called as validation of subscribe
/// </summary>
public class UnsubscribeMessage : MessageBase
{
    /// <summary>
    /// The type of message received from discord
    /// </summary>
    public override MessageType Type => MessageType.Unsubscribe;

    /// <summary>
    /// The event that was subscribed too.
    /// </summary>
    public EventType Event { get; internal set; }

    internal UnsubscribeMessage(ServerEvent evt)
    {
        switch (evt)
        {
            case ServerEvent.Ready:
            case ServerEvent.Error:
            case ServerEvent.ActivitySpectate:
            case ServerEvent.ActivityJoin:
            default:
                Event = EventType.Join;
                break;

            case ServerEvent.ActivityJoinRequest:
                Event = EventType.JoinRequest;
                break;
        }
    }
}