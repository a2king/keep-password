#requires -Version 7.0
# 请用 pwsh 运行（Windows PowerShell 5.1 缺少本脚本用到的 ZipFile 重载，会报“找不到 CreateFromDirectory 的重载”）。
param(
  # 不传就用 Directory.Build.props 里的版本号（发版时唯一需要改的地方，见 scripts\set-version.ps1）。
  [string]$Version,
  # Authenticode 代码签名证书（.pfx）。不传则不签名；密码走环境变量 CODESIGN_CERT_PASSWORD，避免进命令行历史。
  [string]$CertPath = $env:CODESIGN_CERT_PATH,
  [string]$TimestampServer = "http://timestamp.digicert.com",
  # 更新签名私钥（tools\KeepPassword.Release 的 keygen 生成）。仓库已内置公钥时必须提供，否则发出去的更新会被客户端拒绝。
  [string]$UpdateKey = $env:KP_UPDATE_KEY
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.IO.Compression.FileSystem

$Root = (Resolve-Path "$PSScriptRoot\..").Path
if (-not $Version) {
  $Version = "v" + ([regex]::Match((Get-Content -Raw "$Root\Directory.Build.props"), '<Version>([^<]+)</Version>')).Groups[1].Value
}
$env:PATH = "$env:LOCALAPPDATA\dotnet;" + $env:PATH
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"

$Out = Join-Path $Root "artifacts\$Version"
$Staging = Join-Path $Out "staging"
$SetupOut = Join-Path $Out "setup"
$NhOut = Join-Path $Out "native-host-build"
$UninstallOut = Join-Path $Out "uninstall-build"
$Rid = "win-x64"

if (Test-Path -LiteralPath $Out) { Remove-Item -LiteralPath $Out -Recurse -Force }
New-Item -ItemType Directory -Force -Path "$Staging\extension", $SetupOut, $NhOut, $UninstallOut | Out-Null

function Invoke-Dotnet { & dotnet @args; if ($LASTEXITCODE -ne 0) { throw "dotnet $($args -join ' ') failed" } }

# 发布者工具（keygen / sign / verify / status）：先编译一次，之后直接调用 dll，
# 避免 dotnet run 的 “--” 参数分隔符在不同 PowerShell 版本里被吞掉或多传。
$ReleaseProject = Join-Path $Root "tools\KeepPassword.Release\KeepPassword.Release.csproj"
$ReleaseOut = Join-Path $Out "release-tool"
Invoke-Dotnet build $ReleaseProject -c Release -o $ReleaseOut --nologo -v q
$ReleaseDll = Join-Path $ReleaseOut "KeepPassword.Release.dll"
function Invoke-ReleaseTool { & dotnet $ReleaseDll @args; if ($LASTEXITCODE -ne 0) { throw "发布者工具失败：$($args -join ' ')（退出码 $LASTEXITCODE）" } }

# Authenticode 签名：让 SmartScreen 和安全软件认"发布者"而不是认每次都会变的文件哈希。
# 没提供证书就跳过（并在最后提示）；时间戳保证证书过期后签名依然有效。
$script:Signed = 0
function Sign-File([string]$Path) {
  if (-not $CertPath) { return }
  $pass = $env:CODESIGN_CERT_PASSWORD
  $cert = if ($pass) { [Security.Cryptography.X509Certificates.X509Certificate2]::new($CertPath, $pass) }
          else { [Security.Cryptography.X509Certificates.X509Certificate2]::new($CertPath) }
  $arguments = @{ FilePath = $Path; Certificate = $cert; HashAlgorithm = "SHA256" }
  if ($TimestampServer) { $arguments.TimestampServer = $TimestampServer }   # 传空字符串可以关掉时间戳（离线打包时用）
  $result = Set-AuthenticodeSignature @arguments
  if ($result.Status -eq "HashMismatch" -or -not $result.SignerCertificate) { throw "签名失败：$Path（$($result.StatusMessage)）" }
  $script:Signed++
}

# 先检查：仓库里已经内置了更新公钥，却没提供私钥，就在开始耗时的发布前直接失败，而不是发出一个会被客户端拒收的更新。
& dotnet $ReleaseDll status | Out-Host
# 退出码：0 = 已启用签名强制，3 = 未启用；其他都是工具本身出错，必须中止，不能当成“未启用”悄悄放过。
if ($LASTEXITCODE -notin 0, 3) { throw "无法确认是否启用了更新签名强制（工具退出码 $LASTEXITCODE）。" }
$trustEnforced = ($LASTEXITCODE -eq 0)
if ($trustEnforced -and -not $UpdateKey) {
  throw "仓库已启用更新签名强制（UpdateTrust.cs 里有公钥），请用 -UpdateKey 或环境变量 KP_UPDATE_KEY 提供私钥文件。"
}

Write-Host "== 发布主程序 =="
Invoke-Dotnet publish "$Root\src\KeepPassword.App\KeepPassword.App.csproj" -c Release -r $Rid --self-contained true `
  "-p:PublishSingleFile=false" "-p:DebugType=none" "-p:DebugSymbols=false" "-p:PublishReadyToRun=true" -o $Staging --nologo -v q

Write-Host "== 发布 Native Host =="
Invoke-Dotnet publish "$Root\src\KeepPassword.NativeHost\KeepPassword.NativeHost.csproj" -c Release -r $Rid --self-contained true `
  "-p:PublishSingleFile=false" "-p:DebugType=none" "-p:DebugSymbols=false" -o $NhOut --nologo -v q
foreach ($f in "KeepPassword.NativeHost.exe", "KeepPassword.NativeHost.dll", "KeepPassword.NativeHost.deps.json", "KeepPassword.NativeHost.runtimeconfig.json") {
  Copy-Item -LiteralPath (Join-Path $NhOut $f) -Destination $Staging -Force
}

Write-Host "== 发布卸载程序 =="
Invoke-Dotnet publish "$Root\src\KeepPassword.Uninstall\KeepPassword.Uninstall.csproj" -c Release -r $Rid --self-contained true `
  "-p:PublishSingleFile=false" "-p:DebugType=none" "-p:DebugSymbols=false" -o $UninstallOut --nologo -v q
foreach ($f in "Uninstall.exe", "Uninstall.dll", "Uninstall.deps.json", "Uninstall.runtimeconfig.json") {
  Copy-Item -LiteralPath (Join-Path $UninstallOut $f) -Destination $Staging -Force
}

Get-ChildItem -LiteralPath $Staging -Recurse -Filter *.pdb | Remove-Item -Force
Remove-Item -LiteralPath (Join-Path $Staging "createdump.exe") -Force -ErrorAction SilentlyContinue

Write-Host "== 代码签名 =="
foreach ($f in "KeepPassword.exe", "KeepPassword.NativeHost.exe", "Uninstall.exe") { Sign-File (Join-Path $Staging $f) }

Write-Host "== 扩展与说明 =="
Get-ChildItem -LiteralPath "$Root\extension" | Where-Object { $_.Name -ne "tests" } |
  Copy-Item -Destination "$Staging\extension" -Recurse -Force
# 说明文字放在 scripts\install-readme.txt，与 Linux 打包脚本共用同一份，避免两边各改各的。
Copy-Item -LiteralPath "$Root\scripts\install-readme.txt" -Destination (Join-Path $Staging "使用说明.txt") -Force

Write-Host "== 打包 payload =="
$payload = Join-Path $Out "payload.zip"
[IO.Compression.ZipFile]::CreateFromDirectory($Staging, $payload, [IO.Compression.CompressionLevel]::SmallestSize, $false)
$hash = (Get-FileHash -LiteralPath $payload -Algorithm SHA256).Hash.ToLowerInvariant()

Write-Host "== 发布安装向导 =="
try {
  # 内嵌用的 payload 临时放进源码目录，放在 try 里，任何一步失败都由 finally 清掉。
  [IO.File]::WriteAllText("$Root\src\KeepPassword.Setup\payload.sha256", "$hash`n", [Text.Encoding]::ASCII)
  Copy-Item -LiteralPath $payload -Destination "$Root\src\KeepPassword.Setup\payload.zip" -Force
  Invoke-Dotnet publish "$Root\src\KeepPassword.Setup\KeepPassword.Setup.csproj" -c Release -r $Rid --self-contained true `
    "-p:PublishSingleFile=true" "-p:IncludeNativeLibrariesForSelfExtract=true" "-p:EnableCompressionInSingleFile=true" `
    "-p:DebugType=none" "-p:DebugSymbols=false" -o $SetupOut --nologo -v q
}
finally {
  Remove-Item -LiteralPath "$Root\src\KeepPassword.Setup\payload.zip", "$Root\src\KeepPassword.Setup\payload.sha256" -Force -ErrorAction SilentlyContinue
}

$SetupExe = Join-Path $Out "KeepPassword-Setup-win-x64.exe"
Copy-Item -LiteralPath (Join-Path $SetupOut "KeepPassword.Setup.exe") -Destination $SetupExe -Force
Sign-File $SetupExe
$SetupHash = (Get-FileHash -LiteralPath $SetupExe -Algorithm SHA256).Hash
$SetupShaFile = Join-Path $Out "KeepPassword-Setup-win-x64.exe.sha256"
[IO.File]::WriteAllText($SetupShaFile, $SetupHash + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
[IO.Compression.ZipFile]::CreateFromDirectory($Staging, (Join-Path $Out "KeepPassword-win-x64.zip"), [IO.Compression.CompressionLevel]::SmallestSize, $false)

# 更新签名：对"版本号 + 最终 Setup.exe 的 SHA-256"签名，客户端用内置公钥验签。
$SigFile = "$SetupExe.sig"
if ($UpdateKey) {
  Write-Host "== 更新签名 =="
  Invoke-ReleaseTool sign --private $UpdateKey --file $SetupExe --version $Version --out $SigFile
  Invoke-ReleaseTool verify --file $SetupExe --version $Version --sig $SigFile
}

Write-Host "== 打包浏览器扩展 =="
$ExtensionZip = Join-Path $Out "KeepPassword-Chrome-Extension-$Version.zip"
$EdgeExtensionZip = Join-Path $Out "KeepPassword-Edge-Extension-$Version.zip"
[IO.Compression.ZipFile]::CreateFromDirectory("$Staging\extension", $ExtensionZip, [IO.Compression.CompressionLevel]::SmallestSize, $false)
# Edge 商店拒绝 manifest 里的 key。本地加载仍用带 key 的 Chrome 包，以保持扩展 ID 不变。
$EdgeStage = Join-Path $Out "edge-extension"
if (Test-Path -LiteralPath $EdgeStage) { Remove-Item -LiteralPath $EdgeStage -Recurse -Force }
Copy-Item -LiteralPath "$Staging\extension" -Destination $EdgeStage -Recurse
$edgeManifest = Join-Path $EdgeStage "manifest.json"
$edgeText = [regex]::Replace([IO.File]::ReadAllText($edgeManifest), '\r?\n[ \t]*"key"\s*:\s*"[^"]*"\s*,', '')
[IO.File]::WriteAllText($edgeManifest, $edgeText, [Text.UTF8Encoding]::new($false))
if (Test-Path -LiteralPath $EdgeExtensionZip) { Remove-Item -LiteralPath $EdgeExtensionZip -Force }
[IO.Compression.ZipFile]::CreateFromDirectory($EdgeStage, $EdgeExtensionZip, [IO.Compression.CompressionLevel]::SmallestSize, $false)

$assets = @(Get-Item -LiteralPath $SetupExe, (Join-Path $Out "KeepPassword-win-x64.zip"), $ExtensionZip, $EdgeExtensionZip, $SetupShaFile)
if (Test-Path -LiteralPath $SigFile) { $assets += Get-Item -LiteralPath $SigFile }
$assets | ForEach-Object {
  if ($_.Name.EndsWith(".sha256")) {
    "{0}  {1}" -f $_.Name, (Get-Content -LiteralPath $_.FullName -Raw).Trim()
  } else {
    "{0}  {1:N1} MB  {2}" -f $_.Name, ($_.Length / 1MB), (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
  }
}

Write-Host ""
if ($script:Signed -eq 0) { Write-Warning '未做代码签名（没有提供证书 -CertPath）：用户运行安装程序时会看到 SmartScreen 的未知发布者提示。' }
else { Write-Host "代码签名：已签 $($script:Signed) 个文件（证书 $CertPath）" }
if (-not $UpdateKey) { Write-Warning "未做更新签名：客户端目前不会校验发布者签名，只校验 SHA-256。运行 tools\KeepPassword.Release keygen 启用。" }
