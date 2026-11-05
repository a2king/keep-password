param([Parameter(Mandatory = $true)][string]$Version)

# 一次改完所有需要同步的版本号：Directory.Build.props 与浏览器扩展 manifest.json。
# 其余地方（安装向导、卸载信息、更新检查）都在运行时读取程序集版本，不需要手改。
$ErrorActionPreference = "Stop"
$Version = $Version.TrimStart("v", "V")
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "版本号格式应为 主.次.修订，例如 0.9.1，收到：$Version" }

$Root = (Resolve-Path "$PSScriptRoot\..").Path
$props = Join-Path $Root "Directory.Build.props"
$manifest = Join-Path $Root "extension\manifest.json"

$text = [IO.File]::ReadAllText($props)
$text = [regex]::Replace($text, '<Version>[^<]+</Version>', "<Version>$Version</Version>")
[IO.File]::WriteAllText($props, $text, (New-Object Text.UTF8Encoding $false))

$json = [IO.File]::ReadAllText($manifest)
$json = [regex]::Replace($json, '("version"\s*:\s*")[^"]+(")', "`${1}$Version`${2}", 1)
[IO.File]::WriteAllText($manifest, $json, (New-Object Text.UTF8Encoding $false))

Write-Host "版本号已设为 $Version（Directory.Build.props、extension/manifest.json）"
Write-Host "下一步：dotnet test，再运行 artifacts\package.ps1"
