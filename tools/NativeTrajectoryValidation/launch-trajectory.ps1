param(
    [Parameter(Mandatory=$true)][ValidatePattern('^[A-Za-z0-9-]{1,64}$')][string]$Label,
    [Parameter(Mandatory=$true)][string]$Plan,
    [Parameter(Mandatory=$true)][string]$Endpoint,
    [string]$OutputRoot='',
    [string]$GameRoot='C:\Program Files (x86)\Steam\steamapps\common\PEAK'
)
$ErrorActionPreference='Stop'
$toolRoot=[System.IO.Path]::GetFullPath($PSScriptRoot)
$repoRoot=Split-Path (Split-Path $toolRoot -Parent) -Parent
if(-not $OutputRoot){$OutputRoot=Join-Path $repoRoot 'local/checks/native-trajectory/sessions'}
$output=[System.IO.Path]::GetFullPath($OutputRoot)
$game=(Resolve-Path -LiteralPath $GameRoot).Path
$planPath=(Resolve-Path -LiteralPath $Plan).Path
$uri=[Uri]$Endpoint
if($uri.Scheme -ne 'http' -or $uri.Host -ne '127.0.0.1' -or $uri.Port -lt 1 -or $uri.AbsolutePath -ne '/api/route-uploads' -or $uri.Query -or $uri.Fragment -or $uri.UserInfo){throw 'Only an explicit http://127.0.0.1:PORT/api/route-uploads endpoint is allowed.'}
$settings=Get-Content -LiteralPath $planPath -Raw|ConvertFrom-Json
if($settings.MaximumSeconds -lt 1200 -or $settings.MaximumSeconds -gt 2400){throw 'The explicit test plan must bound runtime to 1200–2400 seconds.'}
if(@(Get-Process PEAK -ErrorAction SilentlyContinue).Count -ne 0){throw 'PEAK must be closed before starting the protected test.'}
$session=Join-Path $output $Label
$launch=Join-Path $output ('launch-'+$Label)
if((Test-Path -LiteralPath $session) -or (Test-Path -LiteralPath $launch)){throw 'Use a fresh test label.'}
$probe=Join-Path $repoRoot 'artifacts/bin/TrajectoryAuditProbe/release/PeakReplayLab.LocalTrajectoryAudit.dll'
if(-not(Test-Path -LiteralPath $probe -PathType Leaf)){throw 'Build tools/NativeTrajectoryValidation/driver/TrajectoryAuditProbe.csproj in Release first.'}
$installedProbe=Join-Path $game 'BepInEx/plugins/PeakReplayLab.LocalTrajectoryAudit.dll'
if(Test-Path -LiteralPath $installedProbe){throw 'A companion DLL already exists; inspect and remove the prior test companion before launching.'}
$save=Join-Path $env:USERPROFILE 'AppData/LocalLow/LandCrab/PEAK/quicksave.peak'
if(Test-Path -LiteralPath $save -PathType Container){throw 'The actual native quicksave path is a directory.'}
New-Item -ItemType Directory -Path $launch -Force|Out-Null
$backup=Join-Path $launch 'protected-before'
New-Item -ItemType Directory -Path $backup|Out-Null
$saveExists=Test-Path -LiteralPath $save -PathType Leaf
$saveHash='ABSENT'
if($saveExists){Copy-Item -LiteralPath $save -Destination (Join-Path $backup 'quicksave.peak');$saveHash=(Get-FileHash -LiteralPath $save -Algorithm SHA256).Hash}
$config=Join-Path $game 'BepInEx/config'
$configBackup=Join-Path $backup 'config'
New-Item -ItemType Directory -Path $configBackup|Out-Null
$proof=@(Get-ChildItem -LiteralPath $config -Filter '*.cfg' -File -Recurse|ForEach-Object {
    $relative=[System.IO.Path]::GetRelativePath($config,$_.FullName)
    $destination=Join-Path $configBackup $relative
    New-Item -ItemType Directory -Path (Split-Path $destination -Parent) -Force|Out-Null
    Copy-Item -LiteralPath $_.FullName -Destination $destination
    [pscustomobject]@{File=$relative;Hash=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash}
})
ConvertTo-Json -InputObject $proof|Set-Content -LiteralPath (Join-Path $backup 'config-hashes.json') -Encoding utf8
Copy-Item -LiteralPath $planPath -Destination (Join-Path $launch 'plan.json')
Copy-Item -LiteralPath $probe -Destination $installedProbe
$probeHash=(Get-FileHash -LiteralPath $installedProbe -Algorithm SHA256).Hash
$start=New-Object System.Diagnostics.ProcessStartInfo
$start.FileName=Join-Path $game 'PEAK.exe';$start.WorkingDirectory=$game;$start.UseShellExecute=$false
$start.Environment['SteamAppId']='3527290';$start.Environment['SteamGameId']='3527290'
foreach($argument in @('-screen-fullscreen','0','-screen-width','1280','-screen-height','720','-logFile',(Join-Path $launch 'UnityPlayer.log'),'--peak-trajectory-audit',"--peak-trajectory-label=$Label","--peak-trajectory-output-root=$output","--peak-trajectory-plan=$(Join-Path $launch 'plan.json')","--peak-trajectory-endpoint=$($uri.AbsoluteUri)","--peak-trajectory-save-path=$save","--peak-trajectory-save-hash=$saveHash")){$start.ArgumentList.Add($argument)}
try{$started=[System.Diagnostics.Process]::Start($start)}catch{if((Get-FileHash -LiteralPath $installedProbe).Hash -eq $probeHash){Remove-Item -LiteralPath $installedProbe};throw}
$metadata=[pscustomobject]@{Process=$started.Id;Label=$Label;Session=$session;Launch=$launch;GameRoot=$game;MaximumSeconds=[int]$settings.MaximumSeconds;Endpoint=$uri.AbsoluteUri;ProtectedSavePath=$save;SaveExistedBefore=[bool]$saveExists;SaveHashBefore=$saveHash;ConfigDirectory=$config;InstalledProbe=$installedProbe;ProbeHash=$probeHash;InstalledDllHash=(Get-FileHash -LiteralPath (Join-Path $game 'BepInEx/plugins/PeakReplayLab.dll') -Algorithm SHA256).Hash}
$metadata|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $launch 'launch.json') -Encoding utf8
$watch=New-Object System.Diagnostics.ProcessStartInfo
$watch.FileName=(Get-Process -Id $PID).Path;$watch.UseShellExecute=$false;$watch.WindowStyle=[System.Diagnostics.ProcessWindowStyle]::Hidden
foreach($argument in @('-NoProfile','-File',(Join-Path $toolRoot 'watch-trajectory.ps1'),'-GameProcessId',$started.Id.ToString(),'-Launch',$launch)){$watch.ArgumentList.Add($argument)}
[System.Diagnostics.Process]::Start($watch)|Out-Null
$metadata|Select-Object Process,Label,Session,MaximumSeconds,InstalledDllHash|ConvertTo-Json -Compress
