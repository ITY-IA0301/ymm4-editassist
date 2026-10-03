[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$Ymm4Directory, [string]$DotnetPath = 'dotnet')
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0
function Invoke-Dotnet([string[]]$Arguments) {
    & $DotnetPath @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet failed: exit $LASTEXITCODE" }
}
try {
    $Ymm4Directory = [IO.Path]::GetFullPath($Ymm4Directory.Trim().Trim('"'))
    if ([IO.Path]::GetExtension($Ymm4Directory) -eq '.exe') { $Ymm4Directory = [IO.Path]::GetDirectoryName($Ymm4Directory) }
    if ($Ymm4Directory.Contains(';')) { throw 'Use a host path without semicolons.' }
    $exe = Join-Path $Ymm4Directory 'YukkuriMovieMaker.exe'
    if (-not (Test-Path -LiteralPath $exe)) { throw 'YukkuriMovieMaker.exe not found.' }
    if ((Get-Item -LiteralPath $exe).VersionInfo.FileVersion -ne '4.56.1.1') { throw 'This experimental release supports YMM4 4.56.1.1 only.' }
    $vendorHash = (Get-FileHash -LiteralPath (Join-Path $PSScriptRoot 'vendor/0Harmony.dll') -Algorithm SHA256).Hash
    if ($vendorHash -ne 'FD77B88724F4104440DF0CF979A851D35EEC75EA3A7E86297D04ABE47C71AFF6') { throw 'Harmony vendor hash mismatch.' }
    $artifacts = Join-Path $PSScriptRoot 'artifacts'
    $build = Join-Path $artifacts 'build'
    $tests = Join-Path $artifacts 'checks'
    $stage = Join-Path $artifacts ('stage-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $stage -Force | Out-Null
    Push-Location $PSScriptRoot
    try {
        Invoke-Dotnet @('build', 'src/PreviewLite/PreviewLite.csproj', '-t:Rebuild', '-c', 'Release', "-p:Ymm4Directory=$Ymm4Directory", '-o', $build, '-m:1', '-nr:false', '-p:UseSharedCompilation=false')
        Invoke-Dotnet @('build', 'tests/PreviewLite.Checks/PreviewLite.Checks.csproj', '-t:Rebuild', '-c', 'Release', "-p:Ymm4Directory=$Ymm4Directory", '-o', $tests, '-m:1', '-nr:false', '-p:UseSharedCompilation=false')
        Invoke-Dotnet @((Join-Path $tests 'PreviewLite.Checks.dll'), $Ymm4Directory)
    } finally { Pop-Location }
    Copy-Item -LiteralPath (Join-Path $build 'YMM4.PreviewLite.dll') -Destination $stage
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'vendor/0Harmony.dll') -Destination $stage
    foreach ($doc in @('README.md', 'VALIDATION.md', 'NOTICES.md')) { Copy-Item -LiteralPath (Join-Path $PSScriptRoot $doc) -Destination $stage }
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'vendor/Harmony-LICENSE.txt') -Destination $stage
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $package = Join-Path $artifacts 'PreviewLite-0.3.0-net10.ymme'
    if (Test-Path -LiteralPath $package) { Remove-Item -LiteralPath $package -Force }
    [IO.Compression.ZipFile]::CreateFromDirectory($stage, $package, [IO.Compression.CompressionLevel]::Optimal, $false)
    Write-Host "Package: $package"
    Write-Host 'Nothing was installed. Live playback and export still require testing in YMM4.'
    exit 0
} catch { Write-Error $_; exit 1 }
