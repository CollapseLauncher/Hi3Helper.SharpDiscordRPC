using DiscordRPC.Exceptions;
using System;
using System.Text.Json.Serialization;

namespace DiscordRPC.Entities;

/// <summary>
/// Information about the pictures used in the Rich Presence.
/// </summary>
[Serializable]
public class Assets
{
    private const string ExternalKeyPrefix = "mp:external";

    #region Large Image
    /// <summary>
    /// Name of the uploaded image for the large profile artwork.
    /// <para>Max 256 characters.</para>
    /// </summary>
    /// <remarks>Allows URL to directly link to images.</remarks>
    [JsonPropertyName("large_image")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LargeImageKey
    {
        get;
        set
        {
            if (!BaseRichPresence.ValidateString(value, out field, false, 256))
                throw new StringOutOfRangeException(256);
        }
    }

    /// <summary>
    /// The tooltip for the large square image. For example, "Summoners Rift" or "Horizon Lunar Colony".
    /// <para>Max 128 characters.</para>
    /// </summary>
    [JsonPropertyName("large_text")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LargeImageText
    {
        get;
        set
        {
            if (!BaseRichPresence.ValidateString(value, out field, false, 128))
                throw new StringOutOfRangeException(128);
        }
    }

    /// <summary>
    /// URL that is linked to when clicking on the large image in the activity card.
    /// <para>Max 256 characters.</para>
    /// </summary>
    [JsonPropertyName("large_url")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LargeImageUrl
    {
        get;
        set
        {
            if (!BaseRichPresence.ValidateString(value, out field, false, 256))
                throw new StringOutOfRangeException(256);

            if (!BaseRichPresence.ValidateUrl(field))
                throw new ArgumentException("Url must be a valid URI");
        }
    }

    /// <summary>
    /// The ID of the large image. This is only set after Update Presence and will automatically become null when <see cref="LargeImageKey"/> is changed.
    /// </summary>
    [JsonIgnore]
    public string? LargeImageID { get; private set; }

    /// <summary>
    /// Gets if the large square image is from an external link
    /// </summary>
    [JsonIgnore]
    public bool IsLargeImageKeyExternal { get; private set; }
    #endregion

    #region Small Image
    /// <summary>
    /// Name of the uploaded image for the small profile artwork.
    /// <para>Max 256 characters.</para>
    /// </summary>
    /// <remarks>Allows URL to directly link to images.</remarks>
    [JsonPropertyName("small_image")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SmallImageKey
    {
        get;
        set
        {
            if (!BaseRichPresence.ValidateString(value, out field, false, 256))
                throw new StringOutOfRangeException(256);

            //Reset the small image id
            SmallImageID = null;
        }
    }

    /// <summary>
    /// The tooltip for the small circle image. For example, "LvL 6" or "Ultimate 85%".
    /// <para>Max 128 characters.</para>
    /// </summary>
    [JsonPropertyName("small_text")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SmallImageText
    {
        get;
        set
        {
            if (!BaseRichPresence.ValidateString(value, out field, false, 128))
                throw new StringOutOfRangeException(128);
        }
    }

    /// <summary>
    /// URL that is linked to when clicking on the small image in the activity card.
    /// <para>Max 256 characters.</para>
    /// </summary>
    [JsonPropertyName("small_url")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SmallImageUrl
    {
        get;
        set
        {
            if (!BaseRichPresence.ValidateString(value, out field, false, 256))
                throw new StringOutOfRangeException(256);

            if (!BaseRichPresence.ValidateUrl(field))
                throw new ArgumentException("Url must be a valid URI");
        }
    }

    /// <summary>
    /// The ID of the small image. This is only set after Update Presence and will automatically become null when <see cref="SmallImageKey"/> is changed.
    /// </summary>
    [JsonIgnore]
    public string? SmallImageID { get; private set; }

    /// <summary>
    /// Gets if the small profile artwork is from an external link
    /// </summary>
    [JsonIgnore]
    public bool IsSmallImageKeyExternal { get; private set; }
    #endregion


    /// <summary>
    /// Merges this asset with the other, taking into account for IDs instead of keys.
    /// </summary>
    /// <param name="other"></param>
    internal void Merge(Assets? other)
    {
        if (other == null)
        {
            return;
        }

        //Copy over the names
        SmallImageText = other.SmallImageText;
        SmallImageUrl  = other.SmallImageUrl;
        LargeImageText = other.LargeImageText;
        LargeImageUrl  = other.LargeImageUrl;

        //Convert the Large Key
        string largeKey = other.LargeImageKey ?? "";
        if (largeKey.StartsWith(ExternalKeyPrefix))
        {
            IsLargeImageKeyExternal = true;
            LargeImageID = largeKey;
        }
        else if (ulong.TryParse(largeKey, out _))
        {
            IsLargeImageKeyExternal = false;
            LargeImageID = largeKey;
        }
        else
        {
            IsLargeImageKeyExternal = false;
            LargeImageID            = null;
            LargeImageKey           = largeKey;
        }

        //Convert the Small Key
        //  TODO: Make this a function
        string smallKey = other.SmallImageKey ?? "";
        if (smallKey.StartsWith(ExternalKeyPrefix))
        {
            IsSmallImageKeyExternal = true;
            SmallImageID            = smallKey;
        }
        else if (ulong.TryParse(smallKey, out _))
        {
            IsSmallImageKeyExternal = false;
            SmallImageID            = smallKey;
        }
        else
        {
            IsSmallImageKeyExternal = false;
            SmallImageID            = null;
            SmallImageKey           = smallKey;
        }
    }
}
