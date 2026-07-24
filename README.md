# AIHub 最低价路由器

Windows 原生 WinForms 客户端。程序读取 `https://aihub.top/providers` 背后的公开监测接口，结合倍率、实时性能、任务时长和调用间隔，决定是否把选中的 API Key 切换到更合适的供应商分组。

## 功能

- 展示供应商倍率、当前状态、6 小时可用率、首 Token 延迟和检测时间。
- 读取账号可用分组、用户专属倍率和 `/keys` 下的 API Key。
- 支持单个或多个 Key 立即路由、定时自动路由。
- 支持短任务、1-4 小时、4 小时以上三档剩余工作量估计，并常态化保存选择；该设置只用于用户选择速度模式时的传统自适应估算。
- 用户选择速度模式时，根据当前分组最后调用时间在速度、均衡、经济偏好之间动态调整；用户选择经济或均衡时不受调用间隔覆盖。
- 以最新启用/可用状态、授权、黑名单、倍率、成功率和健康检查结果执行硬筛选；超过 30 分钟的有效性能证据仍可降权参与路由。
- 支持每隔指定时间检查专用 Key 当前分组，并在真实写入前用该 Key 预检尚无新鲜成功记录的目标分组。
- 支持邮箱和密码自动登录；session 临近过期时优先 refresh，refresh 被拒绝后自动重新登录。
- 保留 Token/Cookie/User-Agent 高级备用入口，认证向导可直接说明两种流程。
- 提供“垂直同步（双缓冲）”开关，减少表格滚动和定时刷新时的闪烁。
- 表格列分隔线调整右侧列：向左拖扩宽右侧列，向右拖缩窄右侧列。
- 常态化保存后，邮箱、密码、access/refresh token、Cookie 和 User-Agent 均由 Windows DPAPI 加密。
- 记住已勾选的 API Key，包括“明确一个都不选”的状态；只在第一次加载时默认选中首个 active Key。

## 认证

推荐直接在“连接与认证”中填写 AIHub 邮箱和密码。程序启动后按以下顺序取得 session：

1. access token 距离到期超过 2 分钟时直接复用。
2. access token 已到期或即将到期时，优先使用 refresh token 刷新。
3. 只有 refresh 被服务端以 400/401 拒绝时，才使用保存的邮箱和密码重新登录；网络错误不会触发重复登录。

业务接口返回 401 时只自动续期并重试一次，不会形成无限重试。需要验证码或两步验证的账号会提示先在网页完成交互验证。

### 高级备用认证

AIHub 当前的 `/api/v1/keys` 接口使用 Bearer Token，不是只使用 Cookie。登录网页后可在浏览器开发者工具的 Console 执行：

```js
copy(localStorage.getItem('auth_token')); localStorage.getItem('auth_token')
```

将结果填入“登录 Token”。Cookie 可以从浏览器网络请求的 `Cookie` 请求头复制，程序会一并发送；若 Cookie 中存在 `auth_token`，程序也会自动提取。

服务端支持会话 IP/User-Agent 绑定。若站点启用了该选项，还需要在同一浏览器 Console 执行并填入“User-Agent”：

```js
copy(navigator.userAgent); navigator.userAgent
```

`copy(...)` 本身返回 `undefined` 是正常的，表示复制命令没有返回值。上面的完整命令会在复制后再次显示实际内容。每执行一条命令就立即粘贴到对应字段，避免后一条覆盖前一条剪贴板内容。

邮箱和密码留空时，程序才把高级 Token/Cookie 作为主要认证。手动凭据过期且没有 refresh token 或账号密码时，程序会停止自动路由。

## 常态化保存

勾选“常态化保存认证”会立即保存当前配置，之后点击保存或修改 Key 勾选也会同步更新。邮箱、密码、access token、refresh token、到期时间、Cookie 和 User-Agent 使用 Windows DPAPI 加密，仅当前 Windows 用户可以解密；站点、平台、阈值、轮询间隔、路由模式、任务时长和 Key ID 选择保存在普通设置文件中。

配置目录为 `%LocalAppData%\AIHubRouter`。取消勾选“常态化保存认证”会立即删除加密认证文件，但保留非敏感界面设置。程序不会自动恢复“自动路由”开关，避免启动后未经确认修改 Key。

## 运行与发布

开发运行：

```powershell
dotnet restore .\AIHubRouter.sln --configfile .\NuGet.Config
dotnet run --project .\src\AIHubRouter.WinForms\AIHubRouter.WinForms.csproj --no-restore
```

生成 Windows x64 的压缩自包含版和轻量版：

```powershell
.\scripts\publish.ps1
```

## Routing Stability (v1.1.13)

The v1.1.13 routing rules are summarized below. The complete specification is in
[Routing Stability V2](docs/ROUTING_STABILITY_V2.md).

- **Forced recovery:** if the current group becomes unavailable, disabled,
  unauthorized, blocked, below the success threshold, invalidly priced, failed
  by a fresh health observation, or missing, the router immediately selects an
  eligible replacement. Stale but usable evidence alone is not forced recovery.
- **Policy switches:** every normal Economy, Balanced, or Speed change uses a
  30-second dwell, six already-completed evaluations, and two already-recorded
  observations of the same target. The switch occurs on the next same-target
  proposal, normally the third; `Initial`, `ForcedRecovery`, and
  `ManualOverride` bypass these guards. A pre-hysteresis `route-state.json`
  without the baseline timestamp may allow one migration switch.
- **Mode isolation:** Economy always selects the strict lowest price. Balanced
  always uses its explicit output budget (default 1,000), 26.73-second hard
  deadline, and user soft tolerance. Only user-selected Speed is adjusted by
  the last-call interval and may resolve to Speed, Balanced, or Cost.
- **Conservative evidence:** the 30-minute platform/group window uses numeric
  P50 values, P90 first-token latency, and P25 output rate, while the latest
  `Enabled`, `Available`, and `CheckedAt` remain current state. Older usable
  evidence stays eligible with reduced positive speed/reliability benefit.
- **Health validation:** the scheduled selected-Key check records current-group
  success or failure without moving the Key. A real cycle that will write a
  business Key preflights an unconfirmed target, restores the health Key before
  any business write, and re-evaluates after a recoverable target failure.
- **Response safety:** JSON, `+json`, and SSE application errors cannot become
  probe success. Error envelope fields win over payloads, and diagnostics do
  not echo response bodies or credential material.

The automated core tests model the following cases: immediate recovery from an
invalid group, all-mode policy hysteresis and third-proposal stability,
countdown-free Balanced Deadline routing, 30-minute P50/P90/P25 aggregation,
stale-evidence weighting, scheduled health evidence, target-preflight restore
and fallback safety, HTTP-200/SSE business errors, and credential-free audit
output. Run them with:

```powershell
dotnet run --project .\tests\AIHubRouter.Core.Tests\AIHubRouter.Core.Tests.csproj -c Release
```

发布脚本会先扫描生产源码，再在临时 staging 目录生成并扫描未压缩程序集和两个 EXE。扫描会阻止凭据形态的 JWT、Bearer/API Key、Cookie、邮箱和本机用户路径进入官方包；候选目录最终只允许包含 `AIHubRouter.exe`。所有检查通过后才替换正式目录，失败时保留上一次已验证版本并清理 staging。

发布脚本会生成两个版本：

- `artifacts\AIHubRouter-win-x64\AIHubRouter.exe`：约 49 MiB 的压缩自包含版，目标电脑无需安装 .NET。
- `artifacts\AIHubRouter-win-x64-lite\AIHubRouter.exe`：约 0.25 MiB 的轻量版，目标电脑需要已安装 .NET 10 Desktop Runtime x64。

WinForms 不支持安全的程序集裁剪，因此不启用 `PublishTrimmed`。自包含版压缩会略微增加首次启动的解压时间，但不把运行时文件释放到项目目录。

## 路由规则

完整的模式、Deadline、调用间隔、切换罚金和中位数指标说明见 [路由算法模式](docs/ALGORITHM_MODES.md)。

程序比较 `/providers` 返回的供应商分组数据，然后通过 `PUT /api/v1/keys/{id}` 更新 Key 的 `group_id`；它不会修改请求中的模型名称。用户专属分组倍率存在时，以专属倍率作为实际价格。候选必须通过最新启用/可用状态、账号授权、黑名单、倍率有效性和可调的 6 小时可用率阈值等硬条件，且不存在新鲜健康失败，并至少具有检测时间、新鲜健康成功、首 Token 延迟或输出速度之一作为可用证据。超过 30 分钟的证据不会被硬排除，而是只降低正向速度和可靠性收益。

仅用户选择 Speed 时保留的传统自适应分支使用以下常量：输入价格 `$5 / 1M tokens`、输出价格 `$30 / 1M tokens`、上下文未命中罚金 `300,000` 输入 tokens。该分支的任务时长对应以下剩余输出估计：

- 短任务：悲观 `0`，Cost 乐观 `156,960`，期望完成时间 1 小时。
- 1-4 小时：悲观 `156,960`，Cost 乐观 `627,840`，期望完成时间 2 小时。
- 4 小时以上：悲观 `627,840`，Cost 乐观 `3,767,040`（24 小时上限），期望完成时间 6 小时。

供应商接口可返回 ISO-8601 格式的 `lastCallEndedAt`，程序也兼容 `lastCallAt`。调用间隔覆盖只适用于用户选择的 Speed：不超过 15 秒时仍为 Speed，大于 15 且不超过 30 秒时降为 Balanced，超过 30 秒时降为 Cost；字段尚未返回或时间无效时仍为 Speed。用户选择的 Economy 始终是严格最低价，Balanced 始终走 Deadline。

Economy 选择严格最低有效倍率；Balanced 使用 `P90(TTFT) + 输出预算 / P25(输出速度)` 判断 26.73 秒硬 Deadline 和用户软容忍度；用户选择 Speed 时，传统自适应规则仍要求生成速度提升超过 20% 且涨价不超过 10%，或整体快超过 30 秒且不涨价。候选已有 `1..19` 个本地性能样本时，不能仅凭速度收益切换；达到 20 个样本后才使用上述速度条件。所有正常策略切换（包括 Economy 降价、Balanced Deadline/最快回退）都经过 30 秒、6 次已完成评估和第三次同目标提案门槛；旧版 `route-state.json` 缺少防抖基线时仅允许一次迁移切换。初始路由、当前分组硬失效恢复和手动强制分组不受阻塞。检查在手动刷新、模拟、立即路由或自动轮询取得新供应商数据时执行。

站点接口和字段可能随部署版本变化。程序依据 Sub2API 当前公开接口实现，接口不兼容时会显示错误并停止写入。

## 下游贡献与支持范围

本项目仍然只维护 Windows 原生 WinForms。加权路由模式（经济/均衡/速度）、可解释决策、账户缓存、路由状态、模拟运行、无凭据审计日志和原生主题行为参考并适配自 [OnRightPath/AIHubRouter](https://github.com/OnRightPath/AIHubRouter)。下游项目的 Linux、Avalonia、CLI、systemd 和跨平台发布内容不属于本仓库的支持范围。

供应商警告字段选择性兼容下游 v1.0.2/v1.0.3；本项目仍以 Economy 为默认模式，保留 95/5、80/20、35/65 三档候选排序权重和可调的 6 小时可用率阈值。这些权重只影响普通推荐分数；实际切换由各模式规则、硬门槛和 Policy 防抖共同决定。
