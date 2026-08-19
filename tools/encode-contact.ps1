# 連絡先(メールアドレスや配布ページ URL)を XOR 符号化して、src\VRChatApi.cs に貼るバイト列を出力する。
# 使い方:
#   .\tools\encode-contact.ps1 "you@example.com"
# 出力された ContactEnc の中身を src\VRChatApi.cs の該当箇所に貼り替える(ContactKey はそのまま)。
param([Parameter(Mandatory)][string]$Contact)

$key = [byte[]](0x5A, 0xC3, 0x2F, 0x91, 0x7E, 0x08, 0xB4, 0x6D)  # src\VRChatApi.cs の ContactKey と一致させる
$src = [System.Text.Encoding]::UTF8.GetBytes($Contact)
$parts = for ($i = 0; $i -lt $src.Length; $i++) { "0x{0:X2}" -f ($src[$i] -bxor $key[$i % $key.Length]) }

# 14 個ずつ改行して見やすく
$lines = @()
for ($i = 0; $i -lt $parts.Count; $i += 14) {
    $chunk = $parts[$i..([math]::Min($i + 13, $parts.Count - 1))]
    $lines += "        " + ($chunk -join ", ") + ","
}
Write-Host "src\VRChatApi.cs の ContactEnc をこの中身に置き換えてください:`n"
Write-Host "    private static readonly byte[] ContactEnc ="
Write-Host "    ["
$lines | ForEach-Object { Write-Host $_ }
Write-Host "    ];"
