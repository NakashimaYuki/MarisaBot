using System.Text.Json;
using Marisa.BotDriver.Entity.Message;
using Marisa.Configuration;
using NLog;

namespace Marisa.BotDriver.Plugin;

public static class ExceptionDump
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    public static string? Save(Exception exception, MessageAuditContext auditContext, string? source = null)
    {
        try
        {
            var directory = Path.Join(ConfigurationManager.Configuration.TempPath, "exceptions");
            Directory.CreateDirectory(directory);

            var timestamp = DateTimeOffset.UtcNow;
            var fileName = $"{timestamp:yyyyMMdd-HHmmssfff}-{Guid.NewGuid():N}.json";
            var filePath = Path.Join(directory, fileName);

            var payload = new ExceptionDumpPayload(
                timestamp,
                source ?? "unknown",
                auditContext,
                ExceptionFrames(exception)
            );

            var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions
            {
                WriteIndented = true
            });

            File.WriteAllText(filePath, json);
            return filePath;
        }
        catch (Exception dumpException)
        {
            Logger.Warn(
                "event=exception_dump_failed correlation={0} error_type={1} hresult={2}",
                auditContext.CorrelationId,
                dumpException.GetType().FullName ?? dumpException.GetType().Name,
                dumpException.HResult);
            return null;
        }
    }

    private static IReadOnlyList<ExceptionFrame> ExceptionFrames(Exception exception)
    {
        var frames = new List<ExceptionFrame>();
        var seen = new HashSet<Exception>(ReferenceEqualityComparer.Instance);
        var pending = new Queue<Exception>();
        pending.Enqueue(exception);

        while (pending.Count > 0 && frames.Count < 16)
        {
            var current = pending.Dequeue();
            if (!seen.Add(current)) continue;

            frames.Add(new ExceptionFrame(
                current.GetType().FullName ?? current.GetType().Name,
                current.HResult,
                current.StackTrace));

            if (current is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions) pending.Enqueue(inner);
            }
            else if (current.InnerException is not null)
            {
                pending.Enqueue(current.InnerException);
            }
        }

        return frames;
    }

    private sealed record ExceptionDumpPayload(
        DateTimeOffset TimestampUtc,
        string Source,
        MessageAuditContext AuditContext,
        IReadOnlyList<ExceptionFrame> Exceptions
    );

    private sealed record ExceptionFrame(string Type, int HResult, string? StackTrace);
}
