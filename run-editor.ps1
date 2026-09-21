$ErrorActionPreference = 'Stop'
$pureDotnet = Join-Path $env:LOCALAPPDATA 'PureEngine/dotnet/dotnet.exe'
if (-not (Test-Path -LiteralPath $pureDotnet)) {
    $pureDotnet = (Get-Command dotnet -ErrorAction Stop).Source
}

Push-Location $PSScriptRoot
try {
    & $pureDotnet run --project './PureEngine.Editor/PureEngine.Editor.csproj'
    if ($LASTEXITCODE -ne 0) { throw "Editor exited with code $LASTEXITCODE." }
}
finally {
    Pop-Location
}
