#if NETSTANDARD1_1_OR_GREATER
#define USE_RUNTIME_INFO
#endif

    using DiscordRPC.Logging;
    using System;
    using System.Diagnostics;
#if USE_RUNTIME_INFO
using System.Runtime.InteropServices;
#endif

namespace DiscordRPC.Registry
{
    internal class UriSchemeRegister
    {
        /// <summary>
        /// The ID of the Discord App to register
        /// </summary>
        public string ApplicationID { get; set; }

        /// <summary>
        /// Optional Steam App ID to register. If given a value, then the game will launch through steam instead of Discord.
        /// </summary>
        public string SteamAppID { get; set; }

        /// <summary>
        /// Is this register using steam?
        /// </summary>
        public bool UsingSteamApp { get { return !string.IsNullOrEmpty(SteamAppID) && SteamAppID != ""; } }

        /// <summary>
        /// The full executable path of the application.
        /// </summary>
        public string ExecutablePath { get; set; }

        private ILoggerRpc _iLoggerRpc;
        public UriSchemeRegister(ILoggerRpc iLoggerRpc, string applicationID, string steamAppID = null, string executable = null)
        {
            _iLoggerRpc    = iLoggerRpc;
            ApplicationID  = applicationID.Trim();
            SteamAppID     = steamAppID?.Trim();
            ExecutablePath = executable ?? GetApplicationLocation();
        }

        /// <summary>
        /// Registers the URI scheme, using the correct creator for the correct platform
        /// </summary>
        public bool RegisterUriScheme()
        {
            // Get the creator
            IUriSchemeCreator creator;
            switch(Environment.OSVersion.Platform)
            {
                case PlatformID.Win32Windows:
                case PlatformID.Win32S:
                case PlatformID.Win32NT:
                case PlatformID.WinCE:
                    _iLoggerRpc.Trace("Creating Windows Scheme Creator");
                    creator = new WindowsUriSchemeCreator(_iLoggerRpc);
                    break;

                case PlatformID.Unix:
#if USE_RUNTIME_INFO
                    if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                    {
                        _logger.Trace("Creating MacOSX Scheme Creator");
                        creator = new MacUriSchemeCreator(_logger);
                    }
                    else
                    {
#endif
                        _iLoggerRpc.Trace("Creating Unix Scheme Creator");
                        creator = new UnixUriSchemeCreator(_iLoggerRpc);
#if USE_RUNTIME_INFO
                    }
#endif
                    break;

#if !USE_RUNTIME_INFO
                case PlatformID.MacOSX:
                    _iLoggerRpc.Trace("Creating MacOSX Scheme Creator");
                    creator = new MacUriSchemeCreator(_iLoggerRpc);
                    break;
#endif

                default:
                    _iLoggerRpc.Error("Unknown Platform: {0}", Environment.OSVersion.Platform);
                    throw new PlatformNotSupportedException("Platform does not support registration.");
            }

            // Register the app
            if (creator.RegisterUriScheme(this))
            {
                _iLoggerRpc.Info("URI scheme registered.");
                return true;
            }

            return false;
        }

        /// <summary>
        /// Gets the FileName for the currently executing application
        /// </summary>
        /// <returns></returns>
        public static string GetApplicationLocation()
        {
            return Process.GetCurrentProcess().MainModule?.FileName;
        }
    }
}
