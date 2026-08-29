using System.Text.RegularExpressions;
using System.Reflection;
using Marisa.BotDriver.DI.Message;
using Marisa.BotDriver.Entity.Message;
using Marisa.BotDriver.Entity.MessageData;
using Marisa.BotDriver.Entity.MessageSender;
using Marisa.BotDriver.Plugin;
using Marisa.Configuration;
using NUnit.Framework;

namespace Marisa.BotDriver.Test;

public class ExceptionDumpTest
{
    private string _configPath = null!;
    private string _tempRoot = null!;

    [SetUp]
    public void SetUp()
    {
        _tempRoot = Path.Join(Path.GetTempPath(), "Marisa.BotDriver.Test", Guid.NewGuid().ToString("N"));
        _configPath = CreateTestConfig(_tempRoot);
        ConfigurationManager.SetConfigFilePath(_configPath);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, true);
        }
    }

    [Test]
    public void Save_Should_Write_Safe_Exception_Metadata_Without_Messages()
    {
        const string sentinel = "SECRET_SENTINEL";
        var queue = new MessageQueueProvider();
        var sender = new MessageSenderProvider(queue);
        var message = new Message(new MessageChain(
            new MessageDataId(1, 0),
            new MessageDataText(sentinel)), sender)
        {
            Type = MessageType.FriendMessage,
            Sender = new SenderInfo(114514, "tester")
        };

        Exception exception;
        try
        {
            throw new InvalidOperationException(sentinel, new ArgumentException(sentinel));
        }
        catch (Exception caught)
        {
            exception = caught;
        }

        var path = ExceptionDump.Save(exception, message.AuditContext, "unit-test");

        Assert.That(path, Is.Not.Null);
        Assert.That(NormalizePath(path!), Does.StartWith(NormalizePath(Path.Join(_tempRoot, "exceptions"))));
        Assert.That(File.Exists(path!), Is.True);

        var content = File.ReadAllText(path!);
        Assert.Multiple(() =>
        {
            Assert.That(content, Does.Contain("InvalidOperationException"));
            Assert.That(content, Does.Contain("ArgumentException"));
            Assert.That(content, Does.Contain("unit-test"));
            Assert.That(content, Does.Contain(message.AuditContext.CorrelationId));
            Assert.That(content, Does.Not.Contain(sentinel));
            Assert.That(content, Does.Not.Contain("RelatedMessage"));
            Assert.That(content, Does.Not.Contain("Detail"));
        });

        static string NormalizePath(string value) =>
            Path.GetFullPath(value.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar));
    }

    [Test]
    public async Task ExceptionHandler_Should_Not_Send_Raw_Exception_Detail_To_User()
    {
        var queue = new MessageQueueProvider();
        var sender = new MessageSenderProvider(queue);
        var message = new Message(new MessageChain(
            new MessageDataId(1, 0),
            new MessageDataText("ping")), sender)
        {
            Type = MessageType.FriendMessage,
            Sender = new SenderInfo(114514, "tester")
        };

        await new MarisaPluginBase().ExceptionHandler(new InvalidOperationException("boom"), message);

        Assert.That(queue.SendQueue.Reader.TryRead(out var reply), Is.True);
        var text = ((MessageDataText)reply!.MessageChain.Messages.Single()).Text.ToString();

        Assert.Multiple(() =>
        {
            Assert.That(text, Does.StartWith("出现异常，已上报开发者（故障编号："));
            Assert.That(text, Does.Not.Contain("boom"));
            Assert.That(text, Does.Not.Contain("InvalidOperationException"));
        });
    }

    [Test]
    public async Task ExceptionHandler_Should_Not_Dump_MissingConfigurationException()
    {
        var queue = new MessageQueueProvider();
        var sender = new MessageSenderProvider(queue);
        var message = new Message(new MessageChain(
            new MessageDataId(1, 0),
            new MessageDataText("ping")), sender)
        {
            Type = MessageType.FriendMessage,
            Sender = new SenderInfo(114514, "tester")
        };

        await new MarisaPluginBase().ExceptionHandler(
            new TargetInvocationException(new MissingConfigurationException("divingFish.devToken")),
            message
        );

        Assert.That(queue.SendQueue.Reader.TryRead(out var reply), Is.True);
        var text = ((MessageDataText)reply!.MessageChain.Messages.Single()).Text.ToString();

        Assert.Multiple(() =>
        {
            Assert.That(text, Is.EqualTo("该功能未配置，请联系管理员检查配置项：divingFish.devToken"));
            Assert.That(Directory.Exists(Path.Join(_tempRoot, "exceptions")), Is.False);
        });
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
            if (File.Exists(Path.Join(directory.FullName, "Marisa.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate repository root from current test directory.");
    }
}
