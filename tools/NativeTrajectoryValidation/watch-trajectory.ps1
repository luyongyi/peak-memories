param([Parameter(Mandatory=$true)][int]$GameProcessId,[Parameter(Mandatory=$true)][string]$Launch)
$ErrorActionPreference='Stop'
$metadata=Get-Content -LiteralPath (Join-Path $Launch 'launch.json') -Raw|ConvertFrom-Json
$begun=[DateTime]::UtcNow
$process=Get-Process -Id $GameProcessId -ErrorAction SilentlyContinue
if($process -and $process.ProcessName -ne 'PEAK'){throw 'Refusing to monitor a different process.'}
$started=if($process){$process.StartTime}else{$null}
while($process -and -not $process.HasExited){
    Start-Sleep -Seconds 5
    $process.Refresh();if($process.HasExited){break}
    $heartbeat=Join-Path $metadata.Session 'heartbeat.json'
    $latest=if(Test-Path -LiteralPath $heartbeat){(Get-Item -LiteralPath $heartbeat).LastWriteTimeUtc}else{$begun}
    if(([DateTime]::UtcNow-$latest).TotalSeconds -lt 180 -and ([DateTime]::UtcNow-$begun).TotalSeconds -lt ($metadata.MaximumSeconds+60)){continue}
    [pscustomobject]@{Passed=$false;ForcedExit=$true;Reason='Native trajectory heartbeat/runtime bound exceeded.'}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $Launch 'watchdog.json') -Encoding utf8
    $current=Get-Process -Id $GameProcessId -ErrorAction SilentlyContinue
    if($current -and $current.ProcessName -eq 'PEAK' -and $current.StartTime -eq $started){Stop-Process -Id $GameProcessId}
    break
}
# Kill is asynchronous. Never restore configuration or remove the companion
# until the original process handle confirms that the tested game has exited.
if($process){
    if(-not $process.WaitForExit(15000)){
        New-Item -ItemType Directory -Path $metadata.Session -Force|Out-Null
        [pscustomobject]@{ProcessExited=$false;Passed=$false;Reason='The original game process did not exit after its bounded stop request.';CompanionRemoved=$false;CleanupDeferred=$true}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $metadata.Session 'disk-protection-proof.json') -Encoding utf8
        exit 1
    }
}
New-Item -ItemType Directory -Path $metadata.Session -Force|Out-Null
$proof=@(Get-Content -LiteralPath (Join-Path $Launch 'protected-before/config-hashes.json') -Raw|ConvertFrom-Json)
$headerRestored=@()
foreach($entry in $proof){
    if($entry.File -ne 'cn.mylus.peakreplaylab.cfg'){continue}
    $currentPath=Join-Path $metadata.ConfigDirectory $entry.File
    $backupPath=Join-Path $Launch ('protected-before/config/'+$entry.File)
    if(-not(Test-Path -LiteralPath $currentPath) -or -not(Test-Path -LiteralPath $backupPath)){continue}
    if((Get-FileHash -LiteralPath $currentPath -Algorithm SHA256).Hash -eq $entry.Hash){continue}
    $originalPayload=(@(Get-Content -LiteralPath $backupPath|ForEach-Object {$_.Trim()}|Where-Object {$_ -and -not $_.StartsWith('#')}) -join "`n")
    $currentPayload=(@(Get-Content -LiteralPath $currentPath|ForEach-Object {$_.Trim()}|Where-Object {$_ -and -not $_.StartsWith('#')}) -join "`n")
    if($originalPayload -ceq $currentPayload){Copy-Item -LiteralPath $backupPath -Destination $currentPath -Force;$headerRestored+=$entry.File}
}
$changed=@($proof|Where-Object {$path=Join-Path $metadata.ConfigDirectory $_.File;-not(Test-Path -LiteralPath $path) -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $_.Hash}|ForEach-Object {$_.File})
$original=@($proof|ForEach-Object {$_.File})
$new=@(Get-ChildItem -LiteralPath $metadata.ConfigDirectory -Filter '*.cfg' -File -Recurse|ForEach-Object {[System.IO.Path]::GetRelativePath($metadata.ConfigDirectory,$_.FullName)}|Where-Object {$_ -notin $original})
$saveIsDirectory=Test-Path -LiteralPath $metadata.ProtectedSavePath -PathType Container
$savePresent=Test-Path -LiteralPath $metadata.ProtectedSavePath -PathType Leaf
$saveHash=if($savePresent){(Get-FileHash -LiteralPath $metadata.ProtectedSavePath -Algorithm SHA256).Hash}elseif($saveIsDirectory){'INVALID_DIRECTORY'}else{'ABSENT'}
$expectedProbe=[System.IO.Path]::GetFullPath((Join-Path $metadata.GameRoot 'BepInEx/plugins/PeakReplayLab.LocalTrajectoryAudit.dll'))
if(-not[string]::Equals([System.IO.Path]::GetFullPath($metadata.InstalledProbe),$expectedProbe,[StringComparison]::OrdinalIgnoreCase)){throw 'The installed probe cleanup target does not match the exact known companion path.'}
$probeRemoved=$false
if(Test-Path -LiteralPath $expectedProbe){if((Get-FileHash -LiteralPath $expectedProbe -Algorithm SHA256).Hash -eq $metadata.ProbeHash){Remove-Item -LiteralPath $expectedProbe;$probeRemoved=$true}}else{$probeRemoved=$true}
$bepLog=Join-Path $metadata.GameRoot 'BepInEx/LogOutput.log'
if(Test-Path -LiteralPath $bepLog){Copy-Item -LiteralPath $bepLog -Destination (Join-Path $metadata.Session 'BepInEx.log')}
[pscustomobject]@{ProcessExited=$true;QuicksaveUnchanged=(-not $saveIsDirectory -and $savePresent -eq $metadata.SaveExistedBefore -and $saveHash -ceq $metadata.SaveHashBefore);SaveExistedBefore=$metadata.SaveExistedBefore;SaveExistsAfter=$savePresent;SaveHashBefore=$metadata.SaveHashBefore;SaveHashAfter=$saveHash;ConfigurationsUnchanged=($changed.Count -eq 0 -and $new.Count -eq 0);ChangedConfigurationFiles=$changed;NewConfigurationFiles=$new;RestoredCommentOnlyHeaders=$headerRestored;CompanionRemoved=$probeRemoved;InstalledDllHashAfter=(Get-FileHash -LiteralPath (Join-Path $metadata.GameRoot 'BepInEx/plugins/PeakReplayLab.dll') -Algorithm SHA256).Hash}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $metadata.Session 'disk-protection-proof.json') -Encoding utf8
