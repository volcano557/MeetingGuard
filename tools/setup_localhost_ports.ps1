# =============================================================================
# setup_localhost_ports.ps1
# 将 WSL 后端端口转发到 Windows 127.0.0.1，使前端始终用 localhost 连接。
#
# 用法（必须以管理员身份运行 PowerShell）:
#   cd D:\信安赛\tools
#   .\setup_localhost_ports.ps1
#
# WSL 每次重启后 IP 可能变化，需重新运行本脚本（appsettings 无需修改）。
# =============================================================================

#Requires -RunAsAdministrator

$ErrorActionPreference = "Stop"

function Get-WslIp {
    $ip = $null
    try {
        $ip = (wsl bash -c "hostname -I 2>/dev/null | awk '{print \$1}'").Trim()
    } catch {}
    if (-not $ip) {
        try {
            $ip = (wsl bash -c "ip addr show eth0 2>/dev/null | grep 'inet ' | awk '{print \$2}' | cut -d/ -f1").Trim()
        } catch {}
    }
    if (-not $ip) { throw "无法获取 WSL IP，请确认 WSL 已启动。" }
    return $ip
}

$wslIp = Get-WslIp
$ports = @(5001, 50053, 8003, 8000)

Write-Host ""
Write-Host "============================================" -ForegroundColor Cyan
Write-Host " MeetingGuard 本地端口转发 (127.0.0.1)" -ForegroundColor Cyan
Write-Host "============================================" -ForegroundColor Cyan
Write-Host "  WSL IP : $wslIp" -ForegroundColor Green
Write-Host ""

foreach ($port in $ports) {
    # 清除旧规则（127.0.0.1 与 0.0.0.0 两种 listen 地址）
    netsh interface portproxy delete v4tov4 listenport=$port listenaddress=127.0.0.1 2>$null | Out-Null
    netsh interface portproxy delete v4tov4 listenport=$port listenaddress=0.0.0.0 2>$null | Out-Null

    netsh interface portproxy add v4tov4 `
        listenport=$port listenaddress=127.0.0.1 `
        connectport=$port connectaddress=$wslIp | Out-Null

    Write-Host "  [OK] 127.0.0.1:$port  ->  ${wslIp}:$port" -ForegroundColor Green
}

Write-Host ""
Write-Host "连通性测试：" -ForegroundColor Yellow
foreach ($port in $ports) {
    $r = Test-NetConnection -ComputerName 127.0.0.1 -Port $port -WarningAction SilentlyContinue
    $status = if ($r.TcpTestSucceeded) { "可达" } else { "不可达（请确认 WSL 中对应服务已启动）" }
    $color  = if ($r.TcpTestSucceeded) { "Green" } else { "Red" }
    Write-Host "  127.0.0.1:$port  $status" -ForegroundColor $color
}

Write-Host ""
Write-Host "前端 appsettings.json 已固定使用 127.0.0.1，无需再改 IP。" -ForegroundColor Cyan
Write-Host "服务地址：" -ForegroundColor Cyan
Write-Host "  人脸保护   http://127.0.0.1:5001"  -ForegroundColor White
Write-Host "  AIGC 检测  http://127.0.0.1:50053" -ForegroundColor White
Write-Host "  语义风险   http://127.0.0.1:8003"  -ForegroundColor White
Write-Host "  统一网关   http://127.0.0.1:8000"  -ForegroundColor White
Write-Host ""
Write-Host "注意：WSL 重启后请重新运行本脚本。" -ForegroundColor Yellow
Write-Host ""
