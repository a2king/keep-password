# Keep Password

单机密码管理器。界面用 Avalonia，风格接近 1Password：深色侧栏、分组列表、卡片式详情。同一套代码可以发布到 Windows、macOS 和 Linux。保险库文件与系统无关，换电脑时可以直接拷贝打开。

没有云同步，也不扫描二维码。

## 两把锁

首次运行要设置三项：

- 账号
- 主密码
- 固定短密钥

主密码用 Argon2id 派生密钥，保险库用 AES-256-GCM 加密，主密码不明文落盘。短密钥只存 Argon2id 验证哈希，也不明文落盘。

再次打开时只输入主密码。账号已经写在保险库文件里，解锁页会显示，不用再填。短密钥不参与这次解锁；主密码错误仍然打不开。

补全前会再弹一次短密钥确认，即使刚刚解锁过。设置里可以修改短密钥，提交前必须再输入主密码并且验证通过。

## 数据目录

保险库文件名都是 `vault.kpvault`。默认缓存目录：

| 系统 | 目录 |
| --- | --- |
| Windows | `%LOCALAPPDATA%\KeepPassword` |
| macOS | `~/Library/Application Support/KeepPassword` |
| Linux | `~/.local/share/KeepPassword` |

解锁页和设置里可以改缓存目录。选定新目录后，会把当前目录里的文件全部转过去，再删除原来的目录。目录记在单独的配置文件里，不跟缓存文件放在一起：

| 系统 | 配置文件 |
| --- | --- |
| Windows | `%APPDATA%\KeepPassword\settings.json` |
| macOS | `~/Library/Preferences/KeepPassword/settings.json` |
| Linux | `~/.config/KeepPassword/settings.json` |

## Windows 安装与卸载

**正式安装包是自包含的，不需要安装 .NET，也不需要其它运行库。**  
（开发者在本机编译时才需要 .NET 8 SDK。）

推荐使用安装程序：

1. 运行 `KeepPassword-Setup-win-x64.exe`。
2. 选择安装目录，可勾选：开始菜单快捷方式、桌面快捷方式、安装完成后立即运行。
3. 安装过程有进度条：先解压压缩包，再复制到目标目录。
4. 完成后点「完成」关闭安装窗口。若勾选了立即运行，主程序会自动启动。
5. 默认安装到 `%LOCALAPPDATA%\Programs\KeepPassword`。覆盖安装会清理旧版本残留程序文件，不会动缓存里的保险库。
6. 安装包是压缩后的自包含运行时（主程序与 Native Host 共用一份），**不需要安装 .NET**。
7. 卸载运行安装目录里的 `Uninstall.exe`。可勾选「同时删除缓存目录」，**默认不勾选**。

也可以解压便携包 `KeepPassword-win-x64.zip` 直接使用。

打 Windows 安装包（仅打包机器需要 .NET 8 SDK）：

```bash
./scripts/package-windows.sh /tmp/kp-windows-package
```

产物在输出目录：`KeepPassword-Setup-win-x64.exe`、`KeepPassword-win-x64.zip`。

## 条目

字段：名称、网址、用户名、密码、备注，以及可选的验证码密钥。列表按网址的主机名分组。搜索范围是名称、网址、用户名、备注。详情里可以切换密码明文。

## 首次使用

1. Windows 可用安装程序；开发时也可安装 [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) 后直接运行。
2. 在解锁页创建保险库：账号、主密码、确认主密码、短密钥、确认短密钥。
3. 之后每次打开只输入主密码。主窗口可以锁定，或从托盘锁定。关闭主窗口时，如果托盘可用就缩到托盘，否则锁定。

## CSV 导入

表头必须能认出这五列，大小写不限：

```csv
name,url,username,password,note
GitHub,https://github.com,ada,"p,ass",工作账号
邮箱,https://mail.example.com,ada@example.com,hunter2,个人
```

逗号和 Tab 都可以。密码里如果有逗号，用双引号包起来。导入后按主机名归类，也能被搜索。

## 验证码

TOTP，RFC 6238，SHA-1，6 位，30 秒。密钥用 Base32 手填。可以写在某条登录记录上，也可以单独新建一条（只填名称和密钥）。验证码页面会列出当前码和剩余秒数。

## 补全

发现密码框、匹配条目、验证短密钥、填入用户名和密码，走同一套接口。匹配依据当前页面或窗口标题里的域名，对照条目的网址主机名（含子域名）。

- Windows：普通程序用 UI Automation 查找 `IsPassword` 的输入框；浏览器走 Chrome / Edge 扩展和 Native Messaging。填入前都要再输入短密钥。
- macOS、Linux：程序补全只留了接口和空实现（辅助功能 / AT-SPI 以后再接）。浏览器扩展和本机消息协议是同一份，客户端在这三个系统上都会听本机端口。

扩展在 `extension/`，Manifest V3。安装目录里的 `extension\` 与 `native-host\KeepPassword.NativeHost.exe` 可按 `register-windows.ps1` 注册。

## 构建和运行

在仓库根目录：

```bash
dotnet test
dotnet run --project src/KeepPassword.App
```

Windows 用户请直接用上面的安装包或便携包，**不要**再单独安装 .NET。

若自己发布，请使用自包含（或单文件）模式，例如：

```bash
dotnet publish src/KeepPassword.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o /tmp/kp-win
```

非单文件发布时，可用 `scripts/arrange-lib-layout.sh` 整理目录；脚本会把 `hostfxr` / `coreclr` 留在 exe 旁，避免误报需要安装 .NET。

Windows 安装包请用上面的 `package-windows.sh`。

## 本环境验证

当前环境是 Linux。

- `dotnet test` 已通过。
- 已重新设计界面与 Logo，并在 Linux 上打开过解锁页与主窗口。
- 已能打出 Windows 安装程序与带 `lib\` 布局的发布目录。
- 没有在 Windows 上跑过实际安装向导、UI Automation 填入和浏览器扩展。macOS 未做图形验收。
