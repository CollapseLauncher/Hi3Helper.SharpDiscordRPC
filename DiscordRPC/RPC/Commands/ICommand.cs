using DiscordRPC.RPC.Payload;

namespace DiscordRPC.RPC.Commands;

internal interface ICommand
{
    PayloadBase PreparePayload(long nonce);
}
