using System.Text.RegularExpressions;
using Marisa.Plugin.Shared.DivingFish;

namespace Marisa.Plugin.DivingFish;

[MarisaPluginNoDoc]
[MarisaPlugin(PluginPriority.DivingFishConfirmation)]
[MarisaPluginCommand(MessageType.GroupMessage)]
public sealed class DivingFishConfirmation : MarisaPluginBase
{
    private static readonly Regex ConfirmationPattern = new(
        @"(?<![0-9A-Fa-f])(?<code>[0-9A-Fa-f]{32})(?![0-9A-Fa-f])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    [MarisaPluginCommand(MessageType.GroupMessage)]
    private static MarisaPluginTaskState Confirm(Message message)
    {
        var consumedConfirmations = ConfirmationPattern.Matches(message.Command.Trim().ToString())
            .Cast<Match>()
            .Select(match => match.Groups["code"].Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(code => DivingFishBindingConfirmation.Consume(
                code,
                message.Sender.Id,
                message.GroupInfo?.Id ?? 0))
            .Where(result => result.Status != DivingFishBindingConfirmation.ConsumeStatus.NotFound)
            .ToList();

        if (consumedConfirmations.Count == 0)
        {
            return MarisaPluginTaskState.NoResponse;
        }

        var successful = consumedConfirmations.FirstOrDefault(result => result.IsSuccess);
        if (!successful.IsSuccess)
        {
            if (consumedConfirmations.Any(result => result.Status is
                    DivingFishBindingConfirmation.ConsumeStatus.SenderMismatch or
                    DivingFishBindingConfirmation.ConsumeStatus.GroupMismatch))
            {
                message.Reply("确认码与当前 QQ 或群不匹配，已立即作废。请勿转发他人的水鱼授权链接或确认码。");
            }
            else
            {
                message.Reply("确认码已过期或已被新的绑定请求替代，请重新发起水鱼绑定。");
            }

            return MarisaPluginTaskState.CompletedTask;
        }

        var confirmation = successful.Entry!;
        var commit = DivingFishBindingService.Commit(
            confirmation.Qq,
            confirmation.Sub,
            confirmation.Username,
            confirmation.Scope,
            confirmation.Game);

        if (commit == DivingFishBindingCommitResult.SubjectAlreadyBound)
        {
            message.Reply("该水鱼账号已经绑定到另一个 QQ，本次确认已作废。如需换绑，请先联系管理员核验并解除旧绑定。");
            return MarisaPluginTaskState.CompletedTask;
        }

        var account = string.IsNullOrWhiteSpace(confirmation.Username) ? "已确认账号" : confirmation.Username;
        message.Reply($"DivingFish OAuth 绑定成功！（水鱼账号：{account}）");
        return MarisaPluginTaskState.CompletedTask;
    }
}
