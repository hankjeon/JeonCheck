#Requires -Version 5.1
$ErrorActionPreference = 'Stop'

$projectPath = Join-Path $PSScriptRoot 'dotnetapp\JeonCheck\JeonCheck.csproj'

if (-not (Get-Command 'dotnet' -ErrorAction SilentlyContinue)) {
    Write-Error 'dotnet CLI를 찾을 수 없습니다. .NET 10 SDK를 설치한 뒤 다시 실행하세요.'
    exit 1
}

if (-not (Test-Path -LiteralPath $projectPath -PathType Leaf)) {
    Write-Error "프로젝트 파일을 찾을 수 없습니다: $projectPath"
    exit 1
}

Push-Location -LiteralPath $PSScriptRoot
try {
    & dotnet run --project $projectPath @args
    exit $LASTEXITCODE
}
finally {
    Pop-Location
}
