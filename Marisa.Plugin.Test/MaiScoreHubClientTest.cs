using System;
using System.Threading.Tasks;
using Flurl.Http.Testing;
using Marisa.Plugin.Shared.MaiMaiDx;
using NUnit.Framework;

namespace Marisa.Plugin.Test;

[TestFixture]
public class MaiScoreHubClientTest
{
    private const string FriendCode = "000000000000000";
    private const string BotFriendCode = "999999999999999";

    [Test]
    public async Task LoginRequest_AssignedBotBeforeRequestSent_IsNotReportedAsSent()
    {
        using var http = new HttpTest();
        http.RespondWithJson(new
        {
            jobId = "login-job",
            job = new
            {
                status = "queued",
                stage = "send_request",
                botUserFriendCode = BotFriendCode,
                friendRequestSentAt = (string?)null,
                deadlineAt = "2026-08-28T04:40:00.000Z"
            }
        });

        var result = await new MaiScoreHubClient().LoginRequestAsync(FriendCode);

        Assert.Multiple(() =>
        {
            Assert.That(result.BotFriendCode, Is.EqualTo(BotFriendCode));
            Assert.That(result.FriendRequestSent, Is.False);
            Assert.That(result.DeadlineAt, Is.EqualTo(DateTimeOffset.Parse("2026-08-28T04:40:00.000Z")));
        });
    }

    [Test]
    public async Task LoginRequest_WaitingForAcceptance_IsReportedAsSent()
    {
        using var http = new HttpTest();
        http.RespondWithJson(new
        {
            jobId = "login-job",
            job = new
            {
                status = "processing",
                stage = "wait_acceptance",
                botUserFriendCode = BotFriendCode,
                friendRequestSentAt = (string?)null,
                deadlineAt = "2026-08-28T04:40:00.000Z"
            }
        });

        var result = await new MaiScoreHubClient().LoginRequestAsync(FriendCode);

        Assert.That(result.FriendRequestSent, Is.True);
    }

    [Test]
    public async Task LoginStatus_DeadlineFailure_IsRecognized()
    {
        using var http = new HttpTest();
        http.RespondWithJson(new
        {
            status = "failed",
            job = new
            {
                status = "failed",
                stage = "send_request",
                error = "DXNet job deadline exceeded",
                deadlineAt = "2026-08-28T04:40:00.000Z"
            }
        });

        var result = await new MaiScoreHubClient().LoginStatusAsync("login-job");

        Assert.Multiple(() =>
        {
            Assert.That(result.DeadlineExceeded, Is.True);
            Assert.That(result.ErrorCode, Is.Null);
        });
    }

    [Test]
    public async Task CreateUpdateScoreJob_ParsesServerDeadline()
    {
        using var http = new HttpTest();
        http.RespondWithJson(new
        {
            jobId = "crawl-job",
            job = new
            {
                status = "queued",
                stage = "update_score",
                deadlineAt = "2026-08-28T04:55:00.000Z"
            }
        }, 201);

        var result = await new MaiScoreHubClient().CreateUpdateScoreJobAsync("jwt", "login-job");

        Assert.Multiple(() =>
        {
            Assert.That(result.JobId, Is.EqualTo("crawl-job"));
            Assert.That(result.DeadlineAt, Is.EqualTo(DateTimeOffset.Parse("2026-08-28T04:55:00.000Z")));
        });
    }

    [Test]
    public async Task DxNetJob_DeadlineFailure_IsRecognized()
    {
        using var http = new HttpTest();
        http.RespondWithJson(new
        {
            status = "failed",
            stage = "update_score",
            error = "DXNet job deadline exceeded",
            errorCode = "job_deadline_exceeded",
            deadlineAt = "2026-08-28T04:55:00.000Z"
        });

        var result = await new MaiScoreHubClient().GetJobAsync("jwt", "crawl-job");

        Assert.Multiple(() =>
        {
            Assert.That(result.DeadlineExceeded, Is.True);
            Assert.That(result.ErrorCode, Is.EqualTo("job_deadline_exceeded"));
            Assert.That(result.DeadlineAt, Is.EqualTo(DateTimeOffset.Parse("2026-08-28T04:55:00.000Z")));
        });
    }
}
