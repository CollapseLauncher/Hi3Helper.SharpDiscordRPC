using Microsoft.Extensions.Logging;
using System;
using System.IO;
// ReSharper disable StringLiteralTypo
#pragma warning disable CA1873

namespace DiscordRPC.Registry;

/// <summary>
/// Registers a URI scheme on MacOS.
/// </summary>
public sealed class MacUriScheme : IRegisterUriScheme
{
    private readonly ILogger? _logger;

    private static readonly string[] DiscordClientFolders =
    [
        "discord",
        "discordptb",
        "discordcanary"
    ];

    /// <summary>
    /// Initializes a new instance of the <see cref="MacUriScheme"/> class.
    /// </summary>
    /// <param name="logger"></param>
    public MacUriScheme(ILogger? logger)
    {
        _logger = logger;
    }

    /// <inheritdoc/>
    public bool Register(SchemeInfo info)
    {
        string exe = info.ExecutablePath;
        if (string.IsNullOrEmpty(exe))
        {
            _logger?.LogError("Failed to register because the application could not be located.");
            return false;
        }

        _logger?.LogTrace("Registering Steam Command");

        //Prepare the command
        string command = exe;
        if (info.UsingSteamApp) command = $"steam://rungameid/{info.SteamAppID}";
        else _logger?.LogWarning("This library does not fully support MacOS URI Scheme Registration.");

        //get the folder ready
        foreach (string folder in DiscordClientFolders)
        {
            string discordDirectory = Path.Combine(
                Environment.GetEnvironmentVariable("HOME") ?? "",
                "Library/Application Support/",
                folder
            );

            if (Directory.Exists(discordDirectory))
            {
                _logger?.LogTrace("Discord client folder exists: {}", discordDirectory);
                RegisterSchemeForClient(discordDirectory, info.ApplicationID, command);
            }
            else
            {
                _logger?.LogTrace("Discord client folder does not exist: {}", discordDirectory);
            }
        }

        return true;
    }

    private static void RegisterSchemeForClient(string discordDirectory, string appId, string command)
    {
        string filepath = Path.Combine(discordDirectory, "games", $"{appId}.json");
        if (!Directory.Exists(Path.GetDirectoryName(filepath)))
            Directory.CreateDirectory(Path.GetDirectoryName(filepath) ?? "");

        //Write the contents to file
        File.WriteAllText(filepath, $"{{ \"command\": \"{command}\" }}");
    }
}
