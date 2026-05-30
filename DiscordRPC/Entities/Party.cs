using System;
using System.Text.Json.Serialization;

namespace DiscordRPC.Entities;

/// <summary>
/// Structure representing the part the player is in.
/// </summary>
[Serializable]
public class Party
{
    /// <summary>
    /// Privacy of the party
    /// </summary>
    public enum PrivacySetting
    {
        /// <summary>
        /// The party is private, invites only.
        /// </summary>
        Private = 0,

        /// <summary>
        /// The party is public, anyone can join.
        /// </summary>
        Public = 1
    }

    /// <summary>
    /// A unique ID for the player's current party / lobby / group. If this is not supplied, they player will not be at a party and the rest of the information will not be sent. 
    /// <para>Max 128 Bytes</para>
    /// </summary>
    [JsonPropertyName("id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Id { get; set; }

    /// <summary>
    /// The current size of the players party / lobby / group.
    /// </summary>
    [JsonIgnore]
    public int Size { get; set; }

    /// <summary>
    /// The maximum size of the party / lobby / group. This is required to be larger than <see cref="Size"/>. If it is smaller than the current party size, it will automatically be set too <see cref="Size"/> when the presence is sent.
    /// </summary>
    [JsonIgnore]
    public int Max { get; set; }

    /// <summary>
    /// The privacy of the party
    /// </summary>
    [JsonPropertyName("privacy")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]

    public PrivacySetting Privacy { get; set; }

    [JsonPropertyName("size")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    // ReSharper disable once UnusedMember.Local
    private int[] SizeInner
    {
        get
        {
            //see issue https://github.com/discordapp/discord-rpc/issues/111
            int size = Math.Max(1, Size);
            return [size, Math.Max(size, Max)];
        }

        set
        {
            if (value.Length != 2)
            {
                Size = 0;
                Max  = 0;
            }
            else
            {
                Size = value[0];
                Max  = value[1];
            }
        }
    }
}
