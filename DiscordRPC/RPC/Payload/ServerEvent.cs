#if INCLUDE_FULL_RPC
using System;
#endif

namespace DiscordRPC.RPC.Payload
{
    /// <summary>
    /// See https://discordapp.com/developers/docs/topics/rpc#rpc-server-payloads-rpc-events for documentation
    /// </summary>
    internal enum ServerEvent
    {
        /// <summary>
        /// Sent when the server is ready to accept messages
        /// </summary>
        READY,

        /// <summary>
        /// Sent when something bad has happened
        /// </summary>
        ERROR,

        /// <summary>
        /// Join Event
        /// </summary>
        ACTIVITY_JOIN,

        /// <summary>
        /// Spectate Event
        /// </summary>
        ACTIVITY_SPECTATE,

        /// <summary>
        /// Request Event
        /// </summary>
        ACTIVITY_JOIN_REQUEST,

#if INCLUDE_FULL_RPC
        // Old things that are obsolete
        [Obsolete("This value is a part of the RPC API and is not supported by this library.", true)]
        GUILD_STATUS,
        [Obsolete("This value is a part of the RPC API and is not supported by this library.", true)]
        GUILD_CREATE,
        [Obsolete("This value is a part of the RPC API and is not supported by this library.", true)]
        CHANNEL_CREATE,
        [Obsolete("This value is a part of the RPC API and is not supported by this library.", true)]
        VOICE_CHANNEL_SELECT,
        [Obsolete("This value is a part of the RPC API and is not supported by this library.", true)]
        VOICE_STATE_CREATED,
        [Obsolete("This value is a part of the RPC API and is not supported by this library.", true)]
        VOICE_STATE_UPDATED,
        [Obsolete("This value is a part of the RPC API and is not supported by this library.", true)]
        VOICE_STATE_DELETE,
        [Obsolete("This value is a part of the RPC API and is not supported by this library.", true)]
        VOICE_SETTINGS_UPDATE,
        [Obsolete("This value is a part of the RPC API and is not supported by this library.", true)]
        VOICE_CONNECTION_STATUS,
        [Obsolete("This value is a part of the RPC API and is not supported by this library.", true)]
        SPEAKING_START,
        [Obsolete("This value is a part of the RPC API and is not supported by this library.", true)]
        SPEAKING_STOP,
        [Obsolete("This value is a part of the RPC API and is not supported by this library.", true)]
        MESSAGE_CREATE,
        [Obsolete("This value is a part of the RPC API and is not supported by this library.", true)]
        MESSAGE_UPDATE,
        [Obsolete("This value is a part of the RPC API and is not supported by this library.", true)]
        MESSAGE_DELETE,
        [Obsolete("This value is a part of the RPC API and is not supported by this library.", true)]
        NOTIFICATION_CREATE,
        [Obsolete("This value is a part of the RPC API and is not supported by this library.", true)]
        CAPTURE_SHORTCUT_CHANGE
#endif
    }
}
