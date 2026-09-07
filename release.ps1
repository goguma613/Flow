<#
    새 버전을 내는 스크립트.

    사용법:
        .\release.ps1 1.0.1
        .\release.ps1 1.0.1 -Notes "최소화 버튼이 안 보이던 문제를 고쳤습니다"

    하는 일:
        1. Flow.csproj 의 <Version> 을 올린다
        2. Release 로 단일 exe 를 만든다
        3. 커밋 · 태그 · 푸시
        4. GitHub Releases 에 Flow.exe 를 올린다

    앱은 하루 한 번 이 릴리스를 확인해 새 버전이면 받아 둔다.
    exe 이름이 반드시 Flow.exe 여야 앱이 찾을 수 있다.
#>

param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version,

    [string]$Notes = ''
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$csproj = Join-Path $root 'Flow\Flow.csproj'
$dist = Join-Path $root 'dist'
$exe = Join-Path $dist 'Flow.exe'

Write-Host "▸ 버전 $Version 준비" -ForegroundColor Cyan

# ── 필요한 도구 확인
foreach ($tool in @('dotnet', 'git', 'gh')) {
    if (-not (Get-Command $tool -ErrorAction SilentlyContinue)) {
        throw "$tool 을(를) 찾을 수 없습니다. 설치하거나 PATH 를 확인하세요."
    }
}

if ((gh auth status 2>&1 | Out-String) -notmatch 'Logged in') {
    throw "GitHub 로그인이 필요합니다. 먼저 'gh auth login' 을 실행하세요."
}

# ── 1. 버전 올리기
$text = Get-Content $csproj -Raw
if ($text -notmatch '<Version>[\d.]+</Version>') { throw "Flow.csproj 에서 <Version> 을 찾지 못했습니다." }
$text = $text -replace '<Version>[\d.]+</Version>', "<Version>$Version</Version>"
Set-Content $csproj -Value $text -Encoding utf8 -NoNewline
Write-Host "  버전 표기 완료"

# ── 2. 빌드
Write-Host "▸ 빌드 중…" -ForegroundColor Cyan
Get-Process Flow -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 1

dotnet publish $csproj -c Release -o $dist --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "빌드에 실패했습니다." }

foreach ($junk in @('libHarfBuzzSharp.pdb', 'libSkiaSharp.pdb')) {
    $q = Join-Path $dist $junk
    if (Test-Path $q) { Remove-Item -LiteralPath $q -Force }
}

if (-not (Test-Path $exe)) { throw "Flow.exe 가 만들어지지 않았습니다." }
$mb = [math]::Round((Get-Item $exe).Length / 1MB, 1)
Write-Host "  Flow.exe $mb MB"

# ── 3. 커밋 · 태그 · 푸시
Write-Host "▸ 커밋과 태그" -ForegroundColor Cyan
git -C $root add -A
if ((git -C $root status --porcelain).Length -gt 0) {
    git -C $root commit -m "Release v$Version"
}
git -C $root tag -f "v$Version"
git -C $root push origin HEAD
git -C $root push origin "v$Version" --force

# ── 4. 릴리스
Write-Host "▸ GitHub 릴리스 올리는 중…" -ForegroundColor Cyan
if ([string]::IsNullOrWhiteSpace($Notes)) { $Notes = "Flow $Version" }

if ((gh release view "v$Version" --repo goguma613/Flow 2>&1 | Out-String) -notmatch 'release not found') {
    gh release delete "v$Version" --repo goguma613/Flow --yes --cleanup-tag 2>&1 | Out-Null
    git -C $root push origin "v$Version" --force
}

gh release create "v$Version" $exe `
    --repo goguma613/Flow `
    --title "v$Version" `
    --notes $Notes

Write-Host ""
Write-Host "✓ v$Version 배포 완료" -ForegroundColor Green
Write-Host "  https://github.com/goguma613/Flow/releases/tag/v$Version"
Write-Host "  쓰고 계신 앱들은 하루 안에 알아서 새 버전을 받습니다."
