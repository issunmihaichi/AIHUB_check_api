# AIHub 自动会话与分发安全设计

## 目标

AIHubRouter 使用邮箱和密码自动登录，优先刷新即将过期的会话，刷新失败时自动重新登录；同时持久化用户选择的 API Key，并确保正式分发包不包含开发者本机的任何认证或身份信息。

## 认证体验

- “连接与认证”以邮箱和密码作为主认证方式。
- 手工 Token、Cookie 和 User-Agent 保留为高级认证回退，不作为默认流程。
- 邮箱、密码、access token、refresh token、Cookie 和 User-Agent 仅写入 DPAPI 加密的 `credentials.dat`。
- 站点地址、平台、阈值、轮询间隔、平滑绘制和所选 Key ID 写入非敏感 `settings.json`。
- 站点当前登录接口字段为 `email` 和 `password`。
- 登录响应若要求验证码或 TOTP，程序停止自动认证并显示明确错误，不绕过验证。

## 会话生命周期

1. 启动时读取 DPAPI 凭据。
2. access token 距离过期超过两分钟时直接使用。
3. access token 缺失或即将过期时，优先使用 refresh token 调用 `/api/v1/auth/refresh`。
4. refresh token 无效、撤销或过期时，使用已保存邮箱和密码调用 `/api/v1/auth/login`。
5. 每次成功登录或刷新后立即保存服务端返回的新 token 对和到期时间。
6. 业务 API 返回 401 时只允许刷新并重试一次；第二次 401 终止本轮操作。
7. 自动路由开关不跨启动恢复，避免程序启动即修改远端 Key。

## Key 选择持久化

- 第一次从账号加载 Key 且从未保存选择时，默认选择第一个启用的 Key。
- 此后保存完整的所选 Key ID 集合。
- 空集合是有效选择，表示用户主动取消全部勾选，后续启动不得再次自动选择第一个。
- 服务端已删除或当前账号不可见的 ID 在恢复时忽略。
- 新出现的 Key 默认不选中。
- 勾选变化在常态化开启时立即保存，并在正常退出时再次保存。

## 组件边界

- `AIHubClient`：负责 HTTP、登录、刷新和业务 API 序列化，不决定何时刷新。
- `SessionCoordinator`：维护当前 token、判断到期、执行刷新优先与登录回退，并通过回调持久化轮换后的会话。
- `AppSettingsStore`：DPAPI 加密敏感凭据，普通 JSON 保存非敏感设置与 Key ID。
- `MainForm`：收集邮箱和密码、展示认证状态、请求有效会话并恢复 Key 勾选状态。
- `scripts/scan-release.ps1`：扫描源码和正式 EXE 中的本机路径、JWT、Bearer、API Key、Cookie、邮箱和 refresh token 值；命中即以非零状态退出。

## 分发安全

- 发布脚本只复制编译输出，不读取或复制 `%LocalAppData%\AIHubRouter`。
- Release 禁用 PDB 和调试符号，启用确定性编译和路径映射，避免嵌入开发者绝对路径。
- 扫描规则不得把字段名 `auth_token` 或 `refresh_token` 当作秘密；只匹配具有凭据长度和取值形态的内容。
- 输出约 49 MiB 的压缩自包含版和约 0.25 MiB 的框架依赖轻量版。

## 错误处理

- 错误消息不得包含邮箱、密码、token、Cookie 或服务器响应中的敏感字段。
- 网络超时保留当前会话，等待下次轮询重试。
- `invalid_grant`、401 或明确 token 错误触发登录回退。
- 密码错误停止自动登录并提示检查邮箱和密码，不能循环重试。
- DPAPI 解密失败时忽略本地认证并要求重新登录。

## 测试

- 有效 access token 不触发刷新或登录。
- 即将过期时优先刷新。
- 刷新失败回退邮箱密码登录。
- 401 只重试一次。
- 刷新 token 轮换后立即持久化。
- DPAPI 加密往返覆盖邮箱、密码和 token，磁盘文件不含明文。
- Key ID 多选、空选、失效 ID 和首次默认选择均有测试。
- 发布扫描对合成秘密返回失败，对正式产物返回成功。
