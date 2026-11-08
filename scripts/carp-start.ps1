# Carp 开机自启（Windows 测试机用，非生产）。
# 用法（普通 PowerShell，无需管理员会提示提权）：
#   powershell -ExecutionPolicy Bypass -File scripts\carp-start.ps1
# 效果：注册计划任务 "Carp"，登录时在后台启动 <exe 同级>\carp.exe serve。
# 数据目录自动是 <exe 同级>\data；卸载：Unregister-ScheduledTask -TaskName Carp -Confirm:$false
$ErrorActionPreference = 'Stop'

$exe = Join-Path $PSScriptRoot '..\dist\carp.exe'
if (-not (Test-Path $exe)) {
    $exe = Read-Host 'carp.exe 路径（例如 D:\a\carp.exe）'
}
$exe = (Resolve-Path $exe).Path
$work = Split-Path -Parent $exe

$action = New-ScheduledTaskAction -Execute $exe -Argument 'serve -addr 127.0.0.1:50011 -profile low' -WorkingDirectory $work
$trigger = New-ScheduledTaskTrigger -AtLogOn
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit (New-TimeSpan -Days 0)
$principal = New-ScheduledTaskPrincipal -UserId $env:USERNAME -LogonType Interactive -RunLevel Highest

Register-ScheduledTask -TaskName 'Carp' -Action $action -Trigger $trigger -Settings $settings -Principal $principal -Force | Out-Null
Write-Host "已注册计划任务 Carp：$exe"
Write-Host "启动：Start-ScheduledTask -TaskName Carp；查看：Get-ScheduledTaskInfo -TaskName Carp"
