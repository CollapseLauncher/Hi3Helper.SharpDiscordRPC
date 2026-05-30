using Microsoft.Extensions.Logging;
using System;
using System.Diagnostics;
using System.IO;
#pragma warning disable CA1873

namespace DiscordRPC.Registry;

/// <summary>
/// Registers a URI scheme on Unix-like systems using the xdg-open command. 
/// The scheme is saved as a .desktop file in the user's local applications directory.
/// </summary>
public sealed class UnixUriScheme : IRegisterUriScheme
{
    private readonly ILogger? _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="UnixUriScheme"/> class.
    /// </summary>
    /// <param name="logger"></param>
    public UnixUriScheme(ILogger? logger)
    {
        _logger = logger;
    }

    /// <inheritdoc/>
    public bool Register(SchemeInfo info)
    {
        string? home = Environment.GetEnvironmentVariable("HOME");
        if (string.IsNullOrEmpty(home))
        {
            _logger?.LogError("Failed to register because the HOME variable was not set.");
            return false;
        }

        string exe = info.ExecutablePath;
        if (string.IsNullOrEmpty(exe))
        {
            _logger?.LogError("Failed to register because the application was not located.");
            return false;
        }

        //Prepare the command
        string command =
            //A steam command instead
            info.UsingSteamApp ? $"xdg-open steam://rungameid/{info.SteamAppID}" :
            //Just a regular discord command
            exe;


        //Prepare the file
        const string desktopFileFormat = """
                                         [Desktop Entry]
                                         Name=Game {0}
                                         Exec={1} %u
                                         Type=Application
                                         NoDisplay=true
                                         Categories=Discord;Games;
                                         MimeType=x-scheme-handler/discord-{2}
                                         """;

        string file = string.Format(desktopFileFormat, info.ApplicationID, command, info.ApplicationID);

        //Prepare the path
        string filename = $"/discord-{info.ApplicationID}.desktop";
        string filepath = home + "/.local/share/applications";
        DirectoryInfo directory = Directory.CreateDirectory(filepath);
        if (!directory.Exists)
        {
            _logger?.LogError("Failed to register because {} does not exist", filepath);
            return false;
        }

        //Write the file
        File.WriteAllText(filepath + filename, file);

        //Register the Mime type
        if (!RegisterMime(info.ApplicationID))
        {
            _logger?.LogError("Failed to register because the Mime failed.");
            return false;
        }

        _logger?.LogTrace("Registered {filePath}, {file}, {cmd}", filepath + filename, file, command);
        return true;
    }

    private static bool RegisterMime(string appid)
    {
        //Format the arguments
        const string format    = "default discord-{0}.desktop x-scheme-handler/discord-{0}";
        string       arguments = string.Format(format, appid);

        //Run the process and wait for response
        Process process = Process.Start("xdg-mime", arguments);
        process.WaitForExit();

        //Return if successful
        return process.ExitCode >= 0;
    }
}