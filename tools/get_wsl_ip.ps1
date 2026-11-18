# =============================================================================
# get_wsl_ip.ps1  （已弃用，请改用 setup_localhost_ports.ps1）
#
# MeetingGuard 前端固定使用 127.0.0.1，不再写入 WSL IP 到 appsettings。
# 请以管理员身份运行：
#   .\setup_localhost_ports.ps1
# =============================================================================

Write-Host ""
Write-Host "此脚本已弃用。" -ForegroundColor Yellow
Write-Host "请改用（管理员 PowerShell）：" -ForegroundColor Cyan
Write-Host "  .\setup_localhost_ports.ps1" -ForegroundColor White
Write-Host ""
Write-Host "它会将 WSL 端口转发到 127.0.0.1，前端无需修改 IP。" -ForegroundColor Gray
Write-Host ""

$setup = Join-Path $PSScriptRoot "setup_localhost_ports.ps1"
if (Test-Path $setup) {
    $ans = Read-Host "是否现在运行 setup_localhost_ports.ps1? (Y/N)"
    if ($ans -eq 'Y' -or $ans -eq 'y') {
        & $setup
    }
}
