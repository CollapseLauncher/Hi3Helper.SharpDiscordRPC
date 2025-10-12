using Microsoft.Extensions.Logging;
using System;
using LoggerDotNet = Microsoft.Extensions.Logging.ILogger;

namespace DiscordRPC.Logging
{
    public class MsILoggerWrapper(LoggerDotNet _loggerNet) : ILogger
    {
        public           LogLevel Level { get; set; }

        private string FormatMessage(string message, params object[] args)
        {
            return string.Format(message, args);
        }

        public void Trace(string message, params object[] args)
        {
#if DEBUG
            _loggerNet.LogTrace(FormatMessage(message, args));
#endif
        }

        public void Info(string message, params object[] args)
        {
            _loggerNet.LogInformation(FormatMessage(message, args));
        }

        public void Warning(string message, params object[] args)
        {
            _loggerNet.LogWarning(FormatMessage(message, args));
        }

        public void Error(string message, params object[] args)
        {
            _loggerNet.LogError(FormatMessage(message, args));
        }

        public void Error(Exception ex, string message, params object[] args)
        {
            _loggerNet.LogError(ex, FormatMessage(message, args));
        }
    }
}