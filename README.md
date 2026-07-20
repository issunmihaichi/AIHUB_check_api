# AIHub 最低价路由器

Windows 原生 WinForms 客户端。程序读取 `https://aihub.top/providers` 背后的公开监测接口，并把选中的 API Key 切换到当前符合条件且倍率最低的供应商分组。

## 功能

- 展示供应商倍率、当前状态、6 小时可用率、首 Token 延迟和检测时间。
- 读取账号可用分组、用户专属倍率和 `/keys` 下的 API Key。
- 支持单个或多个 Key 立即路由、定时自动路由。
- 排除当前异常、监测超过 15 分钟、低于可用率阈值、账号无权限的分组。
- 内置认证向导、登录页快捷入口和 Token/Cookie/User-Agent 粘贴按钮。
- 提供“垂直同步（双缓冲）”开关，减少表格滚动和定时刷新时的闪烁。
- 表格列分隔线调整右侧列：向左拖扩宽右侧列，向右拖缩窄右侧列。
- Token、Cookie、User-Agent 只保存在进程内存中，不写文件、不写日志。

## 认证

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

凭据过期或出现 401 后，程序会停止自动路由。不要把 Token、Cookie 或发布目录中的私人配置提交到版本库。

## 常态化保存

勾选“常态化保存认证”并点击“保存当前配置”后，程序会在下次启动时恢复连接、认证和路由界面设置。Token、Cookie 和 User-Agent 使用 Windows DPAPI 加密，仅保存它们的当前 Windows 用户可以解密；站点、平台、阈值和轮询间隔保存在普通设置文件中。

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

发布脚本会生成两个版本：

- `artifacts\AIHubRouter-win-x64\AIHubRouter.exe`：约 49 MiB 的压缩自包含版，目标电脑无需安装 .NET。
- `artifacts\AIHubRouter-win-x64-lite\AIHubRouter.exe`：约 0.25 MiB 的轻量版，目标电脑需要已安装 .NET 10 Desktop Runtime x64。

WinForms 不支持安全的程序集裁剪，因此不启用 `PublishTrimmed`。自包含版压缩会略微增加首次启动的解压时间，但不把运行时文件释放到项目目录。

## 路由规则

程序比较的是 `/providers` 返回的供应商分组倍率，然后通过 `PUT /api/v1/keys/{id}` 更新 Key 的 `group_id`。它不会修改请求中的模型名称。用户专属分组倍率存在时，以专属倍率作为实际价格；同价时依次选择 6 小时可用率更高、首 Token 更快、分组 ID 更小的候选。

站点接口和字段可能随部署版本变化。程序依据 Sub2API 当前公开接口实现，接口不兼容时会显示错误并停止写入。
