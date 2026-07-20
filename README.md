# AIHub 最低价路由器

Windows 原生 WinForms 客户端。程序读取 `https://aihub.top/providers` 背后的公开监测接口，并把选中的 API Key 切换到当前符合条件且倍率最低的供应商分组。

## 功能

- 展示供应商倍率、当前状态、6 小时可用率、首 Token 延迟和检测时间。
- 读取账号可用分组、用户专属倍率和 `/keys` 下的 API Key。
- 支持单个或多个 Key 立即路由、定时自动路由。
- 排除当前异常、监测超过 15 分钟、低于可用率阈值、账号无权限的分组。
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

勾选“常态化保存认证”会立即保存当前配置，之后点击保存或修改 Key 勾选也会同步更新。邮箱、密码、access token、refresh token、到期时间、Cookie 和 User-Agent 使用 Windows DPAPI 加密，仅当前 Windows 用户可以解密；站点、平台、阈值、轮询间隔和 Key ID 选择保存在普通设置文件中。

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

发布脚本会先扫描生产源码，再在临时 staging 目录生成并扫描未压缩程序集和两个 EXE。扫描会阻止凭据形态的 JWT、Bearer/API Key、Cookie、邮箱和本机用户路径进入官方包；候选目录最终只允许包含 `AIHubRouter.exe`。所有检查通过后才替换正式目录，失败时保留上一次已验证版本并清理 staging。

发布脚本会生成两个版本：

- `artifacts\AIHubRouter-win-x64\AIHubRouter.exe`：约 49 MiB 的压缩自包含版，目标电脑无需安装 .NET。
- `artifacts\AIHubRouter-win-x64-lite\AIHubRouter.exe`：约 0.25 MiB 的轻量版，目标电脑需要已安装 .NET 10 Desktop Runtime x64。

WinForms 不支持安全的程序集裁剪，因此不启用 `PublishTrimmed`。自包含版压缩会略微增加首次启动的解压时间，但不把运行时文件释放到项目目录。

## 路由规则

程序比较的是 `/providers` 返回的供应商分组倍率，然后通过 `PUT /api/v1/keys/{id}` 更新 Key 的 `group_id`。它不会修改请求中的模型名称。用户专属分组倍率存在时，以专属倍率作为实际价格；同价时依次选择 6 小时可用率更高、首 Token 更快、分组 ID 更小的候选。

站点接口和字段可能随部署版本变化。程序依据 Sub2API 当前公开接口实现，接口不兼容时会显示错误并停止写入。

## 下游贡献与支持范围

本项目仍然只维护 Windows 原生 WinForms。加权路由模式（经济/均衡/速度）、可解释决策、账户缓存、路由状态、模拟运行、无凭据审计日志和原生主题行为参考并适配自 [OnRightPath/AIHubRouter](https://github.com/OnRightPath/AIHubRouter)。下游项目的 Linux、Avalonia、CLI、systemd 和跨平台发布内容不属于本仓库的支持范围。
