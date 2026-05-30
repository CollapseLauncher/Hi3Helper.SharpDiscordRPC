using DiscordRPC.Exceptions;
using System;
using System.Text;
using System.Text.Json.Serialization;

namespace DiscordRPC.Entities;

/// <summary>
/// A Rich Presence button.
/// </summary>
public class Button
{
    /// <summary>
    /// Text shown on the button
    /// <para>Max 31 bytes.</para>
    /// </summary>
    [JsonPropertyName("label")]
    public string? Label
    {
        get;
        set
        {
            if (!BaseRichPresence.ValidateString(value, out field, true, 31, Encoding.UTF8))
                throw new StringOutOfRangeException(31);
        }
    }

    /// <summary>
    /// The URL opened when clicking the button.
    /// <para>Max 512 characters.</para>
    /// </summary>
    [JsonPropertyName("url")]
    public string? Url
    {
        get;
        set
        {
            if (!BaseRichPresence.ValidateString(value, out field, false, 512))
                throw new StringOutOfRangeException(512);

            if (!BaseRichPresence.ValidateUrl(field))
                throw new ArgumentException("Url must be a valid URI");
        }
    }
}