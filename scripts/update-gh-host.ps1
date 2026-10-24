param([Parameter(Mandatory=$true)][string]$NewIp)
$ErrorActionPreference = 'Stop'
$enc = New-Object System.Text.UTF8Encoding($false)
$h = 'C:\Windows\System32\drivers\etc\hosts'
$t = [IO.File]::ReadAllText($h)
# 把当前 github.com / api.github.com 行的 IP 换成新 IP（匹配任意旧 IP）
$t = $t -replace '(?m)^\d+\.\d+\.\d+\.\d+(\s+)github\.com\s*$', "$NewIp`$1github.com"
$t = $t -replace '(?m)^\d+\.\d+\.\d+\.\d+(\s+)api\.github\.com\s*$', "$NewIp`$1api.github.com"
[IO.File]::WriteAllText($h, $t, $enc)
Write-Output "hosts github.com/api.github.com -> $NewIp"
