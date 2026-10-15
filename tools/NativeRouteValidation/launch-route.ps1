param(
    [Parameter(Mandatory=$true)][ValidatePattern('^[A-Za-z0-9-]{1,64}$')][string]$Label,
    [ValidatePattern('^Level_[0-9]+$')][string]$Scene='Level_0',
    [ValidateRange(15,300)][int]$FirstWait=150,
    [ValidateRange(15,120)][int]$LaterWait=45,
    [ValidateRange(15,120)][int]$FarWait=45,
    [ValidateRange(15,300)][int]$FinalWait=120,
    [ValidateRange(1,5)][int]$Biomes=5,
    [ValidateRange(0,3)][int]$ObserveFromIndex=0,
    [switch]$FaunaVisual,
    [switch]$SnowVisual,
    [string]$RepositoryRoot='',
    [string]$GameRoot='C:\Program Files (x86)\Steam\steamapps\common\PEAK',
    [string]$SavePath=(Join-Path $env:USERPROFILE 'AppData\LocalLow\LandCrab\PEAK\quicksave.peak')
)
$ErrorActionPreference='Stop'
if($SnowVisual -and ($Scene -ne 'Level_9' -or $Biomes -lt 3 -or $ObserveFromIndex -gt 2)){throw 'SnowVisual requires Level_9, at least three biomes, and ObserveFromIndex no later than 2; it triggers by the actual native Alpine biome.'}
$taskRoot=[System.IO.Path]::GetFullPath($PSScriptRoot)
$game=(Resolve-Path -LiteralPath $GameRoot).Path
$bundledProbe=Join-Path $taskRoot 'driver\PeakReplayLab.LocalRouteAudit.0827.dll'
if(Test-Path -LiteralPath $bundledProbe){$probe=$bundledProbe}
else{
    $repoRoot=if($RepositoryRoot){(Resolve-Path -LiteralPath $RepositoryRoot).Path}else{$taskRoot}
    while(-not (Test-Path -LiteralPath (Join-Path $repoRoot 'src/PeakReplayLab/PeakReplayLab.csproj'))){
        $parent=Split-Path -Parent $repoRoot
        if(-not $parent -or $parent -eq $repoRoot){throw 'Pass RepositoryRoot or supply the bundled test DLL.'}
        $repoRoot=$parent
    }
    $probe=Join-Path $repoRoot 'artifacts\bin\RouteAuditProbe\release\PeakReplayLab.LocalRouteAudit.0827.dll'
}
$save=[System.IO.Path]::GetFullPath($SavePath)
$nativeSave=[System.IO.Path]::GetFullPath((Join-Path $env:USERPROFILE 'AppData\LocalLow\LandCrab\PEAK\quicksave.peak'))
if(-not [string]::Equals($save,$nativeSave,[StringComparison]::OrdinalIgnoreCase)){throw 'SavePath must be the native PEAK quicksave.peak path; an alternate proof does not protect the game save.'}
if(Test-Path -LiteralPath $save -PathType Container){throw 'Native save path is a directory, not an absent or existing quicksave file.'}
$saveExists=Test-Path -LiteralPath $save -PathType Leaf
$outputRoot=Join-Path $taskRoot 'sessions'
$launch=Join-Path $taskRoot ('launch-'+$Label)
$session=Join-Path $outputRoot $Label
if(@(Get-Process PEAK -ErrorAction SilentlyContinue).Count -ne 0){throw 'PEAK is already running.'}
if((Test-Path -LiteralPath $launch) -or (Test-Path -LiteralPath $session)){throw 'Use a fresh test label.'}
if(-not (Test-Path -LiteralPath $probe)){throw 'Build RouteAuditProbe first.'}
New-Item -ItemType Directory -Path $launch|Out-Null
$backup=Join-Path $launch 'protected-before'
New-Item -ItemType Directory -Path $backup|Out-Null
$saveHash='ABSENT'
if($saveExists){
    Copy-Item -LiteralPath $save -Destination (Join-Path $backup 'quicksave.peak')
    $saveHash=(Get-FileHash -LiteralPath $save -Algorithm SHA256).Hash
}
[pscustomobject]@{ProtectedSavePath=$save;ExistedBefore=[bool]$saveExists;HashBefore=$saveHash}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $backup 'save-proof.json') -Encoding utf8
$configDirectory=Join-Path $game 'BepInEx\config'
$configBackup=Join-Path $backup 'config'
New-Item -ItemType Directory -Path $configBackup|Out-Null
$proof=@(Get-ChildItem -LiteralPath $configDirectory -File | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination $configBackup
    [pscustomobject]@{File=$_.Name;Hash=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash}
})
$proof|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $backup 'config-hashes.json') -Encoding utf8
Copy-Item -LiteralPath $probe -Destination (Join-Path $game 'BepInEx\plugins\PeakReplayLab.LocalRouteAudit.0827.dll') -Force
$start=New-Object System.Diagnostics.ProcessStartInfo
$start.FileName=Join-Path $game 'PEAK.exe'
$start.WorkingDirectory=$game
$start.UseShellExecute=$false
$start.WindowStyle=[System.Diagnostics.ProcessWindowStyle]::Hidden
$start.Environment['SteamAppId']='3527290'
$start.Environment['SteamGameId']='3527290'
$arguments=@('-screen-fullscreen','0','-screen-width','1280','-screen-height','720','-logFile',(Join-Path $launch 'UnityPlayer.log'),"--peak-route-label=$Label","--peak-route-output-root=$outputRoot","--peak-route-scene=$Scene","--peak-route-save-path=$save","--peak-route-save-hash=$saveHash","--peak-route-first-wait=$FirstWait","--peak-route-later-wait=$LaterWait","--peak-route-far-wait=$FarWait","--peak-route-final-wait=$FinalWait","--peak-route-biomes=$Biomes","--peak-route-observe-from-index=$ObserveFromIndex")
if($FaunaVisual){$arguments+='--peak-route-fauna-visual'}
if($SnowVisual){$arguments+='--peak-route-snow-visual'}
foreach($argument in $arguments){$start.ArgumentList.Add($argument)}
$started=[System.Diagnostics.Process]::Start($start)
$metadata=[pscustomobject]@{Process=$started.Id;Label=$Label;Scene=$Scene;FirstWait=$FirstWait;LaterWait=$LaterWait;FarWait=$FarWait;FinalWait=$FinalWait;Biomes=$Biomes;ObserveFromIndex=$ObserveFromIndex;FaunaVisual=[bool]$FaunaVisual;SnowVisual=[bool]$SnowVisual;PerformanceMeasurementValid=-not ($FaunaVisual -or $SnowVisual);SaveExistedBefore=[bool]$saveExists;SaveHashBefore=$saveHash;ProtectedSavePath=$save;ConfigDirectory=$configDirectory;InstalledDllHash=(Get-FileHash -LiteralPath (Join-Path $game 'BepInEx\plugins\PeakReplayLab.dll') -Algorithm SHA256).Hash}
$metadata|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $launch 'launch.json') -Encoding utf8
$watch=New-Object System.Diagnostics.ProcessStartInfo
$watch.FileName=(Get-Process -Id $PID).Path
$watch.UseShellExecute=$false
$watch.WindowStyle=[System.Diagnostics.ProcessWindowStyle]::Hidden
foreach($argument in @('-NoProfile','-File',(Join-Path $taskRoot 'watch-route.ps1'),'-GameProcessId',$started.Id.ToString(),'-Session',$session,'-Launch',$launch)){$watch.ArgumentList.Add($argument)}
[System.Diagnostics.Process]::Start($watch)|Out-Null
$metadata|Select-Object Process,Label,Scene,FirstWait,LaterWait,FarWait,FinalWait,Biomes,ObserveFromIndex|ConvertTo-Json -Compress
