# 普段使い用のビルド: リポジトリ直下に VRCAvatarChanger.exe を 1 つだけ作る。
#   .\tools\build.ps1
# (.NET ランタイムがある PC 向けの単一 exe。第三者に配るときは publish.ps1 を使う)
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$proj = Join-Path $root "src\VRCAvatarChanger.csproj"
$stage = Join-Path $root "src\obj\single"
$exe = Join-Path $root "VRCAvatarChanger.exe"

if (Get-Process VRCAvatarChanger -ErrorAction SilentlyContinue) {
    Write-Host "[build] VRCAvatarChanger.exe が起動中です。閉じてからもう一度実行してください。" -ForegroundColor Yellow
    exit 1
}

# ソースが変わった以上 dist の配布物は古い(連絡先やバージョンも古い可能性がある)ので捨てる。配布物は publish.ps1 で作り直す
$dist = Join-Path $root "dist"
if (Test-Path $dist) {
    Get-ChildItem $dist -Force | Remove-Item -Recurse -Force
    Write-Host "[build] dist を空にしました(配布物は tools\publish.ps1 で作り直してください)" -ForegroundColor DarkGray
}

Write-Host "[build] building ..." -ForegroundColor Cyan
dotnet publish $proj -c Release -r win-x64 -p:SelfContained=false `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=none `
    -o $stage --nologo -v quiet
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Copy-Item (Join-Path $stage "VRCAvatarChanger.exe") $exe -Force
$size = [math]::Round((Get-Item $exe).Length / 1MB, 1)
Write-Host "[build] done: $exe ($size MB)" -ForegroundColor Green
