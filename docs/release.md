# 发布与签名

发一个版本涉及三件事：改版本号、打包、签名。这份文档说明怎么做，以及为什么要签名。

## 一、发版流程

```powershell
# 1. 一次改完所有版本号（Directory.Build.props 与 extension/manifest.json）
pwsh scripts\set-version.ps1 -Version 0.9.1

# 2. 跑测试（含“各处版本号必须一致”的检查）
dotnet test KeepPassword.sln -c Release

# 3. 打包（需要 pwsh 7；不带 -Version 时取 Directory.Build.props）
pwsh -NoProfile -File artifacts\package.ps1

# 4. 上传 artifacts\v0.9.1\ 里的文件到 GitHub Release
```

也可以推一个形如 `v0.9.1` 的标签，让 `.github/workflows/release.yml` 在干净的 Windows 环境里自动完成第 2 到 4 步。流水线需要先配置下面的密钥，**首次建议用预发布标签试一次**。

版本号只在两处手改（由脚本一起改）：`Directory.Build.props`、`extension/manifest.json`。安装向导、卸载信息、更新检查都在运行时读取程序集版本，不再各写一份；测试会在两者不一致时失败。

## 二、两种签名，解决两个不同的问题

| | 代码签名（Authenticode） | 更新签名（ECDSA） |
|---|---|---|
| 签的是 | 每个 exe | 安装包的版本号和 SHA-256 |
| 谁来验 | Windows、SmartScreen、安全软件 | Keep Password 自己的更新程序 |
| 解决什么 | “未知发布者”警告；安全软件反复提示；每次重装都当成新程序 | GitHub Release 或账号被入侵时，攻击者无法让用户装上被换掉的更新 |
| 需要什么 | 购买代码签名证书（.pfx） | 自己生成一对密钥，免费 |

**为什么 SHA-256 校验文件不够：** 校验文件和安装包放在同一个 Release 里。谁能改安装包，就能同时改校验文件，哈希照样对得上。更新签名的私钥只在你自己的机器上，所以光控制 Release 也伪造不出有效签名。

### 启用更新签名（一次性，需要你本人操作）

私钥必须由你生成并保管，**不要让任何工具或别人替你生成**，也不要提交到仓库。

```powershell
# 生成密钥：私钥用密码加密保存在仓库之外，公钥自动写进 UpdateTrust.cs
$env:KP_SIGN_PASSWORD = "至少 8 位的强密码"      # 不设置则会在终端里交互输入
dotnet run --project tools\KeepPassword.Release -c Release -- keygen --private D:\keys\keep-password.keppriv.pem
```

之后：

1. **立刻把私钥文件和密码备份到离线位置。** 丢了就无法再发布能通过验签的更新，已发出去的客户端只能手动重装。
2. 提交 `UpdateTrust.cs` 的改动，发布一个新版本。**这个版本之后，客户端才会要求更新必须带签名。** 旧版本客户端不受影响，仍按原来只校验哈希的方式更新到这个版本。
3. 此后每次打包都要提供私钥，否则脚本会在开始耗时的发布前直接报错，而不是发出一个会被客户端拒收的更新：

```powershell
$env:KP_SIGN_PASSWORD = "私钥密码"
pwsh -NoProfile -File artifacts\package.ps1 -UpdateKey D:\keys\keep-password.keppriv.pem
```

打包会生成 `KeepPassword-Setup-win-x64.exe.sig` 并立刻自检验签。**发布时必须把它和安装包一起上传。**

查看当前状态：`dotnet run --project tools\KeepPassword.Release -c Release -- status`（已启用返回 0，未启用返回 3）。

换钥时保留旧公钥、追加新公钥，两把并存一段时间，等旧版本客户端都升级后再删旧的。

### 启用代码签名

买到证书（.pfx）后：

```powershell
$env:CODESIGN_CERT_PASSWORD = "证书密码"
pwsh -NoProfile -File artifacts\package.ps1 -CertPath D:\keys\codesign.pfx
```

脚本会给 `KeepPassword.exe`、`KeepPassword.NativeHost.exe`、`Uninstall.exe` 和最终的 `Setup.exe` 签名并加时间戳（证书过期后签名依然有效）。顺序固定为“先代码签名，再计算 SHA-256 和更新签名”，因为签名会改变文件内容。

没有证书时脚本照常打包，只是在末尾提示“未做代码签名”。**自签名证书不会被系统信任**，只能用来验证流程，不能消除 SmartScreen 提示。

## 三、GitHub Actions 需要的密钥

在仓库 Settings → Secrets and variables → Actions 里添加（都可选，没配置就跳过对应签名）：

| 名称 | 内容 |
|---|---|
| `CODESIGN_CERT_BASE64` | 代码签名 .pfx 文件的 base64（`[Convert]::ToBase64String([IO.File]::ReadAllBytes("x.pfx"))`） |
| `CODESIGN_CERT_PASSWORD` | 该证书密码 |
| `UPDATE_SIGN_KEY_BASE64` | 更新签名私钥 .keppriv.pem 的 base64 |
| `UPDATE_SIGN_PASSWORD` | 该私钥密码 |

把更新签名私钥放进 GitHub Secrets，意味着“控制仓库的人”也能签名，削弱了离线私钥的保护。要最高的安全性，请保持在你自己的机器上打包签名，只用流水线做测试。

## 四、清理旧的打包产物

`artifacts\` 里每个版本约 600 MB。预览（不会删除任何东西）：

```powershell
pwsh scripts\prune-artifacts.ps1 -Keep 3
```

确认清单无误后加 `-Apply` 才会真的删除。只处理形如 `v1.2.3` 的目录。

## 五、Linux 打包脚本

`scripts/package-windows.sh` 可在 Linux 上交叉打包，但**没有 Authenticode 代码签名**（需要 Windows 的 signtool 或 osslsigncode），更新签名则通过 `KP_UPDATE_KEY` 环境变量支持。要发正式包请用上面 Windows 上的 `package.ps1`。这个脚本的最新改动我没有在 Linux 上实际跑过，只做了语法检查。
