$ErrorActionPreference = 'Stop'
$pureDotnet = Join-Path $env:LOCALAPPDATA 'PureEngine/dotnet/dotnet.exe'
if (-not (Test-Path -LiteralPath $pureDotnet)) {
    $pureDotnet = (Get-Command dotnet -ErrorAction Stop).Source
}

$pureDotnetRoot = Split-Path -Parent $pureDotnet
$pureEnvironment = @{}
foreach ($name in @('DOTNET_ROOT', 'DOTNET_ROOT_X64', 'DOTNET_HOST_PATH', 'DOTNET_MSBUILD_SDK_RESOLVER_CLI_DIR', 'PATH')) {
    $pureEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
}
Push-Location $PSScriptRoot
try {
    $env:DOTNET_ROOT = $pureDotnetRoot
    $env:DOTNET_ROOT_X64 = $pureDotnetRoot
    $env:DOTNET_HOST_PATH = $pureDotnet
    $env:DOTNET_MSBUILD_SDK_RESOLVER_CLI_DIR = $pureDotnetRoot
    $env:PATH = "$pureDotnetRoot;$env:PATH"
    Write-Host "Using .NET: $pureDotnet"
    & $pureDotnet --version
    if ($LASTEXITCODE -ne 0) { throw "Required SDK could not be resolved using $pureDotnet. See global.json." }
    & $pureDotnet run --project '../src/PureEngine.Editor/PureEngine.Editor.csproj'
    if ($LASTEXITCODE -ne 0) { throw "Editor exited with code $LASTEXITCODE." }
}
finally {
    foreach ($name in $pureEnvironment.Keys) {
        [Environment]::SetEnvironmentVariable($name, $pureEnvironment[$name], 'Process')
    }
    Pop-Location
}
