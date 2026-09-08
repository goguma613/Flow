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

$previous = $ErrorActionPreference
$ErrorActionPreference = 'Continue'
gh auth status *> $null
$loggedIn = ($LASTEXITCODE -eq 0)
$ErrorActionPreference = $previous

if (-not $loggedIn) { throw "GitHub 로그인이 필요합니다. 먼저 'gh auth login' 을 실행하세요." }

# ── 1. 버전 올리기
# -Encoding 을 빼면 5.1 이 UTF-8 파일을 ANSI 로 읽어 한글이 깨진다
$text = Get-Content $csproj -Raw -Encoding UTF8
if ($text -notmatch '<Version>[\d.]+</Version>') { throw 'Flow.csproj 에서 Version 태그를 찾지 못했습니다.' }
$text = $text -replace '<Version>[\d.]+</Version>', "<Version>$Version</Version>"
Set-Content $csproj -Value $text -Encoding utf8 -NoNewline
Write-Host "  버전 표기 완료"

# ── 2. 빌드
Write-Host "▸ 빌드 중…" -ForegroundColor Cyan
# 이 저장소 안에서 돌고 있는 것만 닫는다.
# 사용자가 쓰고 있는 실제사용파일\Flow.exe 를 죽이면 저장 안 된 입력이 날아간다.
Get-Process Flow -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -and $_.Path.StartsWith($root, [StringComparison]::OrdinalIgnoreCase) } |
    Stop-Process -Force
Start-Sleep -Seconds 1

dotnet publish $csproj -c Release -o $dist --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "빌드에 실패했습니다." }

foreach ($junk in @('libHarfBuzzSharp.pdb', 'libSkiaSharp.pdb')) {
    $q = Join-Path $dist $junk
    if (Test-Path $q) { Remove-Item -LiteralPath $q -Force }
}

if (-not (Test-Path $exe)) { throw "Flow.exe 가 만들어지지 않았습니다." }

# 배포용 안내문을 함께 둔다 (원본은 저장소 루트에 있다)
$guide = Join-Path $root '사용법.txt'
if (Test-Path $guide) { Copy-Item $guide (Join-Path $dist '사용법.txt') -Force }
$mb = [math]::Round((Get-Item $exe).Length / 1MB, 1)
Write-Host "  Flow.exe $mb MB"

# ── 3. 커밋 · 태그 · 푸시
Write-Host "▸ 커밋과 태그" -ForegroundColor Cyan
# git 도 줄바꿈 경고 같은 것을 stderr 로 낸다. 5.1 은 그걸 오류로 승격시켜 배포를 멈춘다.
# gh 와 같은 이유로 잠시 꺼 두고 종료 코드만 본다.
$previous = $ErrorActionPreference
$ErrorActionPreference = 'Continue'

git -C $root add -A
if ($LASTEXITCODE -ne 0) { $ErrorActionPreference = $previous; throw "git add 에 실패했습니다." }

if ((git -C $root status --porcelain).Length -gt 0) {
    $message = "Release v$Version`n`nCo-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
    git -C $root commit -m $message
    if ($LASTEXITCODE -ne 0) { $ErrorActionPreference = $previous; throw "커밋에 실패했습니다." }
}

git -C $root tag -f "v$Version"
git -C $root push origin HEAD
if ($LASTEXITCODE -ne 0) { $ErrorActionPreference = $previous; throw "푸시에 실패했습니다." }
git -C $root push origin "v$Version" --force

$ErrorActionPreference = $previous

# ── 4. 릴리스
Write-Host "▸ GitHub 릴리스 올리는 중…" -ForegroundColor Cyan
if ([string]::IsNullOrWhiteSpace($Notes)) { $Notes = "Flow $Version" }

# PowerShell 5.1 은 네이티브 명령의 stderr 를 오류로 승격시킨다.
# 여기서는 종료 코드만 보면 되므로 잠시 꺼 둔다.
$previous = $ErrorActionPreference
$ErrorActionPreference = 'Continue'
gh release view "v$Version" --repo goguma613/Flow *> $null
$alreadyPublished = ($LASTEXITCODE -eq 0)
$ErrorActionPreference = $previous

if ($alreadyPublished) {
    Write-Host "  같은 버전이 이미 있어 지우고 다시 올립니다"
    gh release delete "v$Version" --repo goguma613/Flow --yes
    git -C $root push origin "v$Version" --force
}

# 여러 줄 문자열을 네이티브 명령에 직접 넘기면 PowerShell 5.1 이 인자를 쪼갠다.
# 파일로 넘기면 그런 일이 없다.
$notesFile = Join-Path $env:TEMP "flow_release_notes.md"
Set-Content -LiteralPath $notesFile -Value $Notes -Encoding utf8

gh release create "v$Version" $exe --repo goguma613/Flow --title "v$Version" --notes-file $notesFile
if ($LASTEXITCODE -ne 0) { throw "릴리스 업로드에 실패했습니다." }

Remove-Item -LiteralPath $notesFile -Force -ErrorAction SilentlyContinue

Write-Host ""
Write-Host "✓ v$Version 배포 완료" -ForegroundColor Green
Write-Host "  https://github.com/goguma613/Flow/releases/tag/v$Version"
Write-Host "  쓰고 계신 앱들은 하루 안에 알아서 새 버전을 받습니다."
