[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$RepositoryDirectory,
    [string]$SourceDirectory,
    [string]$ExpectedRemote,
    [string]$Branch = 'main',
    [string]$Message = 'Update EditAssist source after successful checks',
    [switch]$LocalOnly
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0
$previousPrompt = $env:GIT_TERMINAL_PROMPT
$env:GIT_TERMINAL_PROMPT = '0'
try {
    if (-not $SourceDirectory) { $SourceDirectory = $PSScriptRoot }
    $sourceRoot = [IO.Path]::GetFullPath($SourceDirectory).TrimEnd('\', '/')
    $repoRoot = [IO.Path]::GetFullPath($RepositoryDirectory).TrimEnd('\', '/')
    if ($sourceRoot -eq $repoRoot -or $repoRoot.StartsWith($sourceRoot + '\', [StringComparison]::OrdinalIgnoreCase) -or
        $sourceRoot.StartsWith($repoRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Source and repository must be separate, non-nested directories.'
    }
    foreach ($root in @($sourceRoot, $repoRoot)) {
        $current = Get-Item -LiteralPath $root
        while ($null -ne $current) {
            if ($current.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Linked directory is not supported: $($current.FullName)" }
            $current = $current.Parent
        }
    }
    if (-not (Test-Path -LiteralPath (Join-Path $repoRoot '.git') -PathType Container)) {
        throw 'The destination must be an existing, ordinary Git repository.'
    }
    $git = (Get-Command git -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
    $safeDirectory = $repoRoot.Replace('\', '/')
    function Invoke-RepositoryGit {
        param([string[]]$Arguments)
        $result = @(& $git -c "safe.directory=$safeDirectory" -C $repoRoot @Arguments)
        if ($LASTEXITCODE -ne 0) { throw "Git failed ($($Arguments[0])); local files/history have been retained." }
        return $result
    }
    $actualRoot = [IO.Path]::GetFullPath([string](Invoke-RepositoryGit @('rev-parse', '--show-toplevel'))).TrimEnd('\', '/')
    if ($actualRoot -ne $repoRoot) { throw 'The destination is not the repository root.' }
    if ([string](Invoke-RepositoryGit @('branch', '--show-current')) -ne $Branch) { throw "Switch the destination to $Branch first." }
    if (@(Invoke-RepositoryGit @('status', '--porcelain=v1', '--untracked-files=all')).Count -ne 0) {
        throw 'The destination has uncommitted files. Commit or safely preserve them before publishing.'
    }
    if (-not $LocalOnly) {
        if ($ExpectedRemote -notmatch '^https://github\.com/[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+(?:\.git)?$' -and
            $ExpectedRemote -notmatch '^git@github\.com:[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+(?:\.git)?$') {
            throw 'ExpectedRemote must be a GitHub URL without credentials.'
        }
        $fetchUrls = @(Invoke-RepositoryGit @('remote', 'get-url', '--all', 'origin'))
        $pushUrls = @(Invoke-RepositoryGit @('remote', 'get-url', '--push', '--all', 'origin'))
        if ($fetchUrls.Count -ne 1 -or $pushUrls.Count -ne 1 -or
            $fetchUrls[0] -ne $ExpectedRemote -or $pushUrls[0] -ne $ExpectedRemote) { throw 'The GitHub remote changed; review the local configuration first.' }
        Invoke-RepositoryGit @('fetch', '--no-tags', 'origin', $Branch) | Out-Host
        # Refuse divergence BEFORE copying. Never rebase, reset, or force-push.
        Invoke-RepositoryGit @('merge-base', '--is-ancestor', 'FETCH_HEAD', 'HEAD') | Out-Null
    }

    $names = @('README.md', 'NOTICES.md', 'CHANGELOG.md', 'Directory.Build.props', '.gitignore',
        'Build.cmd', 'Build.ps1', 'PackageSource.ps1', 'PublishGitHub.ps1')
    $extensions = @('.cs', '.csproj', '.props', '.xaml', '.md', '.ps1')
    function Add-SourceFolder {
        param([string]$Folder)
        foreach ($entry in Get-ChildItem -LiteralPath (Join-Path $sourceRoot $Folder) -Force) {
            if ($entry.Name -in @('bin', 'obj', 'artifacts', '.git', '.vs')) { continue }
            if ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Linked source is not supported: $Folder/$($entry.Name)" }
            if ($entry.PSIsContainer) { Add-SourceFolder ($Folder + '/' + $entry.Name) }
            elseif ($entry.Extension -in $extensions) { $script:publishNames.Add($Folder + '/' + $entry.Name) }
            else { throw "Unexpected source file; review before uploading: $Folder/$($entry.Name)" }
        }
    }
    $script:publishNames = New-Object 'System.Collections.Generic.List[string]'
    foreach ($name in $names) { $script:publishNames.Add($name) }
    foreach ($folder in @('src', 'tests', 'docs')) { Add-SourceFolder $folder }
    $names = @($script:publishNames | Sort-Object -Unique)
    $statePath = Join-Path $repoRoot '.git/editassist-publish-state.json'
    $baseline = @{}
    if (Test-Path -LiteralPath $statePath) {
        $state = Get-Content -LiteralPath $statePath -Raw -Encoding UTF8 | ConvertFrom-Json
        if ($state.SourceDirectory -ne $sourceRoot -or $state.Version -ne 1) { throw 'The source location or sync-state version changed; review before publishing.' }
        foreach ($property in $state.Files.PSObject.Properties) { $baseline[$property.Name] = [string]$property.Value }
        foreach ($name in $baseline.Keys) {
            if ($name -notin $names) { throw "A previously published source was removed; review that removal manually: $name" }
        }
    }
    $hashes = [ordered]@{}
    foreach ($name in $names) {
        $source = Get-Item -LiteralPath (Join-Path $sourceRoot $name)
        if ($source.PSIsContainer -or ($source.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw "Invalid source file: $name" }
        $hash = (Get-FileHash -LiteralPath $source.FullName -Algorithm SHA256).Hash
        $hashes[$name] = $hash
        $destination = Join-Path $repoRoot $name
        $ancestor = [IO.Path]::GetDirectoryName($destination)
        while ($ancestor.Length -ge $repoRoot.Length) {
            if (Test-Path -LiteralPath $ancestor) {
                $item = Get-Item -LiteralPath $ancestor
                if (-not $item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw "Invalid destination directory: $name" }
            }
            if ($ancestor -eq $repoRoot) { break }
            $ancestor = [IO.Path]::GetDirectoryName($ancestor)
        }
        if (Test-Path -LiteralPath $destination) {
            $item = Get-Item -LiteralPath $destination
            if ($item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw "Invalid destination file: $name" }
            $destinationHash = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash
            if ($baseline.ContainsKey($name)) {
                if ($destinationHash -ne $baseline[$name]) { throw "Repository source was changed independently; review before overwriting: $name" }
            }
            elseif ($destinationHash -ne $hash) { throw "Existing destination differs from this source: $name" }
        }
        elseif ($baseline.ContainsKey($name)) { throw "Repository source was removed independently: $name" }
    }
    # All destination conflict checks completed before any source copy.
    foreach ($name in $names) {
        $destination = Join-Path $repoRoot $name
        if ((Test-Path -LiteralPath $destination) -and
            (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash -eq $hashes[$name]) { continue }
        New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($destination)) -Force | Out-Null
        Copy-Item -LiteralPath (Join-Path $sourceRoot $name) -Destination $destination -Force
    }
    foreach ($name in $names) {
        if ((Get-FileHash -LiteralPath (Join-Path $repoRoot $name) -Algorithm SHA256).Hash -ne $hashes[$name] -or
            (Get-FileHash -LiteralPath (Join-Path $sourceRoot $name) -Algorithm SHA256).Hash -ne $hashes[$name]) {
            throw "Source changed while synchronizing; no commit/push was attempted: $name"
        }
    }
    Invoke-RepositoryGit (@('add', '--') + $names) | Out-Null
    if (@(Invoke-RepositoryGit @('diff', '--cached', '--name-only')).Count -gt 0) {
        Invoke-RepositoryGit @('commit', '-m', $Message) | Out-Host
    }
    $stateText = [ordered]@{ Version = 1; SourceDirectory = $sourceRoot; Files = $hashes } | ConvertTo-Json -Depth 5
    [IO.File]::WriteAllText($statePath, $stateText, [Text.UTF8Encoding]::new($false))
    if ($LocalOnly) { Write-Host "Source committed locally ($($names.Count) files); no network push requested."; return }
    Invoke-RepositoryGit @('push', 'origin', "HEAD:refs/heads/$Branch") | Out-Host
    $head = [string](Invoke-RepositoryGit @('rev-parse', 'HEAD'))
    $remoteHead = @(Invoke-RepositoryGit @('ls-remote', '--heads', 'origin', "refs/heads/$Branch"))
    if ($remoteHead.Count -ne 1 -or ($remoteHead[0] -split '\s+')[0] -ne $head) { throw 'The GitHub commit could not be confirmed; local history has been retained.' }
    Write-Host "GitHub updated and verified: $ExpectedRemote ($($names.Count) source files, $head)" -ForegroundColor Green
}
finally { $env:GIT_TERMINAL_PROMPT = $previousPrompt }
