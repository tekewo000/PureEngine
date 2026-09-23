param([string]$ValidationLayerPath)
$ErrorActionPreference = 'Stop'
Push-Location (Split-Path -Parent $PSScriptRoot)
$previousLayerPath = $env:VK_LAYER_PATH
$previousValidation = $env:PUREENGINE_VULKAN_VALIDATION
$previousSync = $env:VK_LAYER_VALIDATE_SYNC
try {
    if ($ValidationLayerPath) { $env:VK_LAYER_PATH = (Resolve-Path $ValidationLayerPath).Path }
    $env:PUREENGINE_VULKAN_VALIDATION = '1'
    $env:VK_LAYER_VALIDATE_SYNC = '1'
    dotnet build tests/PureEngine.Editor.Checks --warnaserror
    if ($LASTEXITCODE -ne 0) { throw 'GPU check build failed.' }
    $output = & tests/PureEngine.Editor.Checks/bin/Debug/net11.0/PureEngine.Editor.Checks.exe --vulkan 2>&1
    $exitCode = $LASTEXITCODE
    $output | Write-Output
    $completed = $output | Select-String -SimpleMatch 'Vulkan Editor GPU checks passed.'
    $visualReady = $env:PUREENGINE_VISUAL_CHECK -eq '1' -and
        ($output | Select-String -SimpleMatch 'Visual inspection ready; close the window to finish.')
    if ($exitCode -ne 0 -or ($output | Select-String 'Validation Error|VUID-|SYNC-HAZARD') -or
        (!$completed -and !$visualReady)) {
        throw 'GPU validation failed. See the messages above.'
    }
}
finally {
    $env:VK_LAYER_PATH = $previousLayerPath
    $env:PUREENGINE_VULKAN_VALIDATION = $previousValidation
    $env:VK_LAYER_VALIDATE_SYNC = $previousSync
    Pop-Location
}
