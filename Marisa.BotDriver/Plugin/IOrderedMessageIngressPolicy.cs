using Marisa.BotDriver.Entity.Message;

namespace Marisa.BotDriver.Plugin;

/// <summary>
///     Marks sensitive messages that must be handled in receive order instead of by the
///     driver's normal fire-and-forget dispatcher.
/// </summary>
public interface IOrderedMessageIngressPolicy
{
    bool Matches(Message message);
}
