#if NET471_OR_GREATER || NETSTANDARD2_0_OR_GREATER || NET5_0_OR_GREATER
#define HAS_RUNTIME_INFORMATION
#endif

using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using System;
using System.Runtime.InteropServices;
#pragma warning disable CA1873

namespace DiscordRPC.Registry;

/// <summary>
/// Registers a URI scheme on Windows.
/// </summary>
public sealed class WindowsUriScheme : IRegisterUriScheme
{
    private readonly ILogger? _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="WindowsUriScheme"/> class.
    /// </summary>
    /// <param name="logger"></param>
    public WindowsUriScheme(ILogger? logger)
    {
        _logger = logger;
    }

    /// <inheritdoc/>
    public bool Register(SchemeInfo info)
    {
        //Prepare our location
        string location = info.ExecutablePath;

        //Prepare the Scheme, Friendly name, default icon and default command
        string schemePath = $"discord-{info.ApplicationID}";
        string friendlyName = $"Run game {info.ApplicationID} protocol";
        string command = location;

        //We have a steam ID, so attempt to replace the command with a steam command
        if (info.UsingSteamApp)
        {
            //Try to get the steam location. If found, set the command to a run steam instead.
            string? steam = GetSteamLocation();
            command = $"\"{steam}\" steam://rungameid/{info.SteamAppID}";
        }

        //Okay, now actually register it
        CreateUriScheme(schemePath, friendlyName, location, command);
        return true;
    }

    /// <summary>
    /// Creates the actual scheme
    /// </summary>
    /// <param name="scheme"></param>
    /// <param name="friendlyName"></param>
    /// <param name="defaultIcon"></param>
    /// <param name="command"></param>
    private void CreateUriScheme(string scheme, string friendlyName, string defaultIcon, string command)
    {
#if HAS_RUNTIME_INFORMATION
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            throw new PlatformNotSupportedException("Requires Windows to use the Registry");
#endif

        using (RegistryKey key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey($@"SOFTWARE\Classes\{scheme}"))
        {
            key.SetValue("", $"URL:{friendlyName}");
            key.SetValue("URL Protocol", "");

            using (RegistryKey iconKey = key.CreateSubKey("DefaultIcon"))
                iconKey.SetValue("", defaultIcon);

            using (RegistryKey commandKey = key.CreateSubKey(@"shell\open\command"))
                commandKey.SetValue("", command);
        }

        _logger?.LogTrace("Registered {a}, {b}, {c}", scheme, friendlyName, command);
    }

    /// <summary>
    /// Gets the current location of the steam client
    /// </summary>
    /// <returns></returns>
    public static string? GetSteamLocation()
    {
#if HAS_RUNTIME_INFORMATION
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            throw new PlatformNotSupportedException("Requires Windows to use the Registry");
#endif

        using RegistryKey? key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
        return key?.GetValue("SteamExe") as string;
    }
}