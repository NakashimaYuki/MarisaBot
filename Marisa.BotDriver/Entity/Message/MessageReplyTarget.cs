using Marisa.BotDriver.DI.Message;
using Marisa.BotDriver.Entity.MessageData;

namespace Marisa.BotDriver.Entity.Message;

public sealed class MessageReplyTarget
{
    private readonly MessageSenderProvider _sender;

    internal MessageReplyTarget(Message message)
    {
        _sender = message.SenderProvider;
        Type = message.Type;
        Location = message.Location;
        SenderId = message.Sender.Id;
        CorrelationId = message.AuditContext.CorrelationId;
    }

    public MessageType Type { get; }

    public long Location { get; }

    public long SenderId { get; }

    public string CorrelationId { get; }

    public void Reply(string text, bool mentionSender = false)
    {
        if (mentionSender && Type == MessageType.GroupMessage)
        {
            _sender.Send(
                new MessageChain(new MessageDataAt(SenderId), new MessageDataText(" " + text)),
                Type,
                Location,
                null).Wait();
            return;
        }

        _sender.Send(text, Type, Location, null).Wait();
    }
}
