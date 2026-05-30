using System.Text.Json.Serialization;

namespace DiscordRPC.RPC.Payload;

internal class ClosePayload : PayloadBase
{
    /// <summary>
    /// The close code the discord gave us
    /// </summary>
    [JsonPropertyName("code")]
    public int Code { get; set; } = -1;

    /// <summary>
    /// The close reason discord gave us
    /// </summary>
    [JsonPropertyName("message")]
    public string Reason { get; set; } = "";
}
