param([Parameter(Mandatory = $true)][string]$Version)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.IO.Compression.FileSystem

$Root = (Resolve-Path "$PSScriptRoot\..").Path
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

Write-Host "== 扩展与说明 =="
Get-ChildItem -LiteralPath "$Root\extension" | Where-Object { $_.Name -ne "tests" } |
  Copy-Item -Destination "$Staging\extension" -Recurse -Force
$readme = @"
Keep Password

这是自包含安装包，不需要另外安装 .NET。

双击 KeepPassword.exe 启动。
卸载请运行 Uninstall.exe。默认不会删除缓存目录。

浏览器扩展见 extension 文件夹。
Native Messaging 宿主为本目录下的 KeepPassword.NativeHost.exe，例如：

  .\extension\register-windows.ps1 -HostExe .\KeepPassword.NativeHost.exe -ExtensionId <扩展ID>
"@
[IO.File]::WriteAllText((Join-Path $Staging "使用说明.txt"), $readme, (New-Object Text.UTF8Encoding $true))

Write-Host "== 打包 payload =="
$payload = Join-Path $Out "payload.zip"
[IO.Compression.ZipFile]::CreateFromDirectory($Staging, $payload, [IO.Compression.CompressionLevel]::SmallestSize, $false)
$hash = (Get-FileHash -LiteralPath $payload -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText("$Root\src\KeepPassword.Setup\payload.sha256", "$hash`n", [Text.Encoding]::ASCII)
Copy-Item -LiteralPath $payload -Destination "$Root\src\KeepPassword.Setup\payload.zip" -Force

Write-Host "== 发布安装向导 =="
try {
  Invoke-Dotnet publish "$Root\src\KeepPassword.Setup\KeepPassword.Setup.csproj" -c Release -r $Rid --self-contained true `
    "-p:PublishSingleFile=true" "-p:IncludeNativeLibrariesForSelfExtract=true" "-p:EnableCompressionInSingleFile=true" `
    "-p:DebugType=none" "-p:DebugSymbols=false" -o $SetupOut --nologo -v q
}
finally {
  Remove-Item -LiteralPath "$Root\src\KeepPassword.Setup\payload.zip", "$Root\src\KeepPassword.Setup\payload.sha256" -Force -ErrorAction SilentlyContinue
}

$SetupExe = Join-Path $Out "KeepPassword-Setup-win-x64.exe"
Copy-Item -LiteralPath (Join-Path $SetupOut "KeepPassword.Setup.exe") -Destination $SetupExe -Force
$SetupHash = (Get-FileHash -LiteralPath $SetupExe -Algorithm SHA256).Hash
$SetupShaFile = Join-Path $Out "KeepPassword-Setup-win-x64.exe.sha256"
[IO.File]::WriteAllText($SetupShaFile, $SetupHash + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
[IO.Compression.ZipFile]::CreateFromDirectory($Staging, (Join-Path $Out "KeepPassword-win-x64.zip"), [IO.Compression.CompressionLevel]::SmallestSize, $false)

Write-Host "== 打包浏览器扩展 =="
$ExtensionZip = Join-Path $Out "KeepPassword-Chrome-Extension-$Version.zip"
$EdgeExtensionZip = Join-Path $Out "KeepPassword-Edge-Extension-$Version.zip"
[IO.Compression.ZipFile]::CreateFromDirectory("$Staging\extension", $ExtensionZip, [IO.Compression.CompressionLevel]::SmallestSize, $false)
Copy-Item -LiteralPath $ExtensionZip -Destination $EdgeExtensionZip -Force

$assets = Get-Item -LiteralPath $SetupExe, (Join-Path $Out "KeepPassword-win-x64.zip"), $ExtensionZip, $EdgeExtensionZip, $SetupShaFile
$assets | ForEach-Object {
  if ($_.Name.EndsWith(".sha256")) {
    "{0}  {1}" -f $_.Name, (Get-Content -LiteralPath $_.FullName -Raw).Trim()
  } else {
    "{0}  {1:N1} MB  {2}" -f $_.Name, ($_.Length / 1MB), (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
  }
}
