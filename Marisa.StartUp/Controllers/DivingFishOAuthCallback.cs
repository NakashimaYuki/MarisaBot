using Marisa.Plugin.Shared.DivingFish;
using Microsoft.AspNetCore.Mvc;
using NLog;

namespace Marisa.StartUp.Controllers;

/// <summary>
///     OAuth callback. A successful callback only stages an identity proof; the binding
///     is committed after the original QQ submits that proof in the original group.
/// </summary>
[ApiController]
public class DivingFishOAuthCallback : Controller
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    [HttpGet("/oauth/callback")]
    public async Task<IActionResult> Callback(
        [FromQuery] string? code,
        [FromQuery] string? state,
        [FromQuery] string? error)
    {
        SetSecurityHeaders();

        // An OAuth error callback is still correlated with a pending request. Never act on
        // an unauthenticated error parameter before validating state.
        if (string.IsNullOrWhiteSpace(state) || state.Length > 256)
        {
            return Html("无效回调", "缺少有效的 state 参数。");
        }

        var acquire = DivingFishPendingAuth.AcquireForCallback(state);
        if (!acquire.IsAcquired)
        {
            var message = acquire.Status == DivingFishPendingAuth.AcquireStatus.InProgress
                ? "该授权回调正在处理中，请稍后刷新；若仍未完成，请在群内重新发起绑定。"
                : "该授权请求已过期、已被使用或已被新的绑定请求替代，请在群内重新发起绑定。";
            return Html("回调不可用", message);
        }

        var pending = acquire.Entry!;
        if (!string.IsNullOrWhiteSpace(error))
        {
            DivingFishPendingAuth.Release(pending);
            return Html("授权未完成", "水鱼账号授权被取消或拒绝，请返回原群重试或重新发起绑定。");
        }

        if (string.IsNullOrWhiteSpace(code) || code.Length > 4096)
        {
            DivingFishPendingAuth.Release(pending);
            return Html("无效回调", "缺少有效的授权码，请返回原群重新发起绑定。");
        }

        string sub;
        string username;
        string actualScopes;
        try
        {
            var authorization = await DivingFishOAuth.ExchangeAuthCode(code, pending.CodeVerifier, pending.Game);
            sub = authorization.Sub;
            username = authorization.Username;
            actualScopes = authorization.Token.Scope;
        }
        catch (Exception exception)
        {
            // Do not log the exception object: transport exceptions may retain the token
            // request body (authorization code / client secret).
            Logger.Warn("DivingFish OAuth callback exchange failed: {0}", exception.GetType().Name);
            DivingFishPendingAuth.Release(pending);
            return Html("授权处理失败", "授权未能完成，请返回原群重新发起绑定；若持续失败请联系机器人管理员。");
        }

        var proofCode = DivingFishBindingProof.Issue(pending, sub, username, actualScopes);
        if (proofCode == null)
        {
            return Html("授权已失效", "本次请求已被新的绑定请求替代，请返回原群重新发起绑定。");
        }

        var displayName = string.IsNullOrWhiteSpace(username) ? "（未提供展示名）" : username;
        var html = $@"<!DOCTYPE html>
<html lang=""zh-CN""><head><meta charset=""utf-8""><meta name=""viewport"" content=""width=device-width,initial-scale=1""><title>水鱼绑定确认</title></head>
<body style=""font-family:sans-serif;max-width:640px;margin:40px auto;line-height:1.8;padding:0 16px""><main>
<h2>水鱼账号绑定确认</h2>
<p>网页授权成功。水鱼账号：<b>{EscapeHtml(displayName)}</b></p>
<p>绑定目标：QQ <b>{pending.Qq}</b>（群 <b>{pending.GroupId}</b>）</p>
<p>请由上述 QQ 在上述群内发送：</p>
<p style=""font-size:20px;overflow-wrap:anywhere;background:#f0f0f0;padding:12px;border-radius:6px""><code>水鱼确认 {proofCode}</code></p>
<p style=""color:#b00020""><b>不要把确认码私发给任何人。</b>确认码 5 分钟内有效，仅能提交一次；错误 QQ 或错误群提交也会立即使其失效。</p>
</main></body></html>";

        return Content(html, "text/html; charset=utf-8");
    }

    private IActionResult Html(string title, string body)
    {
        var html = $"<!DOCTYPE html><html lang=\"zh-CN\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><title>{EscapeHtml(title)}</title></head>" +
                   $"<body><main><h2>{EscapeHtml(title)}</h2><p>{EscapeHtml(body)}</p></main></body></html>";
        return Content(html, "text/html; charset=utf-8");
    }

    private void SetSecurityHeaders()
    {
        Response.Headers["Cache-Control"] = "no-store, max-age=0";
        Response.Headers["Pragma"] = "no-cache";
        Response.Headers["Referrer-Policy"] = "no-referrer";
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        Response.Headers["Content-Security-Policy"] =
            "default-src 'none'; style-src 'unsafe-inline'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";
    }

    private static string EscapeHtml(string? value) =>
        System.Net.WebUtility.HtmlEncode(value ?? "");
}
