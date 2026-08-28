using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Marisa.Configuration;
using Marisa.BotDriver.DI.Message;
using Marisa.BotDriver.Entity.Message;
using Marisa.BotDriver.Entity.MessageData;
using Marisa.BotDriver.Entity.MessageSender;
using Marisa.Plugin;
using Marisa.Plugin.Shared;
using Marisa.Plugin.Shared.DivingFish;
using NUnit.Framework;

namespace Marisa.Plugin.Test;

[TestFixture]
[NonParallelizable]
[Category("DivingFishOAuth")]
public class DivingFishOAuthSecurityTest
{
    private const int ConcurrentAttemptCount = 32;

    private static long _identitySeed = 8_000_000_000;

    private string _sourceConfigPath = null!;
    private string _tempRoot = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _sourceConfigPath = Path.Join(
            Directory.GetParent(Environment.CurrentDirectory)!.Parent!.Parent!.Parent!.ToString(),
            "Marisa.StartUp", "config.yaml");
        _tempRoot = Path.Combine(
            Path.GetTempPath(),
            "Marisa.Plugin.Test",
            nameof(DivingFishOAuthSecurityTest),
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);

        var escapedTempRoot = _tempRoot.Replace("\\", "\\\\");
        var configPath = Path.Combine(_tempRoot, "config.yaml");
        File.WriteAllText(configPath, $$"""
            tempPath: "{{escapedTempRoot}}"
            resourceRoot: ""
            databasePath: "oauth-security-test.db"
            divingFish:
              clientId: "test-client-id"
              clientSecret: "test-client-secret"
              redirectUri: "https://bot.example.test/oauth/callback"
            """);

        ConfigurationManager.SetConfigFilePath(configPath);
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        ConfigurationManager.SetConfigFilePath(_sourceConfigPath);
        if (Directory.Exists(_tempRoot)) Directory.Delete(_tempRoot, true);
    }

    [TestCase("maimai", "prober.records.read")]
    [TestCase("chunithm", "chunithm.records.read")]
    public void ScopeOf_KnownGame_ReturnsExactScope(string game, string expected)
    {
        Assert.That(DivingFishOAuth.ScopeOf(game), Is.EqualTo(expected));
    }

    [Test]
    public void ScopeOf_UnknownGame_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => DivingFishOAuth.ScopeOf("unknown"));
    }

    [Test]
    public void SubjectRef_SameClientAndExternalId_IsStableLowercaseSha256()
    {
        const string expected = "7be34ed48f3de4511cfb3987c08091ea28d59cc5ba0695bce6a60c40dff1fa75";

        var first = DivingFishOAuth.SubjectRef("123456789");
        var second = DivingFishOAuth.SubjectRef("123456789");

        Assert.Multiple(() =>
        {
            Assert.That(first, Is.EqualTo(expected));
            Assert.That(second, Is.EqualTo(expected));
            Assert.That(first, Does.Match("^[0-9a-f]{64}$"));
            Assert.That(DivingFishOAuth.SubjectRef("123456788"), Is.Not.EqualTo(first));
        });
    }

    [Test]
    public void ConfirmationCandidate_IsGloballyMatchedAndRedactable()
    {
        const string code = "00112233445566778899AABBCCDDEEFF";
        var message = new Message(null!, new MessageDataText($"请帮我确认：水鱼确认 {code}。".AsMemory()))
        {
            Type = MessageType.GroupMessage,
            GroupInfo = new GroupInfo(100, "test", "member"),
            Sender = new SenderInfo(200, "tester")
        };

        var policy = new DivingFishConfirmation();
        Assert.That(policy.Matches(message), Is.True);

        message.RedactFromLogs = true;
        Assert.Multiple(() =>
        {
            Assert.That(message.ToString(), Does.Contain("[REDACTED]"));
            Assert.That(message.ToString(), Does.Not.Contain(code));
        });
    }

    [Test]
    public void ConfirmationHandler_RunsBeforeBlacklist()
    {
        Assert.That(PluginPriority.DivingFishConfirmation, Is.GreaterThan(PluginPriority.BlackList),
            "错误发送者即使在黑名单中，确认码也必须先被全局烧毁");
    }

    [Test]
    public void ConfirmationHandler_DecoyBeforeRealCode_BurnsEveryActiveProof()
    {
        var qq = NextIdentity();
        const long groupId = 105;
        var realCode = IssueProof(qq, groupId, "maimai");
        var decoy = realCode.Equals(new string('F', 32), StringComparison.OrdinalIgnoreCase)
            ? new string('0', 32)
            : new string('F', 32);

        var queue = new MessageQueueProvider();
        var sender = new MessageSenderProvider(queue);
        var message = new Message(
            sender,
            new MessageDataId(1, 0),
            new MessageDataText($"无关摘要 {decoy}，水鱼确认 {realCode}"))
        {
            Type = MessageType.GroupMessage,
            GroupInfo = new GroupInfo(groupId, "test", "member"),
            Sender = new SenderInfo(qq + 1, "wrong-sender")
        };

        var confirm = typeof(DivingFishConfirmation).GetMethod(
            "Confirm",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.That(confirm, Is.Not.Null);
        _ = confirm!.Invoke(null, [message]);

        Assert.That(DivingFishBindingProof.Consume(realCode, qq, groupId).Status,
            Is.EqualTo(DivingFishBindingProof.ConsumeStatus.NotFound),
            "前置无关 32 位摘要不得阻止真实确认码被全局烧毁");
    }

    [Test]
    public async Task PendingAuth_ConcurrentAcquire_OnlyOneAttemptSucceeds()
    {
        var qq = NextIdentity();
        var pending = DivingFishPendingAuth.Begin(qq, 100, "maimai");

        var results = await RunConcurrently(() => DivingFishPendingAuth.AcquireForCallback(pending.State));
        var acquired = results.Where(x => x.IsAcquired).ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(acquired, Has.Length.EqualTo(1));
            Assert.That(results.Count(x => x.Status == DivingFishPendingAuth.AcquireStatus.InProgress),
                Is.EqualTo(ConcurrentAttemptCount - 1));
        });

        var code = DivingFishBindingProof.Issue(
            acquired[0].Entry!, "sub", "tester", DivingFishOAuth.ScopeOf("maimai"));
        Assert.That(code, Is.Not.Null);
        Assert.That(DivingFishBindingProof.Consume(code!, qq, 100).Status,
            Is.EqualTo(DivingFishBindingProof.ConsumeStatus.Success));
    }

    [Test]
    public async Task BindingProof_ConcurrentConsume_OnlyOneAttemptSucceeds()
    {
        var qq = NextIdentity();
        const long groupId = 101;
        var code = IssueProof(qq, groupId, "maimai");

        var results = await RunConcurrently(() => DivingFishBindingProof.Consume(code, qq, groupId));

        Assert.Multiple(() =>
        {
            Assert.That(results.Count(x => x.IsSuccess), Is.EqualTo(1));
            Assert.That(results.Count(x => x.Status == DivingFishBindingProof.ConsumeStatus.NotFound),
                Is.EqualTo(ConcurrentAttemptCount - 1));
        });
    }

    [Test]
    public void BindingProof_WrongQqAttempt_BurnsCode()
    {
        var qq = NextIdentity();
        const long groupId = 102;
        var code = IssueProof(qq, groupId, "maimai");

        var wrongAttempt = DivingFishBindingProof.Consume(code, qq + 1, groupId);
        var replayByOwner = DivingFishBindingProof.Consume(code, qq, groupId);

        Assert.Multiple(() =>
        {
            Assert.That(wrongAttempt.Status, Is.EqualTo(DivingFishBindingProof.ConsumeStatus.SenderMismatch));
            Assert.That(wrongAttempt.Entry, Is.Null);
            Assert.That(replayByOwner.Status, Is.EqualTo(DivingFishBindingProof.ConsumeStatus.NotFound),
                "身份不匹配的首次尝试必须烧毁确认码");
            Assert.That(replayByOwner.Entry, Is.Null);
        });
    }

    [Test]
    public void BindingProof_WrongGroupAttempt_BurnsCode()
    {
        var qq = NextIdentity();
        const long groupId = 103;
        var code = IssueProof(qq, groupId, "maimai");

        var wrongAttempt = DivingFishBindingProof.Consume(code, qq, groupId + 1);
        var replayInOriginalGroup = DivingFishBindingProof.Consume(code, qq, groupId);

        Assert.Multiple(() =>
        {
            Assert.That(wrongAttempt.Status, Is.EqualTo(DivingFishBindingProof.ConsumeStatus.GroupMismatch));
            Assert.That(wrongAttempt.Entry, Is.Null);
            Assert.That(replayInOriginalGroup.Status, Is.EqualTo(DivingFishBindingProof.ConsumeStatus.NotFound),
                "群不匹配的首次尝试必须烧毁确认码");
            Assert.That(replayInOriginalGroup.Entry, Is.Null);
        });
    }

    [Test]
    public void BindingProof_NewBeginRejectsOlderGenerationAcrossGroupsAndGames()
    {
        var qq = NextIdentity();
        const long oldGroupId = 104;
        const long newGroupId = 204;

        var staleStart = DivingFishPendingAuth.Begin(qq, oldGroupId, "maimai");
        var staleAcquire = DivingFishPendingAuth.AcquireForCallback(staleStart.State);
        Assert.That(staleAcquire.Status, Is.EqualTo(DivingFishPendingAuth.AcquireStatus.Acquired));
        Assert.That(staleAcquire.Entry, Is.Not.Null);

        var currentStart = DivingFishPendingAuth.Begin(qq, newGroupId, "chunithm");
        var staleCode = DivingFishBindingProof.Issue(
            staleAcquire.Entry!, $"sub-{qq}", "tester", DivingFishOAuth.ScopeOf("maimai"));

        var currentAcquire = DivingFishPendingAuth.AcquireForCallback(currentStart.State);
        Assert.That(currentAcquire.Status, Is.EqualTo(DivingFishPendingAuth.AcquireStatus.Acquired));
        Assert.That(currentAcquire.Entry, Is.Not.Null);
        var currentCode = DivingFishBindingProof.Issue(
            currentAcquire.Entry!, $"sub-{qq}", "tester", DivingFishOAuth.ScopeOf("chunithm"));
        Assert.That(currentCode, Is.Not.Null);
        var currentAttempt = DivingFishBindingProof.Consume(currentCode!, qq, newGroupId);

        Assert.Multiple(() =>
        {
            Assert.That(staleCode, Is.Null,
                "同一 QQ 的新 Begin 必须拒绝任意群、任意游戏的旧 generation 签发 proof");
            Assert.That(currentAttempt.Status, Is.EqualTo(DivingFishBindingProof.ConsumeStatus.Success));
            Assert.That(currentAttempt.Entry, Is.Not.Null);
        });
    }

    private static string IssueProof(long qq, long groupId, string game)
    {
        var start = DivingFishPendingAuth.Begin(qq, groupId, game);
        var acquired = DivingFishPendingAuth.AcquireForCallback(start.State);
        Assert.That(acquired.Status, Is.EqualTo(DivingFishPendingAuth.AcquireStatus.Acquired));
        Assert.That(acquired.Entry, Is.Not.Null);

        var code = DivingFishBindingProof.Issue(
            acquired.Entry!, $"sub-{qq}", "tester", DivingFishOAuth.ScopeOf(game));
        Assert.That(code, Is.Not.Null);
        return code!;
    }

    private static long NextIdentity()
    {
        return Interlocked.Increment(ref _identitySeed);
    }

    private static async Task<T[]> RunConcurrently<T>(Func<T> action)
    {
        using var ready = new CountdownEvent(ConcurrentAttemptCount);
        using var start = new ManualResetEventSlim(false);

        var tasks = Enumerable.Range(0, ConcurrentAttemptCount)
            .Select(_ => Task.Factory.StartNew(() =>
                {
                    ready.Signal();
                    start.Wait();
                    return action();
                },
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default))
            .ToArray();

        var allWorkersReady = ready.Wait(TimeSpan.FromSeconds(20));
        start.Set();
        var results = await Task.WhenAll(tasks);
        Assert.That(allWorkersReady, Is.True, "并发测试 worker 启动超时");
        return results;
    }
}
