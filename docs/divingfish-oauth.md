# 水鱼 OAuth 部署与安全说明

水鱼计划于 **2026-10-01 00:00（UTC+8）**退役基于 `Developer-Token` 的旧 `/dev/player/*` 接口。MarisaBot
的新接入使用 OAuth 授权码流程、PKCE 和群内一次性确认码，把发起命令的 QQ 与用户授权的水鱼账号建立本地绑定。
水鱼的迁移背景和接口变化以[官方迁移文档](https://maimai.diving-fish.com/manual/docs/developer/oauth-migration/)为准。

## 部署配置

先在水鱼开发者后台注册一个供当前 MarisaBot 实例独占使用的 OAuth 应用。不要在多个互不信任的部署之间共享
同一个 `clientSecret`，也不要把真实凭据提交到 Git、截图、群消息或日志中。

在 `Marisa.StartUp/config.yaml` 中配置：

```yaml
web:
  private: localhost:14311
  public: https://bot.example.com

divingFish:
  clientId: 水鱼分配的客户端ID
  clientSecret: 水鱼分配的客户端密钥
  redirectUri: https://bot.example.com/oauth/callback
  devToken:
```

- `clientId` 是 OAuth 应用标识。
- `clientSecret` 只能保存在 Bot 服务端。应限制 `config.yaml` 和数据库文件的操作系统访问权限。
- `redirectUri` 必须是公网可访问的 **HTTPS** 地址，并与水鱼开发者后台登记的值逐字一致；路径必须为
  `/oauth/callback`。
- Bot 只固定水鱼授权服务器根地址，其余 OAuth/OIDC 端点从官方 discovery 文档读取并做 HTTPS 同源校验。
- `web.public` 是 Bot 其他网页功能使用的对外地址。水鱼授权会直接发送官方 `auth.diving-fish.com`
  链接；反向代理必须把 `/oauth/callback` 转发到 `web.private` 所指向的 MarisaBot 进程。
- 除仅供本机调试外，不要把公网回调配置为 HTTP，也不要把 ASP.NET 监听端口直接暴露到公网。

配置后应从公网环境确认回调地址能够到达 Bot，并确保 OAuth 应用登记值与反向代理暴露的地址完全一致。

## 群内绑定流程

maimai 和 Chunithm 分别通过 `mai bind`、`chu bind` 发起绑定，并在列表中选择 `DivingFish`：

1. Bot 只采用消息事件中的真实发送者 QQ 和当前群号，不接受命令文本、`@`、引用或转发消息指定的代绑对象。
2. Bot 为本次请求生成 OAuth `state`、PKCE verifier/challenge 和短期授权链接，并把水鱼官方链接回复到原群。
3. 用户在水鱼官方页面登录并授权；水鱼只允许回调预先登记的 `/oauth/callback`。
4. callback 校验并独占 `state`，用授权码和 PKCE verifier 换取令牌；成功后消费 state，再生成一个短期、单次的
   一次性确认码。换码失败会释放短期租约供合法 callback 重试。
5. 用户必须由发起绑定的 QQ 在原群发送该确认码。确认成功后，Bot 才持久化
   `QQ -> 水鱼 sub`，并把该 QQ 的查分源切换为 `DivingFish`。日常查询按需用 sub 执行 OBO，
   不保存授权码流程签发的 refresh token。

授权链接和确认码都可能被群成员看到，因此不能把“链接未泄露”当作安全前提。确认处理必须全局识别确认码，并在
校验发送者和群号之前原子地消费首次提交；错误 QQ 或错误群的首次提交也应烧毁该码。否则攻击者可能复制受害者发在
群里的确认码，再由最初发起绑定的 QQ 重放。

授权链接不是安全凭据，也不能替代 `state`、PKCE 和一次性确认。不要把授权链接或确认码写入长期日志；反向代理也应
避免记录 callback 的完整 query string。

## 查询权限边界

OAuth Bearer/OBO token 只代表具有本地 `verified` 绑定的发送者本人：

| 查询对象 | 允许的数据路径 |
|---|---|
| 发送者本人 | 可以使用已确认的 `Subject=sub:...` Bearer 绑定，或严格受限的存量 `Subject=ref:...` OBO 绑定 |
| 用户名查询 | 只能调用水鱼公开查询接口；不得回落到发送者的 Bearer token |
| `@` 其他 QQ | 只能调用水鱼公开查询接口；不得使用目标或发送者的本地身份映射执行 OBO |
| 对方未公开成绩 | 返回未公开提示并结束；不得通过 OAuth 绕过 |

群命令中的 username、QQ、`@` 或引用内容都不能直接拼成 OAuth `subject`。只有完成一次性码确认的 sub 绑定，或
按下节规则从当前发送者 QQ 自动迁移的 verified ref 绑定，才可以进入 token cache 和私有成绩接口。

## 存量 ref 绑定迁移

旧设备码流程可能在水鱼侧留下
`ref:sha256("{client_id}:{QQ}") -> sub` 映射。为避免所有存量用户在升级当天同时重新授权，Bot 可以在严格受限的
条件下自动承接这类映射：

1. 只有“发送者查询自己”、且本地不存在 `verified` 水鱼绑定时，才计算
   `ref:sha256("{client_id}:{发送者QQ}")`。
2. Bot 使用官方 OBO grant 和该精确 ref 请求当前游戏所需 scope。不得从命令文本、用户名、`@`、引用或转发内容
   构造 OBO subject。
3. OBO 成功说明水鱼侧仍保存着该应用、该 QQ ref 对应的旧授权。Bot 将
   `Subject=ref:...`、`Status=verified` 持久化为本地存量绑定；此时 `Sub` 可以为空，后续仅通过
   该 ref 的 OBO token 查询发送者本人。
4. OBO 返回 consent required、旧授权已撤销或请求失败时，不尝试 username OBO，也不把其他用户的 token 当作
   回退；用户应通过 bind 完成新的网页授权。
5. 用户以后完成授权码、PKCE 和原群一次性码确认后，同一条绑定升级为 `Subject=sub:...`，并保存 callback 得到的
   sub。之后以新的 sub 绑定为准，不再依赖旧 ref。

禁止使用旧的 `subject=qq:<QQ>` 作为迁移或长期凭据，也禁止对用户名或 `@` 查询执行 OBO。自动迁移只承接水鱼侧
已经存在的精确 ref 映射，不会创建新的设备码授权。

需要注意：自动迁移继承旧设备码绑定当时的信任结果，并不能追溯证明当时点击授权页面的人确实控制该 QQ。如果管理员
怀疑旧映射曾遭转发钓鱼，应要求相关用户撤销旧授权并重新走网页授权和原群一次性码确认。新的网页确认是从 ref 升级到
sub 绑定、重新建立更强身份关联的推荐路径。

## Realm 数据升级

OAuth 绑定保存在 Realm 的 `DivingFishOAuthBind` 模型中；模型只保存 QQ、OBO subject/sub 与审计元数据，不保存
access token 或 refresh token。本变更将 `Marisa.Database/BotDbContext.cs` 中的 `SchemaVersion` 从 5 提高到 6。

升级前应备份 `databasePath` 指向的 Realm 文件，并确保只有 Bot 运行账户可以读取。新版本首次打开数据库后，不要再
用包含更低 Realm schema 版本的旧程序直接打开同一个数据库。多实例部署还需要共享且具备一致性保证的 pending、
proof 和 token 状态；当前基于进程内短期状态的部署应视为单实例，重启会使尚未完成的授权流程失效，用户需要重新发起。

早期实验分支 `feat/divingfish-oauth-clean` 曾把 schema 临时设为 7。该分支生成的数据库是开发期数据，不能直接由本
PR 的 v6 打开；试跑过实验分支的部署应恢复升级前的 v5 备份，或在停机备份后使用新的数据库文件，不能通过手工修改
Realm 版本号来“降级”。

## 撤销与解绑

完整解绑包含两个彼此独立的动作：

1. **本地解绑**：删除该 QQ 的 `DivingFishOAuthBind`，并清除对应的短期 access-token cache。
2. **水鱼侧撤销**：用户在水鱼账号的授权管理页面撤销 MarisaBot 应用授权，使水鱼侧令牌链失效。

仅在 `bind` 中改选其他查分器不等于解绑，也不会自动撤销水鱼授权。如果当前部署版本尚未提供专门的群内解绑命令，
用户应先在水鱼侧撤销应用授权，并联系 Bot 管理员在停机或确保无并发访问时，使用兼容 Realm 的管理方式删除本地绑定；
不要直接用文本编辑器修改 Realm 文件。管理员不得要求用户把 access token、授权码或一次性确认码发到群里。

## 安全边界与已知限制

- `clientSecret` 和 access token 都是服务端凭据；不得出现在群消息、异常响应、访问日志或代码仓库中。
- 群成员可以看到授权链接和确认码。安全性依赖原 QQ、原群校验，以及“首次提交即原子消费”，而不是依赖群聊保密。
- `state`、PKCE 和一次性码只能防协议内的冒用；如果用户主动把确认码私下交给攻击者，Bot 无法判断这次转交是否自愿。
- pending 授权和一次性证明是短期状态。Bot 重启、链接过期或 callback 命中其他实例后，用户需要重新执行 bind。
- 不应允许用户名或 `@` 查询回落到任何 Bearer token；这既是数据正确性要求，也是对水鱼隐私设置的尊重。
- 水鱼侧撤销后，下一次 OBO 应失败并要求重新绑定；不得把网络故障、限流等临时错误一律解释为用户未绑定。
- DevToken 兼容路径仅用于退役前过渡。**2026-10-01 起不得把它作为可用回退方案**。
