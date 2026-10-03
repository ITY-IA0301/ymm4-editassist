[CmdletBinding()]
param(
    [string]$Ymm4Directory,
    [string]$DotnetPath = 'dotnet',
    [switch]$Install
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

function Invoke-Dotnet {
    param([string[]]$Arguments)
    # Do not leave build servers running or depend on sandboxed named pipes.
    if ($Arguments[0] -eq 'build') {
        $Arguments += @('-t:Rebuild', '-m:1', '-nr:false', '-p:UseSharedCompilation=false')
    }
    & $DotnetPath @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet が失敗しました（終了コード $LASTEXITCODE）。表示された最初のエラーを確認してください。" }
}

try {
    if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
        throw 'このビルドスクリプトはWindows用です。'
    }
    if (-not $Ymm4Directory) {
        $Ymm4Directory = Read-Host 'YukkuriMovieMaker.exe のあるフォルダ、またはexeのパスを入力'
    }
    $Ymm4Directory = $Ymm4Directory.Trim().Trim('"')
    $Ymm4Directory = [IO.Path]::GetFullPath($Ymm4Directory)
    if ([IO.Path]::GetExtension($Ymm4Directory) -eq '.exe') {
        $Ymm4Directory = [IO.Path]::GetDirectoryName($Ymm4Directory)
    }
    if ($Ymm4Directory.Contains(';')) { throw 'MSBuildへの引き渡しのため、セミコロンを含まないYMM4フォルダを使用してください。' }
    $hostExe = Join-Path $Ymm4Directory 'YukkuriMovieMaker.exe'
    $hostDll = Join-Path $Ymm4Directory 'YukkuriMovieMaker.Plugin.dll'
    $runtimePath = Join-Path $Ymm4Directory 'YukkuriMovieMaker.runtimeconfig.json'
    foreach ($required in @($hostExe, $hostDll, $runtimePath, (Join-Path $Ymm4Directory 'YukkuriMovieMaker.Controls.dll'))) {
        if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw "YMM4本体のファイルが見つかりません：$required" }
    }
    $runtime = Get-Content -LiteralPath $runtimePath -Raw -Encoding UTF8 | ConvertFrom-Json
    $runtimeOptions = $runtime.runtimeOptions
    $runtimeMajor = 0
    foreach ($propertyName in @('framework', 'frameworks', 'includedFrameworks')) {
        $property = $runtimeOptions.PSObject.Properties[$propertyName]
        if ($null -ne $property) {
            foreach ($framework in @($property.Value)) {
                if ($framework.name -eq 'Microsoft.NETCore.App' -or $framework.name -eq 'Microsoft.WindowsDesktop.App') {
                    $runtimeMajor = [Math]::Max($runtimeMajor, ([Version]$framework.version).Major)
                }
            }
        }
    }
    if ($runtimeMajor -eq 0) {
        $tfmProperty = $runtimeOptions.PSObject.Properties['tfm']
        if ($null -ne $tfmProperty -and $tfmProperty.Value -match '^net(\d+)\.') { $runtimeMajor = [int]$Matches[1] }
    }
    if ($runtimeMajor -lt 8 -or $runtimeMajor -gt 10) {
        throw "実行環境.NET $runtimeMajor は初版のビルド対象外です。設定を無理に書き換えないでください。"
    }
    if (-not (Get-Command $DotnetPath -ErrorAction SilentlyContinue)) {
        throw ".NET $runtimeMajor SDKが必要です。https://dotnet.microsoft.com/download からSDKを導入して再実行してください（Runtimeだけではビルドできません）。"
    }
    $sdks = & $DotnetPath --list-sdks
    if ($LASTEXITCODE -ne 0 -or -not ($sdks | Where-Object { $_ -match "^$runtimeMajor\." })) {
        throw ".NET $runtimeMajor SDKを導入して再実行してください。実行環境と同じ世代のSDKで検証します。"
    }
    $selectedSdk = & $DotnetPath --version
    if ($LASTEXITCODE -ne 0 -or $selectedSdk -notmatch "^$runtimeMajor\.") {
        throw "選択中のSDK（$selectedSdk）が.NET $runtimeMajor と異なります。対応SDKのdotnet.exeを -DotnetPath で指定するか、global.jsonでSDKを選択してください。"
    }
    $hostVersion = (Get-Item -LiteralPath $hostExe).VersionInfo.FileVersion
    $releaseVersion = ([xml](Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Directory.Build.props') -Raw -Encoding UTF8)).Project.PropertyGroup.Version
    Write-Host "対象YMM4：$hostVersion / .NET $runtimeMajor" -ForegroundColor Cyan
    Write-Host '素材やプロジェクトは変更しません。テスト用ファイルは一時フォルダだけに作成します。'

    $artifacts = Join-Path $PSScriptRoot 'artifacts'
    $checksOutput = Join-Path $artifacts "checks\net$runtimeMajor"
    $pluginOutput = Join-Path $artifacts "build\net$runtimeMajor"
    $windowsOutput = Join-Path $artifacts "windows-checks\net$runtimeMajor"
    $stage = Join-Path $artifacts "stage\net$runtimeMajor"
    New-Item -ItemType Directory -Force -Path $stage | Out-Null
    Push-Location $PSScriptRoot
    try {
        Invoke-Dotnet -Arguments @('build', 'tests/EditAssist.CoreChecks/EditAssist.CoreChecks.csproj',
            '-c', 'Release', "-p:RuntimeMajor=$runtimeMajor", '--output', $checksOutput)
        Invoke-Dotnet -Arguments @((Join-Path $checksOutput 'EditAssist.CoreChecks.dll'))
        Invoke-Dotnet -Arguments @('build', 'src/EditAssist.Plugin/EditAssist.Plugin.csproj',
            '-c', 'Release', "-p:RuntimeMajor=$runtimeMajor", "-p:Ymm4Directory=$Ymm4Directory", '--output', $pluginOutput)
        Invoke-Dotnet -Arguments @('build', 'tests/EditAssist.WindowsChecks/EditAssist.WindowsChecks.csproj',
            '-c', 'Release', "-p:RuntimeMajor=$runtimeMajor", "-p:Ymm4Directory=$Ymm4Directory", '--output', $windowsOutput)
        Invoke-Dotnet -Arguments @((Join-Path $windowsOutput 'EditAssist.WindowsChecks.dll'), $Ymm4Directory)
    }
    finally { Pop-Location }

    # Stage only our own assemblies. Never bundle YMM4 assemblies.
    $allowedAssemblies = @('YMM4.EditAssist.dll', 'EditAssist.Core.dll')
    foreach ($dllName in $allowedAssemblies) {
        Copy-Item -LiteralPath (Join-Path $pluginOutput $dllName) -Destination (Join-Path $stage $dllName) -Force
    }
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README.md') -Destination (Join-Path $stage 'README.md') -Force
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'NOTICES.md') -Destination (Join-Path $stage 'NOTICES.md') -Force
    # Repeated builds must not package a stale or unexpected file.
    $packageNames = @($allowedAssemblies + @('README.md', 'NOTICES.md',
        'docs/EDITING.md', 'docs/MEDIA-REFERENCES.md', 'docs/WINDOWS-CHECKLIST.md', 'docs/VALIDATION.md'))
    $packageBase = Join-Path $artifacts "EditAssist-$releaseVersion-net$runtimeMajor"
    $zipPath = $packageBase + '.zip'
    $packagePath = $packageBase + '.ymme'
    if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $packageStream = [IO.File]::Open($zipPath, [IO.FileMode]::CreateNew)
    $packageArchive = [IO.Compression.ZipArchive]::new($packageStream, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($entryName in $packageNames) {
            $sourceRoot = $stage
            if ($entryName.StartsWith('docs/')) { $sourceRoot = $PSScriptRoot }
            [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($packageArchive,
                (Join-Path $sourceRoot $entryName), $entryName, [IO.Compression.CompressionLevel]::Optimal) | Out-Null
        }
    }
    finally { $packageArchive.Dispose(); $packageStream.Dispose() }
    Move-Item -LiteralPath $zipPath -Destination $packagePath -Force

    $buildInfo = "YMM4 $hostVersion / .NET $runtimeMajor`r`nSDK: $selectedSdk`r`nBuilt: $([DateTimeOffset]::Now.ToString('o'))`r`nSource: EditAssist $releaseVersion`r`nCore checks: passed`r`nWPF construction and synthetic actual-host timeline editing/backup/undo checks: passed`r`nLive YMM4 UI/playback/drop/speech-generation verification: not yet performed`r`n"
    [IO.File]::WriteAllText((Join-Path $artifacts "build-info-net$runtimeMajor.txt"), $buildInfo, [Text.UTF8Encoding]::new($false))
    if ($Install) {
        if (Get-Process -Name 'YukkuriMovieMaker' -ErrorAction SilentlyContinue) {
            throw "ビルドは成功しました。インストールするにはYMM4を終了してください。パッケージ：$packagePath"
        }
        $destination = Join-Path $Ymm4Directory 'user\plugin\EditAssist'
        $otherCopies = @(Get-ChildItem -LiteralPath (Join-Path $Ymm4Directory 'user\plugin') -Filter 'YMM4.EditAssist.dll' -Recurse -File -ErrorAction SilentlyContinue |
            Where-Object { $_.DirectoryName -ne $destination })
        if ($otherCopies.Count -gt 0) {
            throw "別フォルダにEditAssistがあります。二重導入を防ぐため配置を停止しました。旧版のフォルダをプラグイン領域の外へ退避してから再実行してください：$($otherCopies[0].DirectoryName)"
        }
        if (Test-Path -LiteralPath $destination) {
            $backupDirectory = Join-Path $Ymm4Directory ('user\EditAssistBackups\' + [DateTime]::Now.ToString('yyyyMMdd-HHmmss-fff'))
            New-Item -ItemType Directory -Force -Path $backupDirectory | Out-Null
            Copy-Item -LiteralPath $destination -Destination $backupDirectory -Recurse -Force
        }
        New-Item -ItemType Directory -Force -Path $destination | Out-Null
        foreach ($dllName in $allowedAssemblies) {
            Copy-Item -LiteralPath (Join-Path $stage $dllName) -Destination (Join-Path $destination $dllName) -Force
        }
        Write-Host 'EditAssistを配置しました。YMM4を起動し、ツールメニューで確認してください。' -ForegroundColor Green
    }
    Write-Host "パッケージを作成しました：$packagePath" -ForegroundColor Green
    Write-Host 'これはビルド成功です。YMM4での読み込み・試聴・受け渡しは実機確認してください。'
    $publishConfigPath = Join-Path $PSScriptRoot 'GitHubPublish.local.json'
    if (Test-Path -LiteralPath $publishConfigPath) {
        $publishConfig = Get-Content -LiteralPath $publishConfigPath -Raw -Encoding UTF8 | ConvertFrom-Json
        if ($publishConfig.Enabled -eq $true) {
            Write-Host 'テスト・ビルドが成功したため、設定したGitHubへソースを登録します。'
            & (Join-Path $PSScriptRoot 'PublishGitHub.ps1') -RepositoryDirectory $publishConfig.RepositoryDirectory `
                -ExpectedRemote $publishConfig.ExpectedRemote -Branch $publishConfig.Branch `
                -Message "Update EditAssist $releaseVersion after successful checks"
        }
    }
    exit 0
}
catch {
    Write-Host $_.Exception.Message -ForegroundColor Red
    exit 1
}

