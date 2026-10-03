[CmdletBinding()]
param([string]$Destination)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
if (-not $Destination) {
    $releaseVersion = ([xml](Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Directory.Build.props') -Raw -Encoding UTF8)).Project.PropertyGroup.Version
    $Destination = Join-Path $PSScriptRoot "artifacts\YMM4-EditAssist-$releaseVersion-source.zip"
}
$Destination = [IO.Path]::GetFullPath($Destination)
New-Item -ItemType Directory -Force -Path ([IO.Path]::GetDirectoryName($Destination)) | Out-Null
if (Test-Path -LiteralPath $Destination) { throw "同じ名前のZIPが存在します。別名で保存してください：$Destination" }
$root = [IO.Path]::GetFullPath($PSScriptRoot).TrimEnd('\') + '\'
$files = @('README.md', 'NOTICES.md', 'CHANGELOG.md', 'Directory.Build.props', '.gitignore', 'Build.cmd', 'Build.ps1', 'PackageSource.ps1', 'PublishGitHub.ps1') |
    ForEach-Object { Get-Item -LiteralPath (Join-Path $root $_) }
foreach ($folder in @('src', 'tests', 'docs')) {
    $files += Get-ChildItem -LiteralPath (Join-Path $root $folder) -Recurse -File |
        Where-Object { $_.FullName.Substring($root.Length) -notmatch '(^|[\\/])(bin|obj|artifacts)([\\/]|$)' }
}
$stream = [IO.File]::Open($Destination, [IO.FileMode]::CreateNew)
$archive = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($file in $files | Sort-Object FullName) {
        $relative = $file.FullName.Substring($root.Length).Replace('\', '/')
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.FullName,
            'YMM4-EditAssist/' + $relative, [IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
}
finally { $archive.Dispose(); $stream.Dispose() }
Write-Host "ソースZIPを保存しました：$Destination（$($files.Count)ファイル）"
