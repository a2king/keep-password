# 详情展示服务接入说明

`DetailPresentationViewModel` 只保存账号选择、页签名称和可保存状态。真实字段仍写入 `MainViewModel`；`GetDetailValidationMessage()` 根据当前草稿校验，既不保存，也不改变会话。`IsDirty` 继续比较原有快照，密码显隐、验证码刷新和账号切换不会产生修改。

`DisplayChanged` 在条目/字段对象替换与账号切换时触发，适合更新场景布局；普通输入只更新属性通知。账号页签的 `Label` 可直接绑定，顺序保持原集合顺序，名称依次取账号说明、用户名和“账号 N”。保存后按账号 ID 解析新对象，切换条目重置为首账号。宿主关闭、锁定或更换编辑器时须 `Dispose()`，解除订阅并释放展示层持有的账号/源引用。

所有复制入口都调用 `ClipboardService.CopyAsync(TopLevel?, value, sensitive)`，返回值表示写入是否成功；成功后即可显示反馈。`SecretClipboard` 保留为同一服务的敏感复制兼容入口。敏感内容使用 Core `ExpiringClipboard.DefaultLifetime` 的 30 秒期限，后台任务到期后仅在任务仍有效且剪贴板内容仍相同的情况下清空。后一次复制（包括非敏感值）会取消旧任务；读、写与清空串行，避免到期清空覆盖后一次写入。

验证码控件附加到可见界面时调用 `TotpTicker.Subscribe(Action<DateTimeOffset>)`，离开界面或锁定时释放订阅。所有订阅共用一个每秒运行的 `DispatcherTimer`，订阅时立即收到当前时间，最后一个订阅释放后停止。订阅必须在 UI 线程调用，释放可在其他线程调用（实际移除调度回 UI 线程）。

快捷文本由 `DetailQuickActionFormatter` 生成，不会打开网络连接，也不写入已废弃的 `connection` 模型字段：

- 服务器仅 SSH/SFTP，分别使用 `-p` / `-P`，不含密码。Windows 按 PowerShell 单引号转义，macOS/Linux 按 POSIX shell 转义；Windows 的 `cmd.exe` 不属于该命令格式。主机必须是独立的域名或 IP，端口使用独立字段，IPv6 自动补方括号。
- MySQL/PostgreSQL/MongoDB/Redis 使用 URI，用户名、密码与数据库路径分别编码，IPv6 自动补方括号，空端口不补默认值。Redis 数据库编号须为非负整数。
- SQL Server/SQLite 使用 `DbConnectionStringBuilder` 处理分号、引号和空格，采用各自连接字符串字段。SQLite `Password` 仅在显式包含密码时生成，能否使用取决于目标 SQLite 提供程序的加密支持。
- 普通连接文本省略密码；只有显式 `includePassword: true` 才包含密码，此时调用复制服务必须传 `sensitive: true`。不支持或必要字段无效的协议/驱动返回 `null`，界面应隐藏对应操作。

`DetailServicesTests` 覆盖无需真实等待的剪贴板到期与替换保护、清空/写入竞态、失败后复用、共享时钟生命周期、账号保存后恢复、脏状态与无副作用校验，以及 URI/连接字符串/终端转义。
