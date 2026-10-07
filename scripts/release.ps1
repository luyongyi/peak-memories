#requires -Version 7.0
<#
.SYNOPSIS
Build, test, package and optionally publish a PEAK memoir prerelease.
.EXAMPLE
pwsh -File scripts/release.ps1 -Draft
.EXAMPLE
pwsh -File scripts/release.ps1 -Publish -GameRoot 'D:/SteamLibrary/steamapps/common/PEAK'
#>
[CmdletBinding()]
param(
    [string] $Tag,
    [string] $GameRoot = "${env:ProgramFiles(x86)}/Steam/steamapps/common/PEAK",
    [string] $Repository = 'luyongyi/peak-memories',
    [switch] $Draft,
    [switch] $Publish,
    [switch] $PreflightOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = 'true'

function Invoke-Checked {
    param([string] $Command, [string[]] $Arguments, [switch] $Capture)
    if ($Capture) {
        $result = & $Command @Arguments
        if ($LASTEXITCODE -ne 0) { throw "$Command failed (exit $LASTEXITCODE)." }
        return ($result -join "`n").Trim()
    }
    & $Command @Arguments | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "$Command failed (exit $LASTEXITCODE)." }
}

function Write-Utf8 {
    param([string] $Path, [string] $Text)
    [System.IO.File]::WriteAllText($Path, ($Text -replace "`r`n", "`n"), [System.Text.UTF8Encoding]::new($false))
}

function File-Record {
    param([string] $Path)
    $item = Get-Item -LiteralPath $Path
    return [ordered]@{ file = $item.Name; bytes = $item.Length; sha256 = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
}

function Get-Metadata {
    param([string] $Path)
    $json = Invoke-Checked dotnet @($script:verifierDll, 'inspect', $Path) -Capture
    return $json | ConvertFrom-Json
}

function Write-Checksums {
    param([string] $Directory, [string[]] $Names)
    $lines = foreach ($name in $Names) {
        $hash = (Get-FileHash -LiteralPath (Join-Path $Directory $name) -Algorithm SHA256).Hash.ToLowerInvariant()
        "$hash  $name"
    }
    Write-Utf8 (Join-Path $Directory 'SHA256SUMS.txt') (($lines -join "`n") + "`n")
}

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$originalLocation = Get-Location
try {
    Set-Location -LiteralPath $repoRoot
    if ($Draft -and $Publish) { throw 'Choose -Draft or -Publish, not both.' }
    if ($Repository -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$') { throw 'Invalid GitHub repository name.' }
    foreach ($command in @('git', 'dotnet')) { $null = Get-Command $command -ErrorAction Stop }
    [xml] $project = Get-Content -LiteralPath 'src/PeakReplayLab/PeakReplayLab.csproj' -Raw
    $version = [string] $project.Project.PropertyGroup.Version
    if ($version -notmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$') { throw 'The project must use a three-part release version.' }
    if (-not $Tag) { $Tag = "v$version" }
    if ($Tag -cne "v$version") { throw "Tag $Tag does not match project version v$version." }
    $pluginText = Get-Content -LiteralPath 'src/PeakReplayLab/Plugin.cs' -Raw
    $dataText = Get-Content -LiteralPath 'src/PeakReplayLab/ReplayData.cs' -Raw
    $pluginVersion = [regex]::Match($pluginText, '\[BepInPlugin\("cn\.mylus\.peakreplaylab",\s*"PEAK Replay Lab",\s*"([^"]+)"\)\]').Groups[1].Value
    $recorderVersion = [regex]::Match($dataText, 'Recorder\s*\{[^}]+\}\s*=\s*"PeakReplayLab/([^"]+)"').Groups[1].Value
    if ($pluginVersion -cne $version -or $recorderVersion -cne $version) {
        throw 'csproj, BepInPlugin and ReplayHeader.Recorder versions must agree.'
    }
    $currentSchema = [int] [regex]::Match($dataText, 'CurrentSchema\s*=\s*(\d+)').Groups[1].Value
    $schemaExpression = [regex]::Match($dataText, 'SupportedSchema\(int schema\)\s*=>\s*([^;]+);').Groups[1].Value
    $supportedSchemas = @(@([regex]::Matches($schemaExpression, 'schema\s*==\s*(\d+)') | ForEach-Object { [int] $_.Groups[1].Value }) + $currentSchema | Sort-Object -Unique)
    if ($currentSchema -le 0 -or $supportedSchemas.Count -eq 0) { throw 'Replay schema declarations were not found.' }
    $GameRoot = [System.IO.Path]::GetFullPath($GameRoot).TrimEnd('/', '\')
    $managedDir = Join-Path $GameRoot 'PEAK_Data/Managed'
    $requiredReferences = @(
        (Join-Path $managedDir 'Assembly-CSharp.dll'),
        (Join-Path $GameRoot 'BepInEx/core/BepInEx.dll'),
        (Join-Path $GameRoot 'BepInEx/core/0Harmony.dll')
    )
    foreach ($reference in $requiredReferences) {
        if (-not (Test-Path -LiteralPath $reference -PathType Leaf)) { throw "Missing local compile reference: $reference" }
    }
    $commit = Invoke-Checked git @('rev-parse', 'HEAD') -Capture
    if ($commit -notmatch '^[0-9a-f]{40}$') { throw 'A committed Git checkout is required.' }
    $dirty = Invoke-Checked git @('status', '--porcelain', '--untracked-files=normal') -Capture
    if ($dirty) { throw 'Commit or stash source changes before building a release. The package must identify an exact clean commit.' }
    $existingTag = & git rev-parse --verify "$Tag^{commit}" 2>$null
    if ($LASTEXITCODE -eq 0 -and ($existingTag -join '').Trim() -cne $commit) { throw "Local tag $Tag already points to a different commit." }
    $projects = @('ReplayContract', 'NativeAppearanceContract', 'NativeLoadingContract', 'ReplayViewContract', 'ReplayNameplateContract', 'ReplayAudioSpatialContract')
    Write-Host "Preflight passed: $Tag / $commit"
    if ($PreflightOnly) { return }

    Write-Host 'Building the Mod against local PEAK references...'
    $msbuildRoot = $GameRoot.Replace('\', '/') + '/'
    Invoke-Checked dotnet @('build', 'PeakReplayLab.slnx', '-c', 'Release', '--nologo', '-warnaserror', "-p:PEAKGameRootDir=$msbuildRoot")
    foreach ($name in $projects) {
        Invoke-Checked dotnet @('run', '--project', "tests/$name/$name.csproj", '-c', 'Release', '--no-build')
    }
    Invoke-Checked dotnet @('build', 'tools/ReleaseVerifier/ReleaseVerifier.csproj', '-c', 'Release', '--nologo', '-warnaserror')
    $script:verifierDll = Join-Path $repoRoot 'artifacts/bin/ReleaseVerifier/release/ReleaseVerifier.dll'
    Invoke-Checked dotnet @($script:verifierDll, 'self-test')

    $dll = Join-Path $repoRoot 'artifacts/bin/PeakReplayLab/release/PeakReplayLab.dll'
    $metadata = Get-Metadata $dll
    $covers = @($project.SelectNodes('//EmbeddedResource') | ForEach-Object { [string] $_.LogicalName } | Sort-Object)
    $referenceFiles = @(@(Get-ChildItem -LiteralPath $managedDir -Filter '*.dll' -File | Where-Object { $_.Name -notmatch '^(Mono.*|netstandard|System.*|mscorlib)\.dll$' } | ForEach-Object { $_.FullName }) + $requiredReferences[1..2] | Sort-Object)
    $gameAssembly = Get-Metadata $requiredReferences[0]
    $steamBuildId = $null
    $steamManifest = Join-Path (Split-Path (Split-Path $GameRoot -Parent) -Parent) 'appmanifest_3527290.acf'
    if (Test-Path -LiteralPath $steamManifest -PathType Leaf) {
        $buildMatch = [regex]::Match((Get-Content -LiteralPath $steamManifest -Raw), '"buildid"\s+"(\d+)"')
        if ($buildMatch.Success) { $steamBuildId = [int] $buildMatch.Groups[1].Value }
    }
    $unityVersion = $null
    $unityPlayer = Join-Path $GameRoot 'UnityPlayer.dll'
    if (Test-Path -LiteralPath $unityPlayer -PathType Leaf) {
        $unityVersion = ([System.Diagnostics.FileVersionInfo]::GetVersionInfo($unityPlayer).ProductVersion -split ' ')[0]
    }
    $dllRecord = File-Record $dll
    $manifest = [ordered]@{
        schemaVersion = 1
        tag = $Tag
        version = $version
        commit = $commit
        assembly = [ordered]@{
            name = $metadata.assemblyName; version = $metadata.assemblyVersion
            fileVersion = $metadata.fileVersion; informationalVersion = $metadata.informationalVersion
            mvid = $metadata.mvid; file = $dllRecord.file; bytes = $dllRecord.bytes; sha256 = $dllRecord.sha256
        }
        replay = [ordered]@{ currentSchema = $currentSchema; supportedSchemas = $supportedSchemas }
        game = [ordered]@{
            steamBuildId = $steamBuildId; unityVersion = $unityVersion; assemblyCSharpMvid = $gameAssembly.mvid
            references = @($referenceFiles | ForEach-Object { File-Record $_ } | Sort-Object { $_.file })
        }
        contracts = [ordered]@{ projects = $projects; passed = $true }
        covers = $covers
    }
    $staging = Join-Path $repoRoot "artifacts/releases/$Tag/$($commit.Substring(0, 12))-$([guid]::NewGuid().ToString('N'))"
    $payload = Join-Path $staging 'payload'
    $upload = Join-Path $staging 'upload'
    $null = New-Item -ItemType Directory -Path $payload, $upload
    Copy-Item -LiteralPath $dll -Destination (Join-Path $payload 'PeakReplayLab.dll')
    Write-Utf8 (Join-Path $payload 'build-manifest.json') (($manifest | ConvertTo-Json -Depth 10) + "`n")
    $install = @"
# PEAK 回忆录 $version 实验版

需要 Windows PEAK 和 BepInEx 5。先关闭游戏，把 PeakReplayLab.dll 放入游戏目录的 BepInEx/plugins/，已有同名文件时替换。封面已经嵌入 DLL。
配置、Memories、Recordings 和其他插件保留。此包不包含 PEAK、BepInEx 或原版游戏素材。

F7 打开/退出回忆录；内存模式 F6 保存；持续录制 F4 停止/重开；H 展开/收起控制台；空格暂停；Tab 切换玩家；F9 诊断。回放前先离开好友房间。
当前写入 Schema $currentSchema，读取 Schema $($supportedSchemas -join ' / ')。旧录像仍需匹配原游戏版本、Build ID、程序集和地图分支；游戏更新前后的兼容性请在游戏内确认。

源码：$Repository，$Tag，提交 $commit。
说明与问题反馈：https://github.com/$Repository
SHA256SUMS.txt 校验本包 DLL、安装说明和构建清单。build-manifest.json 记录构建引用的版本/哈希，不包含本机路径或私人录像。
自动测试验证数据与配置逻辑，不能替代 Unity 实机画面、音效和帧率验收。
"@
    Write-Utf8 (Join-Path $payload 'README-INSTALL.md') ($install + "`n")
    Write-Checksums $payload @('PeakReplayLab.dll', 'README-INSTALL.md', 'build-manifest.json')
    $zipName = "PeakReplayLab-$version.zip"
    $zipPath = Join-Path $upload $zipName
    # Exact whitelist; never recursively zip build output or game references.
    $zip = [System.IO.Compression.ZipFile]::Open($zipPath, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($name in @('PeakReplayLab.dll', 'README-INSTALL.md', 'SHA256SUMS.txt', 'build-manifest.json')) {
            $entry = $zip.CreateEntry($name, [System.IO.Compression.CompressionLevel]::Optimal)
            $entry.LastWriteTime = [DateTimeOffset]::new(2000, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
            $outputStream = $entry.Open()
            $inputStream = [System.IO.File]::OpenRead((Join-Path $payload $name))
            try { $inputStream.CopyTo($outputStream) } finally { $inputStream.Dispose(); $outputStream.Dispose() }
        }
    } finally { $zip.Dispose() }
    Invoke-Checked dotnet @($script:verifierDll, 'verify', $zipPath, $repoRoot, $Tag, $commit)
    Copy-Item -LiteralPath (Join-Path $payload 'PeakReplayLab.dll'), (Join-Path $payload 'build-manifest.json') -Destination $upload
    Write-Checksums $upload @($zipName, 'PeakReplayLab.dll', 'build-manifest.json')
    $postBuildDirty = Invoke-Checked git @('status', '--porcelain', '--untracked-files=normal') -Capture
    $postBuildCommit = Invoke-Checked git @('rev-parse', 'HEAD') -Capture
    if ($postBuildDirty -or $postBuildCommit -cne $commit) { throw 'Source changed during release preparation; refusing to upload.' }
    Write-Host "Verified package: $zipPath"
    if (-not ($Draft -or $Publish)) { return }

    $null = Get-Command gh -ErrorAction Stop
    Invoke-Checked gh @('auth', 'status')
    $origin = Invoke-Checked git @('remote', 'get-url', 'origin') -Capture
    if ($origin -notin @("https://github.com/$Repository.git", "https://github.com/$Repository", "git@github.com:$Repository.git")) {
        throw 'Origin does not match the requested publication repository.'
    }
    $remoteCommit = Invoke-Checked gh @('api', "repos/$Repository/commits/$commit", '--jq', '.sha') -Capture
    if ($remoteCommit -cne $commit) { throw 'Push the source commit to GitHub before publishing.' }
    $mainRelation = Invoke-Checked gh @('api', "repos/$Repository/compare/$commit...main", '--jq', '.status') -Capture
    if ($mainRelation -notin @('identical', 'ahead')) { throw 'Merge the release source commit into main before publishing.' }
    $null = Invoke-Checked gh @('workflow', 'view', 'release.yml', '--repo', $Repository) -Capture
    $remoteTags = Invoke-Checked git @('ls-remote', '--tags', 'origin', "refs/tags/$Tag", "refs/tags/$Tag^{}") -Capture
    if ($remoteTags) {
        $tagSha = (($remoteTags -split "`n")[-1] -split '\s+')[0]
        if ($tagSha -cne $commit) { throw "Remote tag $Tag already points to a different commit." }
    } else {
        if (-not $existingTag) { Invoke-Checked git @('tag', '-a', $Tag, $commit, '-m', "PEAK memoir $version") }
        Invoke-Checked git @('push', 'origin', "refs/tags/$Tag")
    }

    $releaseResult = & gh release view $Tag --repo $Repository --json tagName,isDraft,assets 2>$null
    if ($LASTEXITCODE -eq 0) {
        $release = ($releaseResult -join "`n") | ConvertFrom-Json
        $expectedAssets = @($zipName, 'PeakReplayLab.dll', 'SHA256SUMS.txt', 'build-manifest.json') | Sort-Object
        $existingAssets = @($release.assets | ForEach-Object { $_.name } | Sort-Object)
        if (Compare-Object $expectedAssets $existingAssets) { throw 'Existing release assets differ. No assets were overwritten.' }
        $existingDir = Join-Path $staging 'existing-release'
        $null = New-Item -ItemType Directory -Path $existingDir
        Invoke-Checked gh @('release', 'download', $Tag, '--repo', $Repository, '--dir', $existingDir)
        foreach ($name in $expectedAssets) {
            if ((Get-FileHash -LiteralPath (Join-Path $existingDir $name)).Hash -cne (Get-FileHash -LiteralPath (Join-Path $upload $name)).Hash) {
                throw "Existing $Tag asset $name has different bytes. No assets were overwritten; use a new version."
            }
        }
        if (-not $release.isDraft) { Write-Host "Already published with identical assets: https://github.com/$Repository/releases/tag/$Tag"; return }
    } else {
        $notes = @"
PEAK 回忆录 $version 实验版。使用本机 PEAK 引用编译，六套合同测试和安装包校验通过后由 GitHub Actions 复核。

下载 PeakReplayLab-$version.zip，关闭游戏后将其中 PeakReplayLab.dll 放到 BepInEx/plugins/。
需要已有 PEAK / BepInEx 5。安装包内有安装说明和哈希清单；单独 DLL、SHA256SUMS.txt、build-manifest.json 也可下载。

源码提交：$commit。读取 Schema $($supportedSchemas -join ' / ')；实机呈现与性能的验证范围见仓库文档。
"@
        $notesFile = Join-Path $staging 'release-notes.md'
        Write-Utf8 $notesFile ($notes + "`n")
        $assets = @(@($zipName, 'PeakReplayLab.dll', 'SHA256SUMS.txt', 'build-manifest.json') | ForEach-Object { Join-Path $upload $_ })
        Invoke-Checked gh (@('release', 'create', $Tag, '--repo', $Repository, '--target', $commit, '--verify-tag', '--draft', '--prerelease', '--title', "PEAK 回忆录 $Tag", '--notes-file', $notesFile) + $assets)
    }
    $requestId = [guid]::NewGuid().ToString('N')
    $publishInput = if ($Publish) { 'true' } else { 'false' }
    Invoke-Checked gh @('workflow', 'run', 'release.yml', '--repo', $Repository, '--ref', 'main', '-f', "tag=$Tag", '-f', "publish=$publishInput", '-f', "request_id=$requestId")
    $deadline = [DateTime]::UtcNow.AddMinutes(3)
    $run = $null
    while (-not $run -and [DateTime]::UtcNow -lt $deadline) {
        $runs = (Invoke-Checked gh @('run', 'list', '--repo', $Repository, '--workflow', 'release.yml', '--event', 'workflow_dispatch', '--limit', '30', '--json', 'databaseId,displayTitle,url') -Capture) | ConvertFrom-Json
        $run = @($runs | Where-Object { $_.displayTitle -ceq "Release $Tag / $requestId" }) | Select-Object -First 1
        if (-not $run) { Start-Sleep -Seconds 3 }
    }
    if (-not $run) { throw 'Draft assets uploaded, but the dispatched workflow was not located. Check GitHub Actions before retrying.' }
    Write-Host "GitHub verification: $($run.url)"
    Invoke-Checked gh @('run', 'watch', [string] $run.databaseId, '--repo', $Repository, '--exit-status', '--interval', '10', '--compact')
    $finalRelease = (Invoke-Checked gh @('release', 'view', $Tag, '--repo', $Repository, '--json', 'isDraft,url') -Capture) | ConvertFrom-Json
    if ($Publish -and $finalRelease.isDraft) { throw 'Verification completed but the release is still a draft; check the publishing job.' }
    Write-Host "$(if ($finalRelease.isDraft) { 'Verified draft' } else { 'Published prerelease' }): $($finalRelease.url)"
} finally {
    Set-Location -LiteralPath $originalLocation
}
