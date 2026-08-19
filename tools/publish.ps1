# 配布用ビルドを作る。
#   .\tools\publish.ps1      -> dist\VRCAvatarChanger-vX.Y.Z-win-x64.zip
# .NET ランタイム不要の単一 exe(自己完結)を作り、利用者向け README と一緒に zip にする。
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root

# 1. 配布前チェック: VRChat API の User-Agent 連絡先が既定値のままなら止める
$api = Get-Content "src\VRChatApi.cs" -Raw -Encoding UTF8
# 埋め込まれた連絡先(XOR 符号化)を復号し、未設定/ダミーのままなら止める
function Get-EmbeddedContact($src) {
    $keyM = [regex]::Match($src, 'ContactKey\s*=\s*\[([^\]]*)\]')
    $encM = [regex]::Match($src, 'ContactEnc\s*=\s*\[([^\]]*)\]')
    if (-not ($keyM.Success -and $encM.Success)) { return $null }
    $key = [regex]::Matches($keyM.Groups[1].Value, '0x[0-9A-Fa-f]+') | ForEach-Object { [Convert]::ToInt32($_.Value, 16) }
    $enc = [regex]::Matches($encM.Groups[1].Value, '0x[0-9A-Fa-f]+') | ForEach-Object { [Convert]::ToInt32($_.Value, 16) }
    $bytes = for ($i = 0; $i -lt $enc.Count; $i++) { [byte]($enc[$i] -bxor $key[$i % $key.Count]) }
    return [System.Text.Encoding]::UTF8.GetString([byte[]]$bytes)
}
$contact = Get-EmbeddedContact $api
if (-not $contact -or $contact -match 'example\.com' -or $contact -eq 'https://github.com/kotag/VRCAvatarChanger') {
    Write-Host "[publish] src\VRChatApi.cs の連絡先が未設定/ダミーのままです。配布者の連絡先(メールアドレスや実在する配布ページ URL)に変えてから配布してください。" -ForegroundColor Red
    exit 1
}
Write-Host "[publish] 連絡先: $contact" -ForegroundColor DarkGray

# 2. バージョン
$csproj = [xml](Get-Content "src\VRCAvatarChanger.csproj")
$version = $csproj.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { $version = "0.0.0" }

$outDir = Join-Path $root "dist\VRCAvatarChanger"
$zip = Join-Path $root "dist\VRCAvatarChanger-v$version-win-x64.zip"
# 古い配布物(古い連絡先やバージョンのまま)が残らないよう、dist は毎回空にする
$dist = Join-Path $root "dist"
if (Test-Path $dist) { Get-ChildItem $dist -Force | Remove-Item -Recurse -Force }
New-Item -ItemType Directory -Force $dist | Out-Null

# 3. publish(自己完結・単一ファイル)
Write-Host "[publish] building v$version ..." -ForegroundColor Cyan
dotnet publish "src\VRCAvatarChanger.csproj" -c Release -r win-x64 -p:SelfContained=true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none `
    -o $outDir --nologo -v quiet
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

# 4. 同梱物
Copy-Item "docs\README-配布用.txt" (Join-Path $outDir "はじめにお読みください.txt")
Get-ChildItem $outDir -Include "*.pdb","*.xml" -Recurse | Remove-Item -Force

# 5. zip + SHA-256(自動アップデートの検証用)
Compress-Archive -Path (Join-Path $outDir "*") -DestinationPath $zip -CompressionLevel Optimal
$hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLower()
$sums = Join-Path $root "dist\SHA256SUMS.txt"
"$hash  $(Split-Path $zip -Leaf)" | Set-Content $sums -Encoding ASCII
$size = [math]::Round((Get-Item $zip).Length / 1MB, 1)
Write-Host "[publish] done: $zip ($size MB)" -ForegroundColor Green

# 6. GitHub Releases への公開手順の案内
$updater = Get-Content "src\Updater.cs" -Raw -Encoding UTF8
if ($updater -match 'GitHubRepo\s*=\s*""') {
    Write-Host "[publish] 注意: src\Updater.cs の GitHubRepo が未設定のため、利用者側の自動アップデート通知は無効です。" -ForegroundColor Yellow
} else {
    $repo = [regex]::Match($updater, 'GitHubRepo\s*=\s*"([^"]+)"').Groups[1].Value
    Write-Host ""
    Write-Host "GitHub Releases に公開するには (gh CLI):" -ForegroundColor Cyan
    Write-Host "  gh release create v$version `"$zip`" `"$sums`" -R $repo -t `"v$version`" --notes `"変更点をここに`""
    Write-Host "ブラウザから手動で公開する場合は、タグ v$version のリリースを作り、zip と SHA256SUMS.txt の 2 つを添付してください。"
}
