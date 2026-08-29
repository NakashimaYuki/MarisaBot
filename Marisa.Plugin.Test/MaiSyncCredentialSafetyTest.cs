using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Flurl.Http;
using Flurl.Http.Testing;
using Marisa.BotDriver;
using Marisa.BotDriver.DI.Message;
using Marisa.BotDriver.Entity.Message;
using Marisa.BotDriver.Entity.MessageData;
using Marisa.BotDriver.Entity.MessageSender;
using Marisa.Configuration;
using Marisa.Database;
using Marisa.Database.Entity.Plugin.MaiMaiDx;
using NUnit.Framework;

namespace Marisa.Plugin.Test;

[NonParallelizable]
public class MaiSyncCredentialSafetyTest
{
    private const long Qq = 114514;
    private const string FriendCode = "123456789012345";
    private const string Sentinel = "SECRET_SENTINEL";
    private const string SettingsUrl = "https://maiscorehub.bakapiano.com/app/sync";

    private string _sourceConfigPath = null!;
    private string _tempRoot = null!;

    [SetUp]
    public void SetUp()
    {
        _sourceConfigPath = Path.Join(FindRepositoryRoot(), "Marisa.StartUp", "config.yaml");
        _tempRoot = Path.Join(Path.GetTempPath(), "Marisa.Plugin.Test", Guid.NewGuid().ToString("N"));
        ConfigurationManager.SetConfigFilePath(CreateTestConfig(_tempRoot));
        BotDbContext.EnsureCreated();
        ResetDispatcherCaches();
    }

    [TearDown]
    public void TearDown()
    {
        ResetDispatcherCaches();
        ConfigurationManager.SetConfigFilePath(_sourceConfigPath);
        if (Directory.Exists(_tempRoot)) Directory.Delete(_tempRoot, true);
    }

    [TestCase(null)]
    [TestCase(1919810)]
    public async Task Legacy_Token_Command_Should_Be_Rejected_Without_Http_Or_Realm_Changes(long? groupId)
    {
        using (var realm = BotDbContext.OpenRealm())
        {
            realm.Write(() => realm.AddWithAutoId(new MaiMaiDxBind(Qq, 0)
            {
                FriendCode = FriendCode,
                ServerName = "DivingFish"
            }));
        }

        using var http = new HttpTest();
        var driver = TestBackend.Create(typeof(MaiMaiDx.MaiMaiDx));
        await driver.SetMessage(Qq, groupId, $"mai 导 999999999999999 水鱼 {Sentinel}");
        driver.Finish();

        await driver.ProcAll();
        var replies = await driver.GetAllSend();

        using var check = BotDbContext.OpenRealm();
        var binding = check.All<MaiMaiDxBind>().Single(x => x.UId == Qq);
        var replyText = string.Join('\n', replies.Select(x => x.MessageChain.Text));
        Assert.Multiple(() =>
        {
            Assert.That(binding.FriendCode, Is.EqualTo(FriendCode));
            Assert.That(http.CallLog, Is.Empty);
            Assert.That(replyText, Does.Contain("不再接收聊天中的查分器凭据"));
            Assert.That(replyText, Does.Contain(SettingsUrl));
            Assert.That(replyText, Does.Not.Contain(Sentinel));
            Assert.That(replyText, Does.Not.Contain("999999999999999"));
        });
    }

    [Test]
    public async Task Missing_Prober_Tokens_Should_Stop_Before_Crawl()
    {
        using var http = LoginResponses(hasLxns: false, hasDivingFish: false);
        var (target, queue) = CreateReplyTarget();

        await InvokeRunSync(target);

        var replyText = DrainReplies(queue);
        Assert.Multiple(() =>
        {
            Assert.That(replyText, Does.Contain(SettingsUrl));
            Assert.That(replyText, Does.Contain("尚未配置查分器"));
            Assert.That(replyText, Does.Not.Contain("JWT_SENTINEL"));
            Assert.That(http.CallLog.Count, Is.EqualTo(3));
            Assert.That(http.CallLog, Has.None.Matches<FlurlCall>(x =>
                x.Request.Url.ToString().Contains("/me/dxnet-jobs", StringComparison.Ordinal)));
        });
    }

    [Test]
    public async Task Configured_Prober_Should_Crawl_Then_Export()
    {
        using var http = LoginResponses(hasLxns: true, hasDivingFish: false);
        http.RespondWithJson(new
        {
            jobId = "crawl-job",
            job = new { deadlineAt = DateTimeOffset.UtcNow.AddMinutes(5) }
        }, 201);
        http.RespondWithJson(new { status = "completed", stage = "update_score" });
        http.RespondWithJson(new
        {
            status = "completed",
            result = new { lxns = new { status = "success", exported = 2202, scores = 2202 } }
        });
        var (target, queue) = CreateReplyTarget();

        await InvokeRunSync(target);

        var calls = http.CallLog.Select(x => x.Request.Url.ToString()).ToList();
        var replyText = DrainReplies(queue);
        Assert.Multiple(() =>
        {
            Assert.That(calls.FindIndex(x => x.EndsWith("/me", StringComparison.Ordinal)),
                Is.LessThan(calls.FindIndex(x => x.EndsWith("/me/dxnet-jobs", StringComparison.Ordinal))));
            Assert.That(calls, Has.Some.EndsWith("/me/sync/latest/exports/lxns"));
            Assert.That(replyText, Does.Contain("落雪 ✅ 导入 2202/2202 条"));
            Assert.That(replyText, Does.Not.Contain("JWT_SENTINEL"));
        });
    }

    private static HttpTest LoginResponses(bool hasLxns, bool hasDivingFish)
    {
        var http = new HttpTest();
        http.RespondWithJson(new
        {
            jobId = "login-job",
            authToken = "fallback-jwt",
            deadlineAt = DateTimeOffset.UtcNow.AddMinutes(5)
        });
        http.RespondWithJson(new { status = "completed", token = "JWT_SENTINEL" });
        http.RespondWithJson(new
        {
            hasLxnsImportToken = hasLxns,
            hasDivingFishImportToken = hasDivingFish
        });
        return http;
    }

    private static async Task InvokeRunSync(MessageReplyTarget target)
    {
        var method = typeof(MaiMaiDx.MaiMaiDx).GetMethod("RunSync", BindingFlags.Static | BindingFlags.NonPublic)!;
        var task = (Task)method.Invoke(null, [target, FriendCode, (Func<TimeSpan, int>)(_ => 0)])!;
        await task;
    }

    private static (MessageReplyTarget Target, MessageQueueProvider Queue) CreateReplyTarget()
    {
        var queue = new MessageQueueProvider();
        var sender = new MessageSenderProvider(queue);
        var message = new Message(
            new MessageChain(new MessageDataId(1, 0), new MessageDataText("mai 导")),
            sender)
        {
            Sender = new SenderInfo(Qq, "tester"),
            Type = MessageType.FriendMessage
        };
        return (message.CaptureReplyTarget(), queue);
    }

    private static string DrainReplies(MessageQueueProvider queue)
    {
        var replies = new List<string>();
        while (queue.SendQueue.Reader.TryRead(out var reply)) replies.Add(reply.MessageChain.Text);
        return string.Join('\n', replies);
    }

    private static void ResetDispatcherCaches()
    {
        foreach (var fieldName in new[] { "_plugins", "_commands", "_subCommands" })
        {
            typeof(MessageDispatcher).GetField(fieldName, BindingFlags.Static | BindingFlags.NonPublic)!
                .SetValue(null, null);
        }
    }

    private string CreateTestConfig(string tempRoot)
    {
        var escapedTempRoot = tempRoot.Replace('\\', '/');
        var config = File.ReadAllText(_sourceConfigPath);
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
}
