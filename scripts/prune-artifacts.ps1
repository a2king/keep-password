#requires -Version 7.0
<#
  清理 artifacts 里的历史打包产物，只保留最近 N 个版本。
  默认只“预览”，什么都不删；加 -Apply 才会真的删除。只处理形如 v1.2.3 的版本目录，
  不碰 package.ps1 和其它文件，也不碰名字不规范的目录（例如手工放进去的测试目录）。
#>
param(
  [int]$Keep = 3,
  [switch]$Apply,
  [string]$Path = (Join-Path (Resolve-Path "$PSScriptRoot\..").Path "artifacts")
)

$ErrorActionPreference = "Stop"
if ($Keep -lt 1) { throw "-Keep 至少为 1。" }

$versions = Get-ChildItem -LiteralPath $Path -Directory |
  Where-Object { $_.Name -match '^v(\d+)\.(\d+)\.(\d+)$' } |
  Sort-Object { [version]($_.Name.TrimStart('v')) } -Descending

$stale = @($versions | Select-Object -Skip $Keep)
if ($stale.Count -eq 0) { Write-Host "没有需要清理的版本（共 $($versions.Count) 个，保留 $Keep 个）。"; return }

$total = 0
foreach ($dir in $stale) {
  $size = (Get-ChildItem -LiteralPath $dir.FullName -Recurse -File -Force | Measure-Object Length -Sum).Sum
  $total += $size
  "{0,-10} {1,9:N0} MB" -f $dir.Name, ($size / 1MB)
}
"保留最新的 $Keep 个：" + (($versions | Select-Object -First $Keep).Name -join "、")
"可释放约 {0:N1} GB（{1} 个版本）" -f ($total / 1GB), $stale.Count

if (-not $Apply) { Write-Host "这是预览。确认无误后加 -Apply 才会真的删除。"; return }
foreach ($dir in $stale) { Remove-Item -LiteralPath $dir.FullName -Recurse -Force; "已删除 $($dir.Name)" }
