# setup_portproxy.ps1
# WSL2 起動後に pgvector (port 5432) を LAN に公開する
# 使い方: タスクスケジューラでログオン時に管理者権限で実行
#   schtasks /Create /SC ONLOGON /TN "WSL2 portproxy pgvector" /TR "powershell -ExecutionPolicy Bypass -File C:\path\to\setup_portproxy.ps1" /RU SYSTEM /RL HIGHEST /F

$port = 5432

# WSL2 が起動するまで待つ（ログオン直後は WSL2 が未起動の場合がある）
Start-Sleep -Seconds 8

# WSL2 の IP を取得
$wslIp = (wsl -- bash -c "hostname -I").Trim().Split()[0]
if (-not $wslIp) {
    Write-Error "WSL2 IP が取得できませんでした"
    exit 1
}

Write-Host "WSL2 IP: $wslIp"

# 既存ルールを削除（冪等化）
netsh interface portproxy delete v4tov4 listenaddress=0.0.0.0 listenport=$port 2>$null

# ポート転送ルールを追加
netsh interface portproxy add v4tov4 `
    listenaddress=0.0.0.0 `
    listenport=$port `
    connectaddress=$wslIp `
    connectport=$port

# Firewall ルールが未登録なら追加
$ruleName = "WSL2 pgvector $port"
if (-not (Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue)) {
    New-NetFirewallRule -DisplayName $ruleName -Direction Inbound -Action Allow -Protocol TCP -LocalPort $port | Out-Null
    Write-Host "Firewall rule added: $ruleName"
}

Write-Host "portproxy OK: 0.0.0.0:$port -> $wslIp`:$port"
