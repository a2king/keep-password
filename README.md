# Keep Password

单机密码管理器。界面用 Avalonia，同一套代码可以发布到 Windows、macOS 和 Linux。保险库文件与系统无关，换电脑时可以直接拷贝打开。

没有云同步，也不扫描二维码。

## 两把锁

首次运行要设置三项：

- 账号
- 主密码
- 固定短密钥

之后每次打开，解锁页都要这三项。主密码用 Argon2id 派生密钥，保险库用 AES-256-GCM 加密，主密码不明文落盘。短密钥只存 Argon2id 验证哈希，也不明文落盘。账号、主密码、短密钥任意一项错误都不能解锁。

补全前会再弹一次短密钥确认，即使刚刚解锁过。用户管理里可以修改短密钥，提交前必须再输入主密码并且验证通过。

## 数据目录

保险库文件名都是 `vault.kpvault`。

| 系统 | 目录 |
| --- | --- |
| Windows | `%LOCALAPPDATA%\KeepPassword` |
| macOS | `~/Library/Application Support/KeepPassword` |
| Linux | `~/.local/share/KeepPassword` |

## 条目

字段：名称、网址、用户名、密码、备注，以及可选的验证码密钥。列表按网址的主机名分组。搜索范围是名称、网址、用户名、备注。详情里可以切换密码明文。

## 首次使用

1. 安装 [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)。
2. 在仓库根目录运行客户端（见下方构建命令）。
3. 在解锁页填写账号、主密码、确认主密码、短密钥、确认短密钥，然后创建。
4. 之后每次打开都输入这三项解锁。主窗口可以锁定，或从托盘锁定。关闭主窗口时，如果托盘可用就缩到托盘，否则锁定。

## CSV 导入

表头必须能认出这五列，大小写不限：

```csv
name,url,username,password,note
GitHub,https://github.com,ada,"p,ass",工作账号
邮箱,https://mail.example.com,ada@example.com,hunter2,个人
```

逗号和 Tab 都可以。密码里如果有逗号，用双引号包起来。导入后按主机名归类，也能被搜索。

Tab 示例：

```text
name	url	username	password	note
银行	https://bank.example	ada	secret	备注
```

## 验证码

TOTP，RFC 6238，SHA-1，6 位，30 秒。密钥用 Base32 手填。可以写在某条登录记录上，也可以单独新建一条（只填名称和密钥）。验证码页面会列出当前码和剩余秒数。

## 补全

发现密码框、匹配条目、验证短密钥、填入用户名和密码，走同一套接口。匹配依据当前页面或窗口标题里的域名，对照条目的网址主机名（含子域名）。

- Windows：普通程序用 UI Automation 查找 `IsPassword` 的输入框；浏览器走 Chrome / Edge 扩展和 Native Messaging。填入前都要再输入短密钥。
- macOS、Linux：程序补全只留了接口和空实现（辅助功能 / AT-SPI 以后再接）。浏览器扩展和本机消息协议是同一份，客户端在这三个系统上都会听本机端口。

扩展在 `extension/`，Manifest V3。它只检测登录页的账号框和密码框，把页面地址发给客户端；客户端弹出匹配条目和短密钥，通过后再把用户名和密码填回去。

### 安装扩展

1. 用对应系统发布 Native Host（见下方命令），得到可执行文件。
2. Chrome 或 Edge 打开扩展管理，开启开发者模式，加载 `extension/` 目录，复制扩展 ID。
3. 注册本机消息宿主：

Windows（PowerShell）：

```powershell
.\extension\register-windows.ps1 -HostExe C:\path\to\KeepPassword.NativeHost.exe -ExtensionId <扩展ID>
```

macOS 或 Linux：

```bash
./extension/register-unix.sh /path/to/KeepPassword.NativeHost <扩展ID>
```

脚本会写入 `com.keeppassword.host`。Windows 写注册表；macOS 写 `~/Library/Application Support/Google/Chrome/NativeMessagingHosts` 和 Edge 的对应目录；Linux 写 `~/.config/google-chrome`、`chromium`、`microsoft-edge` 下的 `NativeMessagingHosts`。

4. 先打开并解锁 Keep Password，再打开登录页。

宿主和客户端之间用 `127.0.0.1:50731` 转发一条 Native Messaging 消息。模板见 `extension/com.keeppassword.host.json`。

## 构建和运行

在仓库根目录：

```bash
dotnet test
dotnet run --project src/KeepPassword.App
```

发布三个目标（需要对应平台的 .NET 8 运行时，这里是框架依赖发布）：

```bash
dotnet publish src/KeepPassword.App -c Release -r win-x64 --self-contained false
dotnet publish src/KeepPassword.App -c Release -r osx-arm64 --self-contained false
dotnet publish src/KeepPassword.App -c Release -r linux-x64 --self-contained false
```

Native Host 同样按系统发布，例如：

```bash
dotnet publish src/KeepPassword.NativeHost -c Release -r win-x64 --self-contained false
dotnet publish src/KeepPassword.NativeHost -c Release -r osx-arm64 --self-contained false
dotnet publish src/KeepPassword.NativeHost -c Release -r linux-x64 --self-contained false
```

Windows 上运行发布结果里的 `KeepPassword.exe`。macOS 运行 `KeepPassword`。Linux 运行 `KeepPassword`。

## 本环境验证

当前环境是 Linux。

- `dotnet test` 已通过。
- `dotnet publish` 已打出 `win-x64`、`osx-arm64`、`linux-x64` 三个界面包。
- 在 Linux 上打开过客户端：创建保险库、解锁、新建条目、保存、按域名显示、搜索框和密码掩码都看过。
- 没有在 Windows 上跑过 UI Automation 填入，也没有在浏览器里加载扩展。macOS 的包只做了编译，没有在 Mac 上打开。
