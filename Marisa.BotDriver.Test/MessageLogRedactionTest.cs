using Marisa.BotDriver.Entity.Message;
using Marisa.BotDriver.Entity.MessageData;
using NUnit.Framework;

namespace Marisa.BotDriver.Test;

public class MessageLogRedactionTest
{
    [Test]
    public void SensitiveText_Should_Redact_Log_View_And_Preserve_Send_Payload()
    {
        const string sentinel = "SECRET_SENTINEL";

        var message = MessageChain.FromSensitiveText(sentinel);
        var payload = ((MessageDataText)message.Messages.Single()).Text.ToString();

        Assert.Multiple(() =>
        {
            Assert.That(message.ToString(), Is.EqualTo("[REDACTED]"));
            Assert.That(message.ToString(), Does.Not.Contain(sentinel));
            Assert.That(payload, Is.EqualTo(sentinel));
        });
    }

    [Test]
    public void OrdinaryText_Should_Remain_Visible_In_Log_View()
    {
        const string content = "ordinary message";

        Assert.That(MessageChain.FromText(content).ToString(), Is.EqualTo(content));
    }
}
