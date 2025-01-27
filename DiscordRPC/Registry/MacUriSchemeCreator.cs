using DiscordRPC.Logging;
using System.IO;

namespace DiscordRPC.Registry
{
    internal class MacUriSchemeCreator : IUriSchemeCreator
    {
        private ILoggerRpc _iLoggerRpc;
        public MacUriSchemeCreator(ILoggerRpc iLoggerRpc)
        {
            _iLoggerRpc = iLoggerRpc;
        }

        public bool RegisterUriScheme(UriSchemeRegister register)
        {
            // var home = Environment.GetEnvironmentVariable("HOME");
            // if (string.IsNullOrEmpty(home)) return;     // TODO: Log Error

            string exe = register.ExecutablePath;
            if (string.IsNullOrEmpty(exe))
            {
                _iLoggerRpc.Error("Failed to register because the application could not be located.");
                return false;
            }

            _iLoggerRpc.Trace("Registering Steam Command");

            // Prepare the command
            string command = exe;
            if (register.UsingSteamApp) command = $"steam://rungameid/{register.SteamAppID}";
            else _iLoggerRpc.Warning("This library does not fully support MacOS URI Scheme Registration.");

            // get the folder ready
            string filepath = "~/Library/Application Support/discord/games";
            var directory = Directory.CreateDirectory(filepath);
            if (!directory.Exists)
            {
                _iLoggerRpc.Error("Failed to register because {0} does not exist", filepath);
                return false;
            }

            // Write the contents to file
            string applicationSchemeFilePath = $"{filepath}/{register.ApplicationID}.json";
            File.WriteAllText(applicationSchemeFilePath, "{ \"command\": \""+ command + "\" }");
            _iLoggerRpc.Trace("Registered {0}, {1}", applicationSchemeFilePath, command);
            return true;
        }
    }
}
