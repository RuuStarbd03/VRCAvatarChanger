# 実験用: VRChat の OSC 入力 (既定 127.0.0.1:9000) に /avatar/change を送り、
# 「同じアバター ID を送ったときにクライアントが再装着するか」を確かめる。
#   .\tools\send-avatar-change.ps1 -AvatarId avtr_xxxxxxxx-....
param(
    [Parameter(Mandatory = $true)][string]$AvatarId,
    [int]$Port = 9000
)
if ($AvatarId -notmatch '^avtr_[0-9a-fA-F-]{36}$') { Write-Error "avtr_ で始まるアバター ID を指定してください"; exit 1 }

# OSC 文字列: ASCII + NUL 終端 + 4 バイト境界までパディング
function ConvertTo-OscString([string]$s) {
    $b = [System.Text.Encoding]::ASCII.GetBytes($s)
    $pad = 4 - (($b.Length + 1) % 4); if ($pad -eq 4) { $pad = 0 }
    return [byte[]]($b + (,[byte]0 * (1 + $pad)))
}

$msg = [byte[]]((ConvertTo-OscString '/avatar/change') + (ConvertTo-OscString ',s') + (ConvertTo-OscString $AvatarId))
$udp = New-Object System.Net.Sockets.UdpClient
try { $udp.Send($msg, $msg.Length, '127.0.0.1', $Port) | Out-Null } finally { $udp.Close() }
Write-Host "sent /avatar/change $AvatarId -> 127.0.0.1:$Port"
