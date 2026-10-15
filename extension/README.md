# 连接 Chrome / Edge

浏览器扩展通过 Native Messaging 宿主连接正在运行的 Keep Password 桌面端。安装桌面程序不会自动安装扩展或注册宿主，首次使用需要完成以下步骤。当前支持 Chrome / Edge 等 Chromium 浏览器，不支持 Firefox。

## Windows 首次连接

1. 启动并解锁 Keep Password。
2. 在 Chrome 打开 `chrome://extensions`，或在 Edge 打开 `edge://extensions`。打开「开发者模式」，点击「加载已解压的扩展」，选择安装目录里的 **extension 文件夹**（其中应包含 `manifest.json`）。
3. 复制扩展卡片中的 **ID**，它是 32 位小写字母，例如 `abcdefghijklmnopabcdefghijklmnop`。此处示例不是你实际的 ID。
4. 在 **Keep Password 安装目录** 打开 PowerShell，运行下面的命令，把示例 ID 替换为刚复制的 ID：

   ```powershell
   powershell -NoProfile -ExecutionPolicy Bypass -File .\extension\register-windows.ps1 -ExtensionId "abcdefghijklmnopabcdefghijklmnop"
   ```

   脚本只注册当前 Windows 用户，不需要管理员权限。默认查找安装目录根部的 `KeepPassword.NativeHost.exe`，也兼容旧的 `native-host` 子目录；都找不到时再找默认安装位置 `%LOCALAPPDATA%\Programs\KeepPassword`。可显式指定其他发布目录的宿主：

   ```powershell
   powershell -NoProfile -ExecutionPolicy Bypass -File .\extension\register-windows.ps1 -HostExe "D:\Apps\KeepPassword\KeepPassword.NativeHost.exe" -ExtensionId "abcdefghijklmnopabcdefghijklmnop"
   ```

   追加 `-WhatIf` 可以检查参数和宿主路径，不写入配置或注册表。不要只复制单独一个宿主 EXE；它需要同目录中的配套发布文件和运行时。
5. 刷新登录网页。扩展刚安装或更新时，已经打开的网页需要重新加载。
6. 在保险库里保存该网站的登录条目，填写实际的网站地址。点击网页里的**密码框**，扩展会自动在输入框附近显示匹配账号。选择账号后在候选框输入短密钥并点击「授权填入」，确认后填入账号和密码。扩展按钮和 `Ctrl+Shift+L` 仍可用于手动重新打开候选框。

聚焦网页密码框会主动显示候选框，但不会自动填入密码，也不会自动提交表单；必须由你选择账号并输入短密钥授权。首次使用新保险库需要先在 Keep Password 中解锁一次，以生成当前 Windows 用户保护的候选索引；以后程序锁定时仍可显示候选，填充时才用短密钥临时解密。若快捷键被其他扩展占用，可在 `chrome://extensions/shortcuts` 或 `edge://extensions/shortcuts` 调整。

Chrome 和 Edge 加载同一扩展时，ID 可能不同。分别取得两个浏览器的 ID，各运行一次注册命令；脚本会保留已经注册的 ID。移动扩展目录后 ID 可能改变，需要重新注册；移动安装目录后宿主路径也需要重新注册。

## 使用单独下载的扩展包

Release 中的 `KeepPassword-Chrome-Extension-<版本>.zip` 与安装目录里的 `extension` 文件夹内容相同，适合单独更新扩展：

1. 先安装同版本的 Keep Password 桌面端（扩展必须配合桌面端和 Native Host 使用）。
2. 把压缩包解压到一个**固定位置**，例如 `D:\Apps\KeepPassword-Extension`。之后不要移动或删除这个文件夹，否则扩展 ID 可能改变。
3. 在扩展管理页「加载已解压的扩展」，选择解压后的文件夹（其中直接包含 `manifest.json`），复制扩展 ID。
4. 在解压后的文件夹里运行注册脚本。桌面端装在默认位置时不需要 `-HostExe`，否则用 `-HostExe` 指向安装目录里的 `KeepPassword.NativeHost.exe`：

   ```powershell
   powershell -NoProfile -ExecutionPolicy Bypass -File .\register-windows.ps1 -ExtensionId "实际扩展ID"
   ```

以后更新扩展时，把新版本解压覆盖到同一个文件夹，在扩展管理页点击「重新加载」即可，ID 不变，不需要重新注册。

## 如何判断连接问题

新版扩展会在网页显示结果，扩展按钮也会显示 `OK` 或 `!`；将鼠标悬停在按钮上可查看具体原因。

| 提示 / 现象 | 处理方法 |
| --- | --- |
| 浏览器尚未连接 / Native messaging host not found | 重新注册宿主，核对当前浏览器显示的扩展 ID 和真实 EXE 路径 |
| Keep Password 客户端未运行 / 无法连接客户端 | 启动桌面程序，确保桌面程序和浏览器使用同一个 Windows 用户 |
| 请先解锁 Keep Password | 首次升级或候选索引不可用时，先在新版桌面程序解锁一次；此后可以保持锁定并使用浏览器填充 |
| 没有匹配的网站记录 | 检查保险库条目的网站地址是否匹配当前页面；内嵌登录页按密码框所在页面的域名匹配 |
| 无法访问当前密码框 | 刷新登录网页，重新点击密码框；浏览器设置页、扩展商店等受限页面不能注入扩展 |
| 密码框已变化 | 网站在确认过程中更新了表单，重新点击当前密码框再填充 |

扩展当前识别普通网页中的 `<input type="password">`，包括允许注入脚本的 iframe。关闭的 Shadow DOM、自绘输入框或把密码框伪装成其他控件的网站仍可能无法识别。

## 开发目录使用

源码中的 `extension` 可直接加载，但必须先发布 NativeHost，再把已发布的 EXE 路径传给注册脚本。现有 Windows 发布包将宿主放在安装根目录，**不是固定放在 `native-host` 子目录**。修改扩展后在扩展管理页点击「重新加载」，再刷新登录网页；修改桌面端 / NativeHost 后还需要重新构建并重启桌面程序。

扩展回归检查（需要 Node.js）：

```powershell
node --test extension/tests/*.test.cjs
```

## macOS / Linux

同样先加载扩展并复制实际 ID，然后运行：

```bash
bash extension/register-unix.sh /absolute/path/to/KeepPassword.NativeHost 实际扩展ID
```

桌面端需要保持运行并解锁。这套浏览器连接独立于普通桌面程序的自动填充能力。
