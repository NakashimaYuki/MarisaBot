using System.Reflection;
using Marisa.BotDriver.Entity.Message;
using Marisa.BotDriver.Entity.MessageData;
using Marisa.BotDriver.Plugin.Attributes;
using Marisa.Configuration;
using NLog;

namespace Marisa.BotDriver.Plugin;

[MarisaPlugin]
public class MarisaPluginBase
{
    public virtual Task BackgroundService(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public virtual Task ExceptionHandler(Exception exception, Message message)
    {
        var log = LogManager.GetCurrentClassLogger();
        var currentException = Unwrap(exception);
        var dumpPath = TrySaveDump(currentException);

        log.Error(
            "event=plugin_exception {0} source={1} error_type={2} hresult={3} dump={4}",
            message.AuditContext,
            GetType().FullName ?? GetType().Name,
            currentException.GetType().FullName ?? currentException.GetType().Name,
            currentException.HResult,
            dumpPath is null ? "none" : Path.GetFileName(dumpPath));

        if (currentException is MissingConfigurationException missingConfigurationException)
        {
            message.Reply(missingConfigurationException.UserMessage);
            return Task.CompletedTask;
        }

        message.Send(new MessageDataText($"出现异常，已上报开发者（故障编号：{message.AuditContext.CorrelationId}）"));

        return Task.CompletedTask;

        string? TrySaveDump(Exception currentException)
        {
            return currentException is MissingConfigurationException
                ? null
                : ExceptionDump.Save(currentException, message.AuditContext, GetType().FullName);
        }

        static Exception Unwrap(Exception exception)
        {
            while (true)
            {
                if (exception is TargetInvocationException { InnerException: not null } targetInvocationException)
                {
                    exception = targetInvocationException.InnerException!;
                    continue;
                }

                if (exception is AggregateException { InnerExceptions.Count: 1 } aggregateException)
                {
                    exception = aggregateException.InnerExceptions[0]!;
                    continue;
                }

                return exception;
            }
        }
    }
}
