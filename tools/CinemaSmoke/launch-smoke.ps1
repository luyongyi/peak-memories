param(
    [Parameter(Mandatory=$true)][ValidatePattern('^[A-Za-z0-9-]{1,64}$')][string]$Label,
    [Parameter(Mandatory=$true)][string]$Recording,
    [Parameter(Mandatory=$true)][string]$OutputRoot,
    [string]$Probe='',
    [string]$GameRoot='C:\Program Files (x86)\Steam\steamapps\common\PEAK',
    [ValidateRange(30,180)][int]$MaximumSeconds=180,
    [ValidateRange(0,120)][int]$ManualSeconds=0
)
$ErrorActionPreference='Stop'
if($PSVersionTable.PSVersion.Major -lt 7){throw 'Run this launcher with PowerShell 7 or newer.'}
$toolRoot=[IO.Path]::GetFullPath($PSScriptRoot)
$repoRoot=Split-Path (Split-Path $toolRoot -Parent) -Parent
$game=(Resolve-Path -LiteralPath $GameRoot).Path
$recordingPath=(Resolve-Path -LiteralPath $Recording).Path
$output=[IO.Path]::GetFullPath($OutputRoot)
if([IO.Path]::GetExtension($recordingPath) -ine '.peakrun' -or (Get-Item -LiteralPath $recordingPath).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Supply an ordinary sealed .peakrun recording, not a link.'}
if(@(Get-Process PEAK -ErrorAction SilentlyContinue).Count){throw 'PEAK must be closed before the protected smoke.'}
$session=Join-Path $output $Label
$launch=Join-Path $output ('launch-'+$Label)
if((Test-Path -LiteralPath $session) -or (Test-Path -LiteralPath $launch)){throw 'Use a fresh smoke label.'}
if(-not $Probe){$Probe=Join-Path $repoRoot 'artifacts/bin/CinemaSmoke/release/PeakReplayLab.LocalCinemaSmoke.dll'}
$probePath=(Resolve-Path -LiteralPath $Probe).Path
if([IO.Path]::GetFileName($probePath) -cne 'PeakReplayLab.LocalCinemaSmoke.dll'){throw 'The explicit companion DLL filename is not the cinema smoke probe.'}
$installedProbe=[IO.Path]::GetFullPath((Join-Path $game 'BepInEx/plugins/PeakReplayLab.LocalCinemaSmoke.dll'))
if(Test-Path -LiteralPath $installedProbe){throw 'A cinema smoke companion already exists; inspect the previous run before continuing.'}
$save=Join-Path $env:USERPROFILE 'AppData/LocalLow/LandCrab/PEAK/quicksave.peak'
$config=Join-Path $game 'BepInEx/config'
$mod=Join-Path $game 'BepInEx/plugins/PeakReplayLab.dll'
foreach($directory in @($config,(Split-Path $save -Parent),(Split-Path $recordingPath -Parent))){
    $protected=[IO.Path]::GetFullPath($directory).TrimEnd('\','/')
    if($output.Equals($protected,[StringComparison]::OrdinalIgnoreCase) -or $output.StartsWith($protected+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'The independent output cannot be inside protected save, config, or recording directories.'}
}
if(Test-Path -LiteralPath $save -PathType Container){throw 'The native quicksave path is a directory.'}
if(-not(Test-Path -LiteralPath $mod -PathType Leaf)){throw 'Install the intended candidate Mod before this test; the launcher does not replace it.'}
New-Item -ItemType Directory -Path $launch -Force|Out-Null
$backup=Join-Path $launch 'protected-before'
New-Item -ItemType Directory -Path $backup -Force|Out-Null
$saveExists=Test-Path -LiteralPath $save -PathType Leaf
$saveHash='ABSENT'
if($saveExists){Copy-Item -LiteralPath $save -Destination (Join-Path $backup 'quicksave.peak');$saveHash=(Get-FileHash -LiteralPath $save -Algorithm SHA256).Hash}
$configBackup=Join-Path $backup 'config'
New-Item -ItemType Directory -Path $configBackup -Force|Out-Null
$proof=@(Get-ChildItem -LiteralPath $config -Filter '*.cfg' -File -Recurse|ForEach-Object {
    $relative=[IO.Path]::GetRelativePath($config,$_.FullName)
    $destination=Join-Path $configBackup $relative
    New-Item -ItemType Directory -Path (Split-Path $destination -Parent) -Force|Out-Null
    Copy-Item -LiteralPath $_.FullName -Destination $destination
    [pscustomobject]@{File=$relative;Hash=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash}
})
ConvertTo-Json -InputObject $proof|Set-Content -LiteralPath (Join-Path $backup 'config-hashes.json') -Encoding utf8
Copy-Item -LiteralPath $probePath -Destination $installedProbe
$probeHash=(Get-FileHash -LiteralPath $installedProbe -Algorithm SHA256).Hash
$start=[Diagnostics.ProcessStartInfo]::new()
$start.FileName=Join-Path $game 'PEAK.exe';$start.WorkingDirectory=$game;$start.UseShellExecute=$false
$start.Environment['SteamAppId']='3527290';$start.Environment['SteamGameId']='3527290'
foreach($argument in @('-screen-fullscreen','0','-screen-width','1280','-screen-height','720','-logFile',(Join-Path $launch 'UnityPlayer.log'),'--peak-cinema-smoke',"--cinema-output=$session","--cinema-recording=$recordingPath","--cinema-save-path=$save","--cinema-save-hash=$saveHash","--cinema-max-seconds=$MaximumSeconds","--cinema-manual-seconds=$ManualSeconds")){$start.ArgumentList.Add($argument)}
$started=$null
try{
    $started=[Diagnostics.Process]::Start($start)
    $metadata=[pscustomobject]@{Process=$started.Id;ProcessStartUtc=$started.StartTime.ToUniversalTime().ToString('o');Label=$Label;Session=$session;Launch=$launch;GameRoot=$game;MaximumSeconds=$MaximumSeconds;ProtectedSavePath=$save;SaveExistedBefore=[bool]$saveExists;SaveHashBefore=$saveHash;ConfigDirectory=$config;InstalledProbe=$installedProbe;ProbeHash=$probeHash;InstalledDllHash=(Get-FileHash -LiteralPath $mod -Algorithm SHA256).Hash;Recording=$recordingPath;RecordingHash=(Get-FileHash -LiteralPath $recordingPath -Algorithm SHA256).Hash}
    $metadata|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $launch 'launch.json') -Encoding utf8
    $watch=[Diagnostics.ProcessStartInfo]::new();$watch.FileName=(Get-Process -Id $PID).Path;$watch.UseShellExecute=$false;$watch.WindowStyle=[Diagnostics.ProcessWindowStyle]::Hidden
    foreach($argument in @('-NoProfile','-File',(Join-Path $toolRoot 'watch-smoke.ps1'),'-GameProcessId',$started.Id.ToString(),'-Launch',$launch)){$watch.ArgumentList.Add($argument)}
    [Diagnostics.Process]::Start($watch)|Out-Null
    $metadata|Select-Object Process,Label,Session,MaximumSeconds,InstalledDllHash|ConvertTo-Json -Compress
}catch{
    if($started -and -not $started.HasExited){
        $current=Get-Process -Id $started.Id -ErrorAction SilentlyContinue
        if($current -and $current.ProcessName -eq 'PEAK' -and $current.StartTime -eq $started.StartTime){Stop-Process -Id $started.Id}
        $started.WaitForExit(15000)|Out-Null
    }
    if(((-not $started) -or $started.HasExited) -and (Test-Path -LiteralPath $installedProbe) -and (Get-FileHash -LiteralPath $installedProbe -Algorithm SHA256).Hash -eq $probeHash){Remove-Item -LiteralPath $installedProbe}
    throw
}
