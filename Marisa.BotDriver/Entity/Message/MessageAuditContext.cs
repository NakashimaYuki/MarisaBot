using System.Security.Cryptography;
using System.Text;
using Marisa.BotDriver.Entity.MessageData;

namespace Marisa.BotDriver.Entity.Message;

public sealed record MessageAuditContext(
    string CorrelationId,
    string MessageRef,
    MessageType MessageType,
    string LocationKind,
    string SenderRef,
    string LocationRef,
    string SegmentTypes,
    int TextLength)
{
    private static readonly byte[] PseudonymKey = RandomNumberGenerator.GetBytes(32);

    public static MessageAuditContext FromMessage(Message message)
    {
        ArgumentNullException.ThrowIfNull(message);

        var chain = message.MessageChain;
        var messageId = chain?.Messages.OfType<MessageDataId>().FirstOrDefault()?.Id;
        var locationId = message.Type == MessageType.GroupMessage
            ? message.GroupInfo?.Id
            : message.Sender.Id;

        return new MessageAuditContext(
            Convert.ToHexString(RandomNumberGenerator.GetBytes(8)),
            messageId is null ? "none" : Pseudonymize("message", messageId.Value),
            message.Type,
            LocationKindOf(message.Type),
            Pseudonymize("sender", message.Sender.Id),
            locationId is null ? "none" : Pseudonymize($"location:{message.Type}", locationId.Value),
            SummarizeSegments(chain),
            chain?.Messages.OfType<MessageDataText>().Sum(x => x.Text.Length) ?? 0);
    }

    public static string Pseudonymize(string domain, long value)
    {
        using var hmac = new HMACSHA256(PseudonymKey);
        var digest = hmac.ComputeHash(Encoding.UTF8.GetBytes($"{domain}:{value}"));
        return Convert.ToHexString(digest.AsSpan(0, 8));
    }

    public static string SummarizeSegments(MessageChain? chain)
    {
        if (chain is null || chain.Messages.Count == 0) return "none";

        return string.Join(',', chain.Messages
            .GroupBy(x => x.Type)
            .OrderBy(x => x.Key)
            .Select(x => $"{x.Key}:{x.Count()}"));
    }

    public override string ToString() =>
        $"correlation={CorrelationId} message_ref={MessageRef} type={MessageType} " +
        $"location_kind={LocationKind} sender_ref={SenderRef} location_ref={LocationRef} " +
        $"segments={SegmentTypes} text_length={TextLength}";

    private static string LocationKindOf(MessageType type) => type switch
    {
        MessageType.GroupMessage => "group",
        MessageType.TempMessage => "temp",
        MessageType.FriendMessage or MessageType.StrangerMessage => "private",
        _ => "unknown"
    };
}
