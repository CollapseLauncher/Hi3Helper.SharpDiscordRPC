using Microsoft.Extensions.Logging;
using System;

namespace DiscordRPC.Logging
{
    public class MsILoggerWrapper(ILogger logger) : ILoggerRpc
    {
        public LogLevel Level { get; set; }

        private string FormatMessage(string message, params object[] args)
        {
            return string.Format(message, args);
        }

        public void Trace(string message, params object[] args)
        {
#if DEBUG
            logger.LogTrace(FormatMessage(message, args));
#endif
        }

        public void Info(string message, params object[] args)
        {
            logger.LogInformation(FormatMessage(message, args));
        }

        public void Warning(string message, params object[] args)
        {
            logger.LogWarning(FormatMessage(message, args));
        }

        public void Error(string message, params object[] args)
        {
            logger.LogError(FormatMessage(message, args));
        }

        public void Error(Exception ex, string message, params object[] args)
        {
            logger.LogError(ex, FormatMessage(message, args));
        }
    }
}