param([Parameter(Mandatory=$true)][int]$GameProcessId,
      [Parameter(Mandatory=$true)][string]$Session,
      [Parameter(Mandatory=$true)][string]$Launch)
$ErrorActionPreference='Stop'
$begun=[DateTime]::UtcNow
$process=Get-Process -Id $GameProcessId -ErrorAction SilentlyContinue
if(-not $process -or $process.ProcessName -ne 'PEAK'){exit}
$started=$process.StartTime
while(-not $process.HasExited){
    Start-Sleep -Seconds 10
    $process.Refresh()
    if($process.HasExited){break}
    $heartbeat=Join-Path $Session 'heartbeat.jsonl'
    $phase=Join-Path $Session 'phase.json'
    $latest=$begun
    foreach($path in @($heartbeat,$phase)){
        if(Test-Path -LiteralPath $path){$time=(Get-Item -LiteralPath $path).LastWriteTimeUtc;if($time -gt $latest){$latest=$time}}
    }
    $silent=([DateTime]::UtcNow-$latest).TotalSeconds
    if($silent -lt 120 -and ([DateTime]::UtcNow-$begun).TotalSeconds -lt 1500){continue}
    $phaseData=if(Test-Path -LiteralPath $phase){Get-Content -LiteralPath $phase -Raw|ConvertFrom-Json}else{$null}
    [pscustomobject]@{Passed=$false;Reason='Native route stopped making progress or exceeded its runtime bound';SilenceSeconds=$silent;GameProcessId=$GameProcessId;Phase=$phaseData;ForcedExit=$true}|ConvertTo-Json -Depth 8|Set-Content -LiteralPath (Join-Path $Launch 'watchdog.json') -Encoding utf8
    $current=Get-Process -Id $GameProcessId -ErrorAction SilentlyContinue
    if($current -and $current.ProcessName -eq 'PEAK' -and $current.StartTime -eq $started){Stop-Process -Id $GameProcessId}
    break
}
$metadata=Get-Content -LiteralPath (Join-Path $Launch 'launch.json') -Raw|ConvertFrom-Json
if($metadata.ProtectedSavePath -and $metadata.ConfigDirectory){
    $saveHash=if(Test-Path -LiteralPath $metadata.ProtectedSavePath){(Get-FileHash -LiteralPath $metadata.ProtectedSavePath -Algorithm SHA256).Hash}else{'ABSENT'}
    $configProof=Get-Content -LiteralPath (Join-Path $Launch 'protected-before/config-hashes.json') -Raw|ConvertFrom-Json
    # BepInEx can rewrite only its version/comment header before the opt-in
    # protection plugin starts. Restore that exact original file after exit
    # only when every non-comment setting is byte-for-byte equivalent.
    $headerRestored=@()
    foreach($entry in $configProof){
        if($entry.File -ne 'cn.mylus.peakreplaylab.cfg'){continue}
        $currentPath=Join-Path $metadata.ConfigDirectory $entry.File
        $backupPath=Join-Path $Launch ('protected-before/config/'+$entry.File)
        if(-not(Test-Path -LiteralPath $currentPath) -or -not(Test-Path -LiteralPath $backupPath)){continue}
        if((Get-FileHash -LiteralPath $currentPath -Algorithm SHA256).Hash -eq $entry.Hash){continue}
        $originalPayload=(@(Get-Content -LiteralPath $backupPath|ForEach-Object {$_.Trim()}|Where-Object {$_ -and -not $_.StartsWith('#')}) -join "`n")
        $currentPayload=(@(Get-Content -LiteralPath $currentPath|ForEach-Object {$_.Trim()}|Where-Object {$_ -and -not $_.StartsWith('#')}) -join "`n")
        if($originalPayload -ceq $currentPayload){
            Copy-Item -LiteralPath $backupPath -Destination $currentPath -Force
            $headerRestored+=$entry.File
        }
    }
    $changed=@($configProof|Where-Object {
        $path=Join-Path $metadata.ConfigDirectory $_.File
        -not (Test-Path -LiteralPath $path) -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $_.Hash
    }|ForEach-Object {$_.File})
    [pscustomobject]@{ProcessExited=$true;QuicksaveUnchanged=($saveHash -eq $metadata.SaveHashBefore);ConfigurationsUnchanged=($changed.Count -eq 0);RestoredCommentOnlyHeaders=$headerRestored;ChangedConfigurationFiles=$changed}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $Session 'disk-protection-proof.json') -Encoding utf8
}
