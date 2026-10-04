[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('EditAssistVersionChecks-' + [Guid]::NewGuid().ToString('N'))
$sourceScript = Join-Path $PSScriptRoot '../src/EditAssist.VersionManager/Apply-Version.ps1'
$checks = 0
function Assert-Check([bool]$condition, [string]$name) {
    if (-not $condition) { throw ('FAIL ' + $name) }
    $script:checks++
    Write-Host ('PASS ' + $name)
}
function Make-Fixture([string]$name) {
    $fixture = Join-Path $testRoot $name
    $target = Join-Path $fixture 'user/plugin/EditAssist'
    $storagePath = Join-Path $fixture 'user/EditAssistVersions'
    $requestPath = Join-Path $storagePath 'requests/test'
    $stagePath = Join-Path $requestPath 'stage'
    New-Item -ItemType Directory -Force -Path $target, $stagePath | Out-Null
    [IO.File]::WriteAllText((Join-Path $target 'YMM4.EditAssist.dll'), 'old')
    [IO.File]::WriteAllText((Join-Path $stagePath 'YMM4.EditAssist.dll'), 'new')
    [IO.File]::WriteAllText((Join-Path $fixture 'keep.ymmp'), 'project untouched')
    Copy-Item -LiteralPath $sourceScript -Destination (Join-Path $requestPath 'Apply-Version.ps1')
    $requestPlan = @{
        Target = $target; Stage = $stagePath; HostExe = (Join-Path $fixture 'YukkuriMovieMaker.exe');
        Storage = $storagePath; Request = $requestPath; Version = '0.5.2';
        OriginalHash = (Get-FileHash -LiteralPath (Join-Path $target 'YMM4.EditAssist.dll') -Algorithm SHA256).Hash;
        Files = @(@{ Name = 'YMM4.EditAssist.dll'; Hash = (Get-FileHash -LiteralPath (Join-Path $stagePath 'YMM4.EditAssist.dll') -Algorithm SHA256).Hash })
    }
    $requestPlan | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $requestPath 'plan.json') -Encoding UTF8
    return @{ Root = $fixture; Target = $target; Stage = $stagePath; Request = $requestPath; Storage = $storagePath }
}
try {
    # Scope mocks to fixture runs. The shipped helper always waits for real YMM4 processes.
    & {
        function Get-Process { param($Name, $ErrorAction) return @() }
        function Read-Host { return '' }
        $success = Make-Fixture 'success'
        & (Join-Path $success.Request 'Apply-Version.ps1')
        Assert-Check (([IO.File]::ReadAllText((Join-Path $success.Target 'YMM4.EditAssist.dll'))) -eq 'new') 'switch installs staged version'
        $backups = @(Get-ChildItem -LiteralPath (Join-Path $success.Storage 'backups') -Recurse -Filter 'YMM4.EditAssist.dll' -File)
        Assert-Check ($backups.Count -eq 1 -and [IO.File]::ReadAllText($backups[0].FullName) -eq 'old') 'previous version backed up outside plugin tree'
        Assert-Check (([IO.File]::ReadAllText((Join-Path $success.Root 'keep.ymmp'))) -eq 'project untouched') 'project remains untouched'
        $changed = Make-Fixture 'changed'
        [IO.File]::WriteAllText((Join-Path $changed.Stage 'YMM4.EditAssist.dll'), 'tampered')
        & (Join-Path $changed.Request 'Apply-Version.ps1')
        Assert-Check (([IO.File]::ReadAllText((Join-Path $changed.Target 'YMM4.EditAssist.dll'))) -eq 'old') 'changed stage rejected before replacing old version'
        $stale = Make-Fixture 'stale'
        [IO.File]::WriteAllText((Join-Path $stale.Target 'YMM4.EditAssist.dll'), 'installed elsewhere')
        & (Join-Path $stale.Request 'Apply-Version.ps1')
        Assert-Check (([IO.File]::ReadAllText((Join-Path $stale.Target 'YMM4.EditAssist.dll'))) -eq 'installed elsewhere') 'newer installation is not overwritten'
        $duplicate = Make-Fixture 'duplicate'
        $other = Join-Path $duplicate.Root 'user/plugin/Other'
        New-Item -ItemType Directory -Path $other | Out-Null
        [IO.File]::WriteAllText((Join-Path $other 'YMM4.EditAssist.dll'), 'duplicate')
        & (Join-Path $duplicate.Request 'Apply-Version.ps1')
        Assert-Check (([IO.File]::ReadAllText((Join-Path $duplicate.Target 'YMM4.EditAssist.dll'))) -eq 'old') 'duplicate installation rejected'
    }
    Write-Host ("All $checks version-switch fixture checks passed.")
} finally { if (Test-Path -LiteralPath $testRoot) { Remove-Item -LiteralPath $testRoot -Recurse -Force } }
