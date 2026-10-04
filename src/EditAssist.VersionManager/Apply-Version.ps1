[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0
$lock = $null
$backup = $null
$moved = $false
$completed = $false
try {
    $plan = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'plan.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    $hostRoot = [IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($plan.HostExe))
    $pluginRoot = Join-Path $hostRoot 'user\plugin'
    $storage = Join-Path $hostRoot 'user\EditAssistVersions'
    if ([IO.Path]::GetFullPath($plan.Storage) -ne $storage -or
        [IO.Path]::GetFullPath($plan.Request) -ne $PSScriptRoot -or
        [IO.Path]::GetFullPath($plan.Stage) -ne (Join-Path $PSScriptRoot 'stage') -or
        -not ([IO.Path]::GetFullPath($plan.Target).StartsWith($pluginRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) -or
        [IO.Path]::GetFileName($plan.HostExe) -ne 'YukkuriMovieMaker.exe') { throw '切替先のパスが不正です。' }
    # One worker per YMM4 installation. Never kill YMM4 or change loaded assemblies.
    $lock = [IO.File]::Open((Join-Path $storage 'switch.lock'), [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    Write-Host ('EditAssist ' + $plan.Version + 'への切替を待っています。')
    Write-Host 'プロジェクトを保存してYMM4を終了してください。このウィンドウを閉じると待機を中止できます。'
    $deadline = [DateTime]::UtcNow.AddMinutes(30)
    while (@(Get-Process -Name YukkuriMovieMaker -ErrorAction SilentlyContinue).Count -gt 0) {
        if ([DateTime]::UtcNow -ge $deadline) { throw '30分経過したため中止しました。版は変更していません。' }
        Start-Sleep -Seconds 1
    }
    # A different installer or a changed stage must not be overwritten silently.
    foreach ($entry in $plan.Files) {
        $path = Join-Path $plan.Stage $entry.Name
        if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $entry.Hash) { throw '切替準備ファイルが変更されています。再度予約してください。' }
    }
    $actual = @(Get-ChildItem -LiteralPath $plan.Stage -Recurse -File)
    if ($actual.Count -ne @($plan.Files).Count) { throw '切替準備ファイルが増減しています。' }
    if (Test-Path -LiteralPath $plan.Target) {
        $dll = Join-Path $plan.Target 'YMM4.EditAssist.dll'
        if (-not $plan.OriginalHash -or -not (Test-Path -LiteralPath $dll) -or
            (Get-FileHash -LiteralPath $dll -Algorithm SHA256).Hash -ne $plan.OriginalHash) { throw '導入済みの版が予約後に変わりました。再度予約してください。' }
        $otherDlls = @(Get-ChildItem -LiteralPath $plan.Target -Recurse -Filter '*.dll' -File | Where-Object { $_.Name -notin @('YMM4.EditAssist.dll', 'EditAssist.Core.dll') })
        if ($otherDlls.Count -gt 0) { throw '切替先に他のプラグインがあります。' }
    } elseif ($plan.OriginalHash) { throw '導入先が予約後に変わりました。' }
    # Refuse links on every path we move or write, including plugin/storage ancestors.
    foreach ($path in @($plan.Stage, $plan.Target, $storage, $pluginRoot)) {
        $cursor = [IO.Path]::GetFullPath($path)
        while ($cursor -and $cursor.Length -ge $hostRoot.Length) {
            if ((Test-Path -LiteralPath $cursor) -and ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'リンクを含むフォルダへの切替は中止しました。' }
            $cursor = [IO.Path]::GetDirectoryName($cursor)
        }
    }
    foreach ($folder in @($plan.Stage, $plan.Target)) {
        if (Test-Path -LiteralPath $folder) {
            if (@(Get-ChildItem -LiteralPath $folder -Recurse -Force | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }).Count -gt 0) { throw 'リンクを含むファイルがあります。' }
        }
    }
    $copies = @(Get-ChildItem -LiteralPath $pluginRoot -Recurse -Filter 'YMM4.EditAssist.dll' -File -ErrorAction SilentlyContinue)
    if ($copies.Count -gt 1 -or ($copies.Count -eq 1 -and $copies[0].DirectoryName -ne $plan.Target)) { throw '別の場所にもEditAssistが導入されています。' }
    New-Item -ItemType Directory -Force -Path $pluginRoot | Out-Null
    if (Test-Path -LiteralPath $plan.Target) {
        $backupRoot = Join-Path $storage 'backups'
        New-Item -ItemType Directory -Force -Path $backupRoot | Out-Null
        $backup = Join-Path $backupRoot ([DateTime]::Now.ToString('yyyyMMdd-HHmmss-fff') + '-' + [Guid]::NewGuid().ToString('N'))
        [IO.Directory]::Move($plan.Target, $backup)
        $moved = $true
    }
    [IO.Directory]::Move($plan.Stage, $plan.Target)
    $completed = $true
    $message = 'EditAssist ' + $plan.Version + 'への切替が完了しました。YMM4を起動してください。'
    if ($backup) { $message += "`r`n変更前のバックアップ：" + $backup }
    [IO.File]::WriteAllText((Join-Path $storage 'last-result.txt'), $message, [Text.UTF8Encoding]::new($true))
    Write-Host $message -ForegroundColor Green
} catch {
    $message = $_.Exception.Message
    if ($moved -and -not $completed -and -not (Test-Path -LiteralPath $plan.Target)) {
        try { [IO.Directory]::Move($backup, $plan.Target); $message += "`r`n変更前の版を復元しました。" }
        catch { $message += "`r`n自動復元できませんでした。変更前の版は次の場所に残っています：" + $backup }
    }
    Write-Host ('切替を完了できませんでした：' + $message) -ForegroundColor Red
    if ($lock) { try { [IO.File]::WriteAllText((Join-Path $storage 'last-result.txt'), $message, [Text.UTF8Encoding]::new($true)) } catch { } }
} finally { if ($lock) { $lock.Dispose() } }
Write-Host 'Enterキーでこのウィンドウを閉じます。'
Read-Host | Out-Null
