param([switch]$Check)

$ErrorActionPreference = 'Stop'
Push-Location (Split-Path -Parent $PSScriptRoot)
try {
    $sdkVersion = (dotnet --version).Trim()
    if ($LASTEXITCODE -ne 0) { throw 'Install the SDK specified by global.json.' }
    $sdkEntry = dotnet --list-sdks | Where-Object { $_ -match "^$([regex]::Escape($sdkVersion)) \[(.+)\]$" }
    if (!$sdkEntry) { throw "Cannot locate SDK $sdkVersion." }
    $sdkRoot = $sdkEntry.Substring($sdkEntry.IndexOf('[') + 1).TrimEnd(']')
    # Invoke the selected SDK's formatter directly; the RC1 CLI resolves its path incorrectly.
    $formatter = Join-Path $sdkRoot "$sdkVersion/DotnetTools/dotnet-format/dotnet-format.dll"

    dotnet restore PureEngine.slnx
    if ($LASTEXITCODE -ne 0) { throw 'Restore failed.' }

    if (!$Check) {
        foreach ($mode in @('style', 'analyzers')) {
            # Signature changes need review: lifecycle callbacks and bindings use reflection.
            dotnet $formatter $mode PureEngine.slnx --no-restore --severity info --exclude-diagnostics CA1050 IDE0130 IDE0160 IDE0161 CA1822 IDE0060 CA1068
            if ($LASTEXITCODE -ne 0) { throw "$mode fixes failed." }
        }
    }
    foreach ($mode in @('style', 'analyzers')) {
        dotnet $formatter $mode PureEngine.slnx --no-restore --severity info --verify-no-changes
        if ($LASTEXITCODE -ne 0) { throw "$mode diagnostics remain. Review the reported locations." }
    }

    dotnet build PureEngine.slnx --no-restore --warnaserror
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    foreach ($project in @('tests/PureEngine.Core.Checks', 'tests/PureEngine.Editor.Checks')) {
        dotnet run --project $project --no-build
        if ($LASTEXITCODE -ne 0) { throw "$project failed." }
    }
}
finally { Pop-Location }
