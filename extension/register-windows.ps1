[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string]$HostExe,
    [Parameter(Mandatory = $true)][ValidatePattern('^[a-p]{32}$')][string]$ExtensionId
)

$ErrorActionPreference = "Stop"
if ([string]::IsNullOrWhiteSpace($HostExe)) {
    $installDir = Split-Path -Parent $PSScriptRoot
    $candidates = @(
        (Join-Path $installDir "KeepPassword.NativeHost.exe"),
        (Join-Path $installDir "native-host\KeepPassword.NativeHost.exe")
    )
    $HostExe = $candidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
    if ([string]::IsNullOrWhiteSpace($HostExe)) {
        throw "未找到 KeepPassword.NativeHost.exe。请用 -HostExe 指定已发布或安装的宿主文件。"
    }
}
$hostPath = (Resolve-Path -LiteralPath $HostExe).ProviderPath
if (-not (Test-Path -LiteralPath $hostPath -PathType Leaf) -or [IO.Path]::GetExtension($hostPath) -ne '.exe') {
    throw "HostExe 必须是 KeepPassword.NativeHost.exe 文件，不能是目录或 DLL。"
}
$dir = Join-Path $env:LOCALAPPDATA "KeepPassword"
$manifestPath = Join-Path $dir "com.keeppassword.host.json"
$origins = @("chrome-extension://$ExtensionId/")
if (Test-Path -LiteralPath $manifestPath -PathType Leaf) {
    $existing = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $origins += @($existing.allowed_origins | Where-Object { $_ -cmatch '^chrome-extension://[a-p]{32}/$' })
}
$manifest = @{
    name = "com.keeppassword.host"
    description = "Keep Password 本机消息宿主"
    path = $hostPath
    type = "stdio"
    allowed_origins = @($origins | Select-Object -Unique)
} | ConvertTo-Json

if (-not $PSCmdlet.ShouldProcess("$manifestPath (Chrome / Edge, current user)", "Register Keep Password native host")) {
    return
}

New-Item -ItemType Directory -Force -Path $dir | Out-Null
# Chrome requires UTF-8 JSON. Avoid the BOM emitted by Windows PowerShell 5.1's UTF8 encoding.
[IO.File]::WriteAllText($manifestPath, $manifest, (New-Object Text.UTF8Encoding($false)))

$keys = @(
    "HKCU:\Software\Google\Chrome\NativeMessagingHosts\com.keeppassword.host",
    "HKCU:\Software\Microsoft\Edge\NativeMessagingHosts\com.keeppassword.host"
)
foreach ($key in $keys) {
    New-Item -Path $key -Force | Out-Null
    Set-Item -Path $key -Value $manifestPath
}

Write-Host "已写入 $manifestPath"
Write-Host "Chrome 与 Edge 的 Native Messaging 注册表项已更新。"
Write-Host "宿主: $hostPath"
Write-Host "请启动并解锁 Keep Password，刷新登录网页，点击密码框后使用扩展按钮或 Ctrl+Shift+L。"
