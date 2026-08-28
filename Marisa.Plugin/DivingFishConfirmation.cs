using System.Text.RegularExpressions;
using Marisa.Plugin.Shared.DivingFish;

namespace Marisa.Plugin;

/// <summary>
///     Globally routes DivingFish confirmation proofs before per-user dialogs. This is a
///     security boundary: a proof submitted by the wrong QQ/group must still be found and
///     burned before the intended initiator can copy and replay it.
/// </summary>
[MarisaPluginNoDoc]
[MarisaPlugin(PluginPriority.DivingFishConfirmation)]
[MarisaPluginCommand(MessageType.GroupMessage)]
public sealed class DivingFishConfirmation : MarisaPluginBase, IOrderedMessageIngressPolicy
{
    private static readonly Regex ProofPattern = new(
        @"(?<![0-9A-Fa-f])(?<code>[0-9A-Fa-f]{32})(?![0-9A-Fa-f])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public bool Matches(Message message)
    {
        return message.Type == MessageType.GroupMessage &&
               ProofPattern.IsMatch(message.Command.Trim().ToString());
    }

    [MarisaPluginCommand(MessageType.GroupMessage)]
    private static MarisaPluginTaskState Confirm(Message message)
    {
        // A message can contain more than one proof-shaped token. Burn every active proof,
        // not just the first match; otherwise an unrelated hash placed before the real code
        // could leave the disclosed real proof replayable by the original initiator.
        var consumedProofs = ProofPattern.Matches(message.Command.Trim().ToString())
            .Cast<Match>()
            .Select(match => match.Groups["code"].Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(code => DivingFishBindingProof.Consume(
                code,
                message.Sender.Id,
                message.GroupInfo?.Id ?? 0))
            .Where(result => result.Status != DivingFishBindingProof.ConsumeStatus.NotFound)
            .ToList();

        if (consumedProofs.Count == 0)
        {
            // It may simply contain unrelated hashes. Keep ingress ordering/redaction for all
            // candidates, but do not make random proof-shaped text produce a bot reply.
            return MarisaPluginTaskState.NoResponse;
        }

        var successful = consumedProofs.FirstOrDefault(result => result.IsSuccess);
        if (!successful.IsSuccess)
        {
            if (consumedProofs.Any(result => result.Status is
                    DivingFishBindingProof.ConsumeStatus.SenderMismatch or
                    DivingFishBindingProof.ConsumeStatus.GroupMismatch))
            {
                message.Reply("确认码与当前 QQ 或群不匹配，已立即作废。请勿转发他人的水鱼授权链接或确认码。");
            }
            else
            {
                message.Reply("确认码已过期或已被新的绑定请求替代，请重新发起水鱼绑定。");
            }

            return MarisaPluginTaskState.CompletedTask;
        }

        var proof = successful.Entry!;
        var commit = DivingFishBindingService.Commit(
            proof.Qq,
            proof.Sub,
            proof.Username,
            proof.Scope,
            proof.Game);

        if (commit == DivingFishBindingCommitResult.SubjectAlreadyBound)
        {
            message.Reply("该水鱼账号已经绑定到另一个 QQ，本次确认已作废。如需换绑，请先联系管理员核验并解除旧绑定。");
            return MarisaPluginTaskState.CompletedTask;
        }

        var account = string.IsNullOrWhiteSpace(proof.Username) ? "已确认账号" : proof.Username;
        message.Reply($"DivingFish OAuth 绑定成功！（水鱼账号：{account}）");
        return MarisaPluginTaskState.CompletedTask;
    }
}
