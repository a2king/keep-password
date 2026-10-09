param(
    [Parameter(Mandatory = $true)][string]$HostExe,
    [Parameter(Mandatory = $true)][string]$ExtensionId
)

$ErrorActionPreference = "Stop"
$hostExe = (Resolve-Path $HostExe).Path
$dir = Join-Path $env:LOCALAPPDATA "KeepPassword"
New-Item -ItemType Directory -Force -Path $dir | Out-Null
$manifestPath = Join-Path $dir "com.keeppassword.host.json"
@{
    name = "com.keeppassword.host"
    description = "Keep Password 本机消息宿主"
    path = $hostExe
    type = "stdio"
    allowed_origins = @("chrome-extension://$ExtensionId/")
} | ConvertTo-Json | Set-Content -Path $manifestPath -Encoding UTF8

$keys = @(
    "HKCU:\Software\Google\Chrome\NativeMessagingHosts\com.keeppassword.host",
    "HKCU:\Software\Microsoft\Edge\NativeMessagingHosts\com.keeppassword.host"
)
foreach ($key in $keys) {
    New-Item -Path $key -Force | Out-Null
    Set-ItemProperty -Path $key -Name "(default)" -Value $manifestPath
}

Write-Host "已写入 $manifestPath"
Write-Host "Chrome 与 Edge 的 Native Messaging 注册表项已更新。"
