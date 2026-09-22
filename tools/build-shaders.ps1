$ErrorActionPreference = 'Stop'
$compiler = Join-Path $PSScriptRoot '.cache/glslang/bin/glslang.exe'
if (!(Test-Path $compiler)) {
    New-Item -ItemType Directory -Force "$PSScriptRoot/.cache" | Out-Null
    Invoke-WebRequest 'https://github.com/KhronosGroup/glslang/releases/download/16.6.0/glslang-16.6.0-windows-x86_64-release.zip' -OutFile "$PSScriptRoot/.cache/glslang.zip"
    if ((Get-FileHash "$PSScriptRoot/.cache/glslang.zip").Hash -ne '82BF434E69B9BB4829DE7E2B4BC2C5E7A7861E53D66CF75E5CC70F5F694A8D9B') { throw 'Unexpected glslang archive hash.' }
    Expand-Archive "$PSScriptRoot/.cache/glslang.zip" "$PSScriptRoot/.cache/glslang" -Force
}
$shaderRoot = Join-Path $PSScriptRoot '../src/PureEngine.Rendering/Shaders'
$hashes = @('<Project>', '  <ItemGroup>')
foreach ($stage in @('vert', 'frag')) {
    $source = Join-Path $shaderRoot "quad.$stage"
    & $compiler -V --target-env vulkan1.1 $source -o "$source.spv"
    if ($LASTEXITCODE -ne 0) { throw "Shader compilation failed: $source" }
    foreach ($file in @($source, "$source.spv")) {
        $name = Split-Path $file -Leaf
        $hash = (Get-FileHash $file).Hash
        $hashes += "    <ShaderFile Include=`"Shaders/$name`" ExpectedHash=`"$hash`" />"
    }
}
$hashes += @('  </ItemGroup>', '</Project>')
$hashes | Set-Content (Join-Path $shaderRoot 'Hashes.props')
