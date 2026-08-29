using System.Reflection;
using System.Text.RegularExpressions;
using System.Text.Json;
using Marisa.Backend.NapCat;
using Marisa.BotDriver.DI;
using Marisa.BotDriver.DI.Message;
using Marisa.BotDriver.Entity.Message;
using Marisa.BotDriver.Entity.MessageData;
using Marisa.BotDriver.Entity.MessageSender;
using Marisa.BotDriver.Plugin;
using Marisa.BotDriver.Plugin.Attributes;
using Marisa.BotDriver.Plugin.Trigger;
using Marisa.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NLog;
using NLog.Config;
using NLog.Targets;
using NUnit.Framework;

namespace Marisa.BotDriver.Test;

[NonParallelizable]
public class MessageAuditSecurityTest
{
    private const string Sentinel = "SECRET_SENTINEL";

    private string _tempRoot = null!;
    private MemoryTarget _memory = null!;
    private AuditTestDriver _driver = null!;

    [SetUp]
    public void SetUp()
    {
        _tempRoot = Path.Join(Path.GetTempPath(), "Marisa.BotDriver.Test", Guid.NewGuid().ToString("N"));
        ConfigurationManager.SetConfigFilePath(CreateTestConfig(_tempRoot));
        ResetDispatcherCaches();

        _memory = new MemoryTarget("AuditSecurityMemory")
        {
            Layout = "${message}|${all-event-properties}|${exception:format=tostring}"
        };
        var logging = new LoggingConfiguration();
        logging.AddRuleForAllLevels(_memory);
        LogManager.Configuration = logging;

        _driver = AuditTestDriver.Create();
        AuditPlugin.LastCommand = null;
    }

    [TearDown]
    public void TearDown()
    {
        _driver.Dispose();
        LogManager.Shutdown();
        ResetDispatcherCaches();

        if (Directory.Exists(_tempRoot)) Directory.Delete(_tempRoot, true);
    }

    [Test]
    public async Task Normal_Dispatch_Should_Preserve_Command_And_Omit_Body_From_Audit_Logs()
    {
        var message = _driver.CreateMessage($"audit ok {Sentinel}");

        await _driver.Process(message);
        LogManager.Flush();

        Assert.Multiple(() =>
        {
            Assert.That(AuditPlugin.LastCommand, Is.EqualTo($"ok {Sentinel}"));
            Assert.That(message.ToString(), Does.Not.Contain(Sentinel));
            Assert.That(_memory.Logs, Has.Some.Contains("event=message_received"));
            Assert.That(_memory.Logs, Has.Some.Contains("event=handler_completed"));
            Assert.That(_memory.Logs, Has.Some.Contains("text_length="));
            Assert.That(_memory.Logs, Has.None.Contains(Sentinel));
        });
    }

    [Test]
    public async Task Plugin_Exception_Should_Omit_Message_And_Exception_Text_From_Logs_And_Dump()
    {
        var message = _driver.CreateMessage($"audit throw {Sentinel}");

        await _driver.Process(message);
        LogManager.Flush();

        var dump = Directory.GetFiles(Path.Join(_tempRoot, "exceptions"), "*.json").Single();
        var dumpContent = File.ReadAllText(dump);
        Assert.Multiple(() =>
        {
            Assert.That(_memory.Logs, Has.Some.Contains("event=plugin_exception"));
            Assert.That(_memory.Logs, Has.None.Contains(Sentinel));
            Assert.That(dumpContent, Does.Contain("InvalidOperationException"));
            Assert.That(dumpContent, Does.Contain(message.AuditContext.CorrelationId));
            Assert.That(dumpContent, Does.Not.Contain(Sentinel));
        });
    }

    [Test]
    public async Task Timeout_Should_Omit_Message_Body_From_Error_Log()
    {
        var message = _driver.CreateMessage($"audit timeout {Sentinel}");

        await _driver.Process(message);
        LogManager.Flush();

        Assert.Multiple(() =>
        {
            Assert.That(_memory.Logs, Has.Some.Contains("event=message_timeout"));
            Assert.That(_memory.Logs, Has.None.Contains(Sentinel));
        });
    }

    [Test]
    public void NapCat_Endpoint_Log_Should_Drop_UserInfo_Query_And_Fragment()
    {
        var build = typeof(NapCatBackend).GetMethod("BuildEndpoint", BindingFlags.Static | BindingFlags.NonPublic)!;
        var forLog = typeof(NapCatBackend).GetMethod("EndpointForLog", BindingFlags.Static | BindingFlags.NonPublic)!;
        var endpoint = (Uri)build.Invoke(null, [$"ws://user:{Sentinel}@localhost:3001/onebot?access_token={Sentinel}#fragment"])!;
        var logged = (string)forLog.Invoke(null, [endpoint])!;

        Assert.Multiple(() =>
        {
            Assert.That(build.GetParameters(), Has.Length.EqualTo(1));
            Assert.That(endpoint.Query, Does.Contain(Sentinel));
            Assert.That(logged, Is.EqualTo("ws://localhost:3001/onebot"));
            Assert.That(logged, Does.Not.Contain(Sentinel));
        });
    }

    [Test]
    public void NapCat_Action_Exception_Should_Not_Include_Response_Body()
    {
        using var response = JsonDocument.Parse($$"""
            { "status": "failed", "retcode": 1200, "message": "{{Sentinel}}", "data": { "result": 110 } }
            """);
        var create = typeof(NapCatBackend).GetMethod("CreateActionException", BindingFlags.Static | BindingFlags.NonPublic)!;
        var exception = (Exception)create.Invoke(null, ["send_group_msg", response.RootElement])!;

        Assert.Multiple(() =>
        {
            Assert.That(exception.ToString(), Does.Contain("retcode 1200"));
            Assert.That(exception.ToString(), Does.Not.Contain(Sentinel));
        });
    }

    private static void ResetDispatcherCaches()
    {
        foreach (var fieldName in new[] { "_plugins", "_commands", "_subCommands" })
        {
            typeof(MessageDispatcher).GetField(fieldName, BindingFlags.Static | BindingFlags.NonPublic)!
                .SetValue(null, null);
        }
    }

    private static string CreateTestConfig(string tempRoot)
    {
        var sourceConfigPath = Path.Join(FindRepositoryRoot(), "Marisa.StartUp", "config.yaml");
        var escapedTempRoot = tempRoot.Replace('\\', '/');
        var config = File.ReadAllText(sourceConfigPath);
        config = Regex.Replace(config, @"^tempPath:\s*.*$", $"tempPath:     {escapedTempRoot}", RegexOptions.Multiline);
        config = Regex.Replace(config, @"^databasePath:\s*.*$", "databasePath: bot.db", RegexOptions.Multiline);
        var configPath = Path.Join(tempRoot, "config.yaml");

        Directory.CreateDirectory(tempRoot);
        File.WriteAllText(configPath, config);
        return configPath;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(Environment.CurrentDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Join(directory.FullName, "Marisa.sln"))) return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate repository root from current test directory.");
    }

    [MarisaPlugin]
    [MarisaPluginCommand("audit")]
    private sealed class AuditPlugin : MarisaPluginBase
    {
        public static string? LastCommand { get; set; }

        [MarisaPluginCommand]
        private static async Task<MarisaPluginTaskState> Handle(Message message)
        {
            LastCommand = message.Command.ToString();
            if (message.Command.Span.StartsWith("throw", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(Sentinel);
            }

            if (message.Command.Span.StartsWith("timeout", StringComparison.Ordinal))
            {
                await Task.Delay(TimeSpan.FromSeconds(5));
            }

            return MarisaPluginTaskState.CompletedTask;
        }
    }

    private sealed class AuditTestDriver(
        IServiceProvider serviceProvider,
        IEnumerable<MarisaPluginBase> plugins,
        DictionaryProvider dictionary,
        MessageSenderProvider sender,
        MessageQueueProvider queues,
        ServiceProvider owner)
        : BotDriver(serviceProvider, plugins, dictionary, sender, queues), IDisposable
    {
        protected override TimeSpan MessageProcessingTimeout => TimeSpan.FromMilliseconds(50);

        public static AuditTestDriver Create()
        {
            var services = Config([typeof(AuditPlugin)]);
            var owner = services.BuildServiceProvider();
            return new AuditTestDriver(
                owner,
                owner.GetServices<MarisaPluginBase>(),
                owner.GetRequiredService<DictionaryProvider>(),
                owner.GetRequiredService<MessageSenderProvider>(),
                owner.GetRequiredService<MessageQueueProvider>(),
                owner);
        }

        public Message CreateMessage(string text)
        {
            return new Message(
                new MessageChain(new MessageDataId(123, 0), new MessageDataText(text)),
                MessageSenderProvider)
            {
                Type = MessageType.GroupMessage,
                Sender = new SenderInfo(114514, "sensitive sender name"),
                GroupInfo = new GroupInfo(1919810, "sensitive group name", null)
            };
        }

        public Task Process(Message message) => ProcMessageStep(message);

        protected override Task RecvMessage() => Task.CompletedTask;

        protected override Task SendMessage() => Task.CompletedTask;

        public void Dispose() => owner.Dispose();
    }
}
