# Isolated, offline checks. Uses disposable fixture copies, never a real remote.
[CmdletBinding()]
param([string]$TestDirectory = (Join-Path ([IO.Path]::GetTempPath()) ('editassist-git-checks-' + [Guid]::NewGuid().ToString('N'))))
$ErrorActionPreference = 'Stop'
$pluginRoot = Split-Path $PSScriptRoot -Parent
$publisher = Join-Path $pluginRoot 'PublishGitHub.ps1'
$testRoot = [IO.Path]::GetFullPath($TestDirectory)
if (Test-Path -LiteralPath $testRoot) { throw 'Use a new, empty test directory.' }
New-Item -ItemType Directory -Path $testRoot | Out-Null
$fixture = Join-Path $testRoot 'source'
$repository = Join-Path $testRoot 'repository'
New-Item -ItemType Directory -Path $fixture, $repository | Out-Null
$rootPrefix = $pluginRoot.TrimEnd('\') + '\'
foreach ($file in Get-ChildItem -LiteralPath $pluginRoot -Recurse -File -Force) {
    $relative = $file.FullName.Substring($rootPrefix.Length)
    if ($relative -match '(^|[\\/])(bin|obj|artifacts|\.git|\.vs)([\\/]|$)' -or $relative -eq 'GitHubPublish.local.json') { continue }
    $target = Join-Path $fixture $relative
    New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($target)) -Force | Out-Null
    Copy-Item -LiteralPath $file.FullName -Destination $target
}
$git = (Get-Command git -CommandType Application | Select-Object -First 1).Source
function Invoke-TestGit {
    param([string[]]$Arguments)
    $result = @(& $git -c "safe.directory=$($repository.Replace('\', '/'))" -C $repository @Arguments)
    if ($LASTEXITCODE -ne 0) { throw 'Fixture Git command failed.' }
    return $result
}
$script:checks = 0
function Check {
    param([bool]$Condition, [string]$Name)
    if (-not $Condition) { throw "FAIL: $Name" }
    $script:checks++
    Write-Host "PASS: $Name"
}
function Must-Refuse {
    param([scriptblock]$Action, [string]$Name, [string]$ExpectedError)
    $refused = $false
    try { & $Action }
    catch { if ($_.Exception.Message -notlike "*$ExpectedError*") { throw }; $refused = $true }
    Check $refused $Name
}
Invoke-TestGit @('init', '-b', 'main') | Out-Null
Invoke-TestGit @('config', 'user.name', 'EditAssist isolated check') | Out-Null
Invoke-TestGit @('config', 'user.email', 'fixture@example.invalid') | Out-Null
Invoke-TestGit @('config', 'core.autocrlf', 'false') | Out-Null
Invoke-TestGit @('commit', '--allow-empty', '-m', 'Fixture start') | Out-Null
& $publisher -RepositoryDirectory $repository -SourceDirectory $fixture -LocalOnly
Check (Test-Path -LiteralPath (Join-Path $repository 'src/EditAssist.Core/MaterialSearch.cs')) 'Source is copied'
Check (-not (Test-Path -LiteralPath (Join-Path $repository 'artifacts'))) 'Build artifacts are excluded'
Check (@(Invoke-TestGit @('status', '--porcelain')).Count -eq 0) 'Initial sync leaves a clean repository'
Check (-not (@(Invoke-TestGit @('ls-files')) -match 'editassist-publish-state|GitHubPublish.local')) 'Private configuration and state are not tracked'
$firstHead = [string](Invoke-TestGit @('rev-parse', 'HEAD'))
& $publisher -RepositoryDirectory $repository -SourceDirectory $fixture -LocalOnly
Check ([string](Invoke-TestGit @('rev-parse', 'HEAD')) -eq $firstHead) 'No-op sync makes no extra commit'
[IO.File]::AppendAllText((Join-Path $fixture 'README.md'), "`nFixture source change.`n")
& $publisher -RepositoryDirectory $repository -SourceDirectory $fixture -LocalOnly
Check ([string](Invoke-TestGit @('rev-parse', 'HEAD')) -ne $firstHead) 'New source changes are committed'
$beforeHash = (Get-FileHash -LiteralPath (Join-Path $repository 'README.md')).Hash
$beforeHead = [string](Invoke-TestGit @('rev-parse', 'HEAD'))
$dirty = Join-Path $repository 'unrelated-user-note.txt'
[IO.File]::WriteAllText($dirty, 'Keep this fixture user note.')
Must-Refuse { & $publisher -RepositoryDirectory $repository -SourceDirectory $fixture -LocalOnly } 'Uncommitted user files prevent synchronization' 'uncommitted'
Check ((Get-FileHash -LiteralPath (Join-Path $repository 'README.md')).Hash -eq $beforeHash -and
    [string](Invoke-TestGit @('rev-parse', 'HEAD')) -eq $beforeHead) 'Refusal does not change destination files or history'
Invoke-TestGit @('add', '--', 'unrelated-user-note.txt') | Out-Null
Invoke-TestGit @('commit', '-m', 'Preserve fixture user note') | Out-Null
[IO.File]::AppendAllText((Join-Path $repository 'README.md'), "`nIndependent fixture edit.`n")
Invoke-TestGit @('add', '--', 'README.md') | Out-Null
Invoke-TestGit @('commit', '-m', 'Independent fixture edit') | Out-Null
Must-Refuse { & $publisher -RepositoryDirectory $repository -SourceDirectory $fixture -LocalOnly } 'Independent committed edits are not overwritten' 'changed independently'
# Restore only fixture content using a new ordinary commit (no reset/checkout).
Copy-Item -LiteralPath (Join-Path $fixture 'README.md') -Destination (Join-Path $repository 'README.md') -Force
Invoke-TestGit @('add', '--', 'README.md') | Out-Null
Invoke-TestGit @('commit', '-m', 'Restore fixture baseline') | Out-Null
$unexpected = Join-Path $fixture 'src/credentials.env'
[IO.File]::WriteAllText($unexpected, 'Fixture-only non-source file.')
Must-Refuse { & $publisher -RepositoryDirectory $repository -SourceDirectory $fixture -LocalOnly } 'Unknown source extension prevents upload' 'Unexpected source file'
Move-Item -LiteralPath $unexpected -Destination (Join-Path $testRoot 'retained-credentials-fixture.env')
Move-Item -LiteralPath (Join-Path $fixture 'NOTICES.md') -Destination (Join-Path $testRoot 'retained-notices-fixture.md')
Must-Refuse { & $publisher -RepositoryDirectory $repository -SourceDirectory $fixture -LocalOnly } 'Missing required source prevents upload' 'NOTICES.md'
Copy-Item -LiteralPath (Join-Path $testRoot 'retained-notices-fixture.md') -Destination (Join-Path $fixture 'NOTICES.md')
Must-Refuse { & $publisher -RepositoryDirectory $repository -SourceDirectory $fixture -ExpectedRemote 'https://token@github.com/owner/repo.git' } 'Credential-bearing remote URL is rejected' 'without credentials'
Must-Refuse { & $publisher -RepositoryDirectory $repository -SourceDirectory $fixture -LocalOnly -Branch 'other' } 'Wrong branch is rejected' 'Switch the destination'
Must-Refuse { & $publisher -RepositoryDirectory $repository -SourceDirectory $repository -LocalOnly } 'Overlapping directories are rejected' 'separate'
Write-Host "$script:checks publishing checks passed. Fixtures retained: $testRoot" -ForegroundColor Green
