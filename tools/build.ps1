# 普段使い用のビルド: リポジトリ直下に VRCAvatarChanger.exe を 1 つだけ作る。
#   .\tools\build.ps1
# (.NET ランタイムがある PC 向けの単一 exe。第三者に配るときは publish.ps1 を使う)
# 起動中なら終了してからビルドし、終わったら起動し直す。
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$proj = Join-Path $root "src\VRCAvatarChanger.csproj"
$stage = Join-Path $root "src\obj\single"
$exe = Join-Path $root "VRCAvatarChanger.exe"

# 起動中の exe は上書きできない。VRChat 連動がオンだと × で閉じてもトレイに残っているので、ここで終了させる。
# 「閉じる」を送るとトレイへ隠れるだけなので、強制終了する (設定などの保存はアトミックなので壊れない)。
# 二重起動防止で、残っていると新しい exe を起動しても古い方が前に出るだけになるため、どこから起動したものでも終わらせる
$relaunchArgs = $null
$running = @(Get-CimInstance Win32_Process -Filter "Name = 'VRCAvatarChanger.exe'")
if ($running.Count -gt 0) {
    $relaunchArgs = if ($running | Where-Object { $_.CommandLine -match '--tray' }) { "--tray" } else { "" }
    foreach ($p in $running) { Stop-Process -Id $p.ProcessId -Force -ErrorAction SilentlyContinue }
    # 強制終了は非同期なので、一覧から消えるまで待つ (最大 10 秒)
    $deadline = (Get-Date).AddSeconds(10)
    while ((Get-Process VRCAvatarChanger -ErrorAction SilentlyContinue) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 200 }
    if (Get-Process VRCAvatarChanger -ErrorAction SilentlyContinue) {
        Write-Host "[build] VRCAvatarChanger.exe を終了できませんでした。タスクマネージャーで終了してからもう一度実行してください。" -ForegroundColor Yellow
        exit 1
    }
    Write-Host "[build] 起動中の VRCAvatarChanger を終了しました" -ForegroundColor DarkGray
}

Write-Host "[build] building ..." -ForegroundColor Cyan
dotnet publish $proj -c Release -r win-x64 -p:SelfContained=false `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=none `
    -o $stage --nologo -v quiet
if ($LASTEXITCODE -ne 0) {
    Write-Host "[build] ビルドに失敗しました (exe と dist はそのまま)" -ForegroundColor Red
    exit $LASTEXITCODE
}

Copy-Item (Join-Path $stage "VRCAvatarChanger.exe") $exe -Force
$size = [math]::Round((Get-Item $exe).Length / 1MB, 1)
Write-Host "[build] done: $exe ($size MB)" -ForegroundColor Green

# ソースが変わった以上 dist の配布物は古い(連絡先やバージョンも古い可能性がある)ので捨てる。配布物は publish.ps1 で作り直す
# (ビルドに失敗したときに配布物だけ消えないよう、成功してから消す)
$dist = Join-Path $root "dist"
if ((Test-Path $dist) -and (Get-ChildItem $dist -Force)) {
    Get-ChildItem $dist -Force | Remove-Item -Recurse -Force
    Write-Host "[build] dist を空にしました(配布物は tools\publish.ps1 で作り直してください)" -ForegroundColor DarkGray
}

if ($null -ne $relaunchArgs) {
    if ($relaunchArgs) { Start-Process $exe -ArgumentList $relaunchArgs } else { Start-Process $exe }
    Write-Host "[build] 起動し直しました" -ForegroundColor DarkGray
}
