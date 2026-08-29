using System.Diagnostics;
using System.Reflection;
using Marisa.BotDriver.DI;
using Marisa.BotDriver.DI.Message;
using Marisa.BotDriver.Entity.Message;
using Marisa.BotDriver.Plugin;
using Marisa.BotDriver.Plugin.Attributes;
using Microsoft.Extensions.DependencyInjection;
using NLog;
using Polly;
using Polly.Timeout;

namespace Marisa.BotDriver;

public abstract class BotDriver(
    IServiceProvider serviceProvider,
    IEnumerable<MarisaPluginBase> pluginsAll,
    DictionaryProvider dict,
    MessageSenderProvider messageSenderProvider,
    MessageQueueProvider messageQueueProvider)
{
    protected readonly MessageSenderProvider MessageSenderProvider = messageSenderProvider;
    protected readonly MessageQueueProvider MessageQueueProvider = messageQueueProvider;
    protected readonly MessageDispatcher MessageDispatcher = new(pluginsAll, serviceProvider, dict);
    protected readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private readonly CancellationTokenSource _shutdown = new();

    protected CancellationToken ShutdownToken => _shutdown.Token;
    protected virtual TimeSpan MessageProcessingTimeout => TimeSpan.FromMinutes(10);

    /// <summary>
    /// 配置依赖注入
    /// </summary>
    /// <param name="types">一堆插件的类型</param>
    /// <returns>ServiceCollection</returns>
    protected static IServiceCollection Config(Type[] types)
    {
        var sc = new ServiceCollection()
            .AddScoped(p => p)
            .AddScoped(p => (ServiceProvider)p)
            .AddScoped<DictionaryProvider>()
            .AddScoped<MessageQueueProvider>()
            .AddScoped<MessageSenderProvider>();

        var plugins = types
            .Where(t => t.GetCustomAttribute<MarisaPluginAttribute>(true) is not null)
            .Where(t => t.GetCustomAttribute<MarisaPluginDisabledAttribute>(false) is null)
            .OrderByDescending(t => t.GetCustomAttribute<MarisaPluginAttribute>()!.Priority);

        var logger = LogManager.GetCurrentClassLogger();

        foreach (var plugin in plugins)
        {
            logger.Info($"Enabled plugin: `{plugin}`");
            sc.AddScoped(typeof(MarisaPluginBase), plugin);
        }

        return sc;
    }

    /// <summary>
    /// 处理消息的默认实现
    /// </summary>
    /// <exception cref="Exception"></exception>
    protected virtual async Task ProcMessage()
    {
        try
        {
            while (await MessageQueueProvider.RecvQueue.Reader.WaitToReadAsync(ShutdownToken))
            {
                var message = await MessageQueueProvider.RecvQueue.Reader.ReadAsync(ShutdownToken);
                _ = ProcMessageStep(message);
            }
        }
        catch (OperationCanceledException) when (ShutdownToken.IsCancellationRequested)
        {
            return;
        }

        Logger.Fatal("Message processing task exited unexpectedly");
    }

    protected async Task ProcMessageStep(Message message)
    {
        var audit = message.AuditContext;
        var started = Stopwatch.GetTimestamp();
        Logger.Info("event=message_received {0}", audit);

        try
        {
            var res = await Policy.TimeoutAsync(MessageProcessingTimeout, TimeoutStrategy.Pessimistic).ExecuteAndCaptureAsync(async () =>
            {
                var toInvoke = MessageDispatcher.Dispatch(message);

                foreach (var (plugin, method, m2) in toInvoke)
                {
                    var handlerStarted = Stopwatch.GetTimestamp();
                    var state = await MessageDispatcher.Invoke(plugin, method, m2);
                    Logger.Info(
                        "event=handler_completed {0} plugin={1} handler={2} outcome={3} duration_ms={4:F1}",
                        audit,
                        plugin.GetType().FullName ?? plugin.GetType().Name,
                        method.Name,
                        state,
                        Stopwatch.GetElapsedTime(handlerStarted).TotalMilliseconds);

                    if (state == MarisaPluginTaskState.CompletedTask) break;
                }
            });

            if (res.Outcome != OutcomeType.Failure)
            {
                Logger.Info(
                    "event=message_completed {0} duration_ms={1:F1}",
                    audit,
                    Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                return;
            }

            if (res.FinalException is TimeoutRejectedException)
            {
                message.Reply("Cancelled due to timeout (10min)");
                Logger.Error(
                    "event=message_timeout {0} duration_ms={1:F1}",
                    audit,
                    Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }
            else
            {
                Logger.Error(
                    "event=dispatch_failed {0} error_type={1} hresult={2} duration_ms={3:F1}",
                    audit,
                    res.FinalException?.GetType().FullName ?? "unknown",
                    res.FinalException?.HResult ?? 0,
                    Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }
        }
        catch (Exception e)
        {
            Logger.Error(
                "event=message_failed {0} error_type={1} hresult={2} duration_ms={3:F1}",
                audit,
                e.GetType().FullName ?? e.GetType().Name,
                e.HResult,
                Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
    }

    /// <summary>
    /// 从服务器拉取消息并更新接收队列
    /// </summary>
    protected abstract Task RecvMessage();

    /// <summary>
    /// 从接收队列接收消息并发送到服务器
    /// </summary>
    protected abstract Task SendMessage();

    /// <summary>
    /// 登录
    /// </summary>
    /// <returns></returns>
    public virtual Task Login()
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// 调用bot的默认实现
    /// </summary>
    public virtual async Task Invoke()
    {
        await Task.WhenAll(
            Task.WhenAll(pluginsAll.Select(p => p.BackgroundService(ShutdownToken))),
            RecvMessage(), SendMessage(), ProcMessage()
        );
    }

    public virtual void Stop()
    {
        if (_shutdown.IsCancellationRequested)
        {
            return;
        }

        _shutdown.Cancel();
    }
}
