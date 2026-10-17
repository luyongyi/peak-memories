param([Parameter(Mandatory=$true)][int]$GameProcessId,[Parameter(Mandatory=$true)][string]$Launch)
$ErrorActionPreference='Stop'
$metadata=Get-Content -LiteralPath (Join-Path $Launch 'launch.json') -Raw|ConvertFrom-Json
$startedUtc=if($metadata.ProcessStartUtc -is [DateTime]){$metadata.ProcessStartUtc.ToUniversalTime()}else{[DateTime]::Parse([string]$metadata.ProcessStartUtc,[Globalization.CultureInfo]::InvariantCulture,[Globalization.DateTimeStyles]::RoundtripKind).ToUniversalTime()}
$process=Get-Process -Id $GameProcessId -ErrorAction SilentlyContinue
if($process -and ($process.ProcessName -ne 'PEAK' -or $process.StartTime.ToUniversalTime() -ne $startedUtc)){throw 'The monitored process does not match the exact launched PEAK process.'}
$forced=$false
while($process -and -not $process.HasExited){
    Start-Sleep -Seconds 1
    $process.Refresh();if($process.HasExited){break}
    if(([DateTime]::UtcNow-$startedUtc).TotalSeconds -lt [int]$metadata.MaximumSeconds){continue}
    $current=Get-Process -Id $GameProcessId -ErrorAction SilentlyContinue
    if($current -and $current.ProcessName -eq 'PEAK' -and $current.StartTime.ToUniversalTime() -eq $startedUtc){Stop-Process -Id $GameProcessId;$forced=$true}
    break
}
New-Item -ItemType Directory -Path $metadata.Session -Force|Out-Null
if($process -and -not $process.WaitForExit(15000)){
    [pscustomobject]@{Passed=$false;ProcessExited=$false;CompanionRemoved=$false;CleanupDeferred=$true;Reason='The original process has not confirmed exit.'}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $metadata.Session 'disk-protection-proof.json') -Encoding utf8
    exit 1
}
if($forced){[pscustomobject]@{Passed=$false;ForcedExit=$true;Reason='The protected smoke exceeded its process runtime bound.'}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $Launch 'watchdog.json') -Encoding utf8}
$proof=@(Get-Content -LiteralPath (Join-Path $Launch 'protected-before/config-hashes.json') -Raw|ConvertFrom-Json)
$headers=@()
foreach($entry in $proof){
    $currentPath=Join-Path $metadata.ConfigDirectory $entry.File
    $backupPath=Join-Path $Launch ('protected-before/config/'+$entry.File)
    if(-not(Test-Path -LiteralPath $currentPath -PathType Leaf) -or -not(Test-Path -LiteralPath $backupPath -PathType Leaf)){continue}
    if((Get-FileHash -LiteralPath $currentPath -Algorithm SHA256).Hash -eq $entry.Hash){continue}
    $originalPayload=(@(Get-Content -LiteralPath $backupPath|ForEach-Object {$_.Trim()}|Where-Object {$_ -and -not $_.StartsWith('#')}) -join "`n")
    $currentPayload=(@(Get-Content -LiteralPath $currentPath|ForEach-Object {$_.Trim()}|Where-Object {$_ -and -not $_.StartsWith('#')}) -join "`n")
    if($originalPayload -ceq $currentPayload){Copy-Item -LiteralPath $backupPath -Destination $currentPath -Force;$headers+=$entry.File}
}
$changed=@($proof|Where-Object {$path=Join-Path $metadata.ConfigDirectory $_.File;-not(Test-Path -LiteralPath $path -PathType Leaf) -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $_.Hash}|ForEach-Object {$_.File})
$original=@($proof|ForEach-Object {$_.File})
$new=@(Get-ChildItem -LiteralPath $metadata.ConfigDirectory -Filter '*.cfg' -File -Recurse|ForEach-Object {[IO.Path]::GetRelativePath($metadata.ConfigDirectory,$_.FullName)}|Where-Object {$_ -notin $original})
$saveDirectory=Test-Path -LiteralPath $metadata.ProtectedSavePath -PathType Container
$saveExists=Test-Path -LiteralPath $metadata.ProtectedSavePath -PathType Leaf
$saveHash=if($saveExists){(Get-FileHash -LiteralPath $metadata.ProtectedSavePath -Algorithm SHA256).Hash}elseif($saveDirectory){'INVALID_DIRECTORY'}else{'ABSENT'}
$expectedProbe=[IO.Path]::GetFullPath((Join-Path $metadata.GameRoot 'BepInEx/plugins/PeakReplayLab.LocalCinemaSmoke.dll'))
if(-not[string]::Equals([IO.Path]::GetFullPath($metadata.InstalledProbe),$expectedProbe,[StringComparison]::OrdinalIgnoreCase)){throw 'The cleanup target is not the exact known smoke companion path.'}
$removed=$false
if(Test-Path -LiteralPath $expectedProbe){if((Get-FileHash -LiteralPath $expectedProbe -Algorithm SHA256).Hash -eq $metadata.ProbeHash){Remove-Item -LiteralPath $expectedProbe;$removed=$true}}else{$removed=$true}
$recordingUnchanged=(Test-Path -LiteralPath $metadata.Recording -PathType Leaf) -and (Get-FileHash -LiteralPath $metadata.Recording -Algorithm SHA256).Hash -eq $metadata.RecordingHash
$mod=Join-Path $metadata.GameRoot 'BepInEx/plugins/PeakReplayLab.dll'
$modHash=if(Test-Path -LiteralPath $mod -PathType Leaf){(Get-FileHash -LiteralPath $mod -Algorithm SHA256).Hash}else{'ABSENT'}
$saveUnchanged=-not $saveDirectory -and $saveExists -eq $metadata.SaveExistedBefore -and $saveHash -ceq $metadata.SaveHashBefore
$configUnchanged=$changed.Count -eq 0 -and $new.Count -eq 0
$sidecarRemoved=$true
$sidecarCleanupError=''
$sidecarProofPath=Join-Path $metadata.Session 'sidecar-proof.json'
if(Test-Path -LiteralPath $sidecarProofPath -PathType Leaf){
    try{
        $sidecar=Get-Content -LiteralPath $sidecarProofPath -Raw|ConvertFrom-Json
        if(-not $sidecar.CreatedByProbe -or [string]$sidecar.ProjectName -cnotmatch '^cinema-smoke-[0-9a-f]{16}$'){throw 'The sidecar proof does not identify the unique smoke project.'}
        if(-not[string]::Equals([IO.Path]::GetFullPath([string]$sidecar.Recording),[IO.Path]::GetFullPath([string]$metadata.Recording),[StringComparison]::OrdinalIgnoreCase)){throw 'The sidecar proof belongs to another source recording.'}
        $expectedSidecar=[IO.Path]::GetFullPath([string]$metadata.Recording)+'.'+[string]$sidecar.ProjectName+'.camera.json'
        if(-not[string]::Equals([IO.Path]::GetFullPath([string]$sidecar.Path),$expectedSidecar,[StringComparison]::OrdinalIgnoreCase)){throw 'The sidecar cleanup target differs from the exact generated filename.'}
        $sidecarParent=[IO.Path]::GetDirectoryName($expectedSidecar)
        while($sidecarParent){
            if(([IO.File]::GetAttributes($sidecarParent) -band [IO.FileAttributes]::ReparsePoint) -ne 0){throw 'The generated sidecar ancestor is now a link or junction.'}
            $sidecarParent=[IO.Path]::GetDirectoryName($sidecarParent)
        }
        if(Test-Path -LiteralPath $expectedSidecar -PathType Container){throw 'The generated sidecar path is now a directory.'}
        if(Test-Path -LiteralPath $expectedSidecar -PathType Leaf){
            if([string]$sidecar.Hash -notmatch '^[A-Fa-f0-9]{64}$' -or ((Get-Item -LiteralPath $expectedSidecar).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or (Get-FileHash -LiteralPath $expectedSidecar -Algorithm SHA256).Hash -ine [string]$sidecar.Hash){throw 'The generated sidecar hash/link proof changed; it was retained for inspection.'}
            Remove-Item -LiteralPath $expectedSidecar
        }
        $sidecarRemoved=-not(Test-Path -LiteralPath $expectedSidecar)
        $sidecar.Removed=$sidecarRemoved
        $sidecar|ConvertTo-Json|Set-Content -LiteralPath $sidecarProofPath -Encoding utf8
    }catch{$sidecarRemoved=$false;$sidecarCleanupError=$_.Exception.Message}
}
$ownedFilesRemoved=$true
$ownedCleanupErrors=@()
$ownedProofPath=Join-Path $metadata.Session 'owned-files-proof.json'
if(Test-Path -LiteralPath $ownedProofPath -PathType Leaf){
    try{
        $ownedProof=Get-Content -LiteralPath $ownedProofPath -Raw|ConvertFrom-Json
        if([string]$ownedProof.ProjectName -cnotmatch '^cinema-smoke-[0-9a-f]{16}$'){throw 'The owned-files proof has no unique smoke project.'}
        if(-not[string]::Equals([IO.Path]::GetFullPath([string]$ownedProof.Recording),[IO.Path]::GetFullPath([string]$metadata.Recording),[StringComparison]::OrdinalIgnoreCase)){throw 'Owned-files proof belongs to another source recording.'}
        $ownedEntries=@($ownedProof.Files)
        if($ownedEntries.Count -gt 8){throw 'Too many owned-file cleanup entries.'}
        $seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach($owned in $ownedEntries){
            try{
                $allowedNames=@([string]$ownedProof.ProjectName,([string]$ownedProof.ProjectName+'-copy'),([string]$ownedProof.ProjectName+'-renamed'))
                if(-not $owned.CreatedByProbe -or [string]$owned.ProjectName -cnotin $allowedNames -or [string]$owned.Kind -cnotin @('Plan','Recovery')){throw 'Unrecognized owned-file name or kind.'}
                $expectedOwned=[IO.Path]::GetFullPath([string]$metadata.Recording)+'.'+[string]$owned.ProjectName+'.camera.json'
                if([string]$owned.Kind -ceq 'Recovery'){$expectedOwned+='.recovery.camera.json'}
                if(-not[string]::Equals([IO.Path]::GetFullPath([string]$owned.Path),$expectedOwned,[StringComparison]::OrdinalIgnoreCase) -or -not $seen.Add($expectedOwned)){throw 'Owned cleanup path differs from its exact generated name or is duplicated.'}
                $parent=[IO.Path]::GetDirectoryName($expectedOwned)
                while($parent){
                    if(([IO.File]::GetAttributes($parent) -band [IO.FileAttributes]::ReparsePoint) -ne 0){throw 'An owned-file ancestor is now a link or junction.'}
                    $parent=[IO.Path]::GetDirectoryName($parent)
                }
                $item=Get-Item -LiteralPath $expectedOwned -Force -ErrorAction SilentlyContinue
                if($item){
                    if(($item.Attributes -band ([IO.FileAttributes]::ReparsePoint -bor [IO.FileAttributes]::Directory)) -ne 0){throw 'An owned-file cleanup target is now a link or directory.'}
                    if([string]$owned.Hash -notmatch '^[A-Fa-f0-9]{64}$' -or (Get-FileHash -LiteralPath $expectedOwned -Algorithm SHA256).Hash -ine [string]$owned.Hash){throw 'Owned file hash changed; it was retained for inspection.'}
                    Remove-Item -LiteralPath $expectedOwned
                }
                $owned.Removed=-not(Test-Path -LiteralPath $expectedOwned)
                if(-not $owned.Removed){throw 'An owned test file survived cleanup.'}
            }catch{$ownedFilesRemoved=$false;$ownedCleanupErrors+=([string]$owned.Path+': '+$_.Exception.Message)}
        }
        $ownedProof|ConvertTo-Json -Depth 6|Set-Content -LiteralPath $ownedProofPath -Encoding utf8
    }catch{$ownedFilesRemoved=$false;$ownedCleanupErrors+=$_.Exception.Message}
}
$summaryPath=Join-Path $metadata.Session 'summary.json'
$probePassed=$false
try{if(Test-Path -LiteralPath $summaryPath -PathType Leaf){$probePassed=[bool](Get-Content -LiteralPath $summaryPath -Raw|ConvertFrom-Json).Passed}}catch{$sidecarCleanupError+= ' Summary parse failed: '+$_.Exception.Message}
$bepLog=Join-Path $metadata.GameRoot 'BepInEx/LogOutput.log'
if(Test-Path -LiteralPath $bepLog){Copy-Item -LiteralPath $bepLog -Destination (Join-Path $metadata.Session 'BepInEx.log')}
[pscustomobject]@{Passed=($probePassed -and -not $forced -and $saveUnchanged -and $configUnchanged -and $recordingUnchanged -and $removed -and $sidecarRemoved -and $ownedFilesRemoved -and $modHash -eq $metadata.InstalledDllHash);ProcessExited=$true;ForcedExit=$forced;QuicksaveUnchanged=$saveUnchanged;ConfigurationsUnchanged=$configUnchanged;RecordingUnchanged=$recordingUnchanged;ChangedConfigurationFiles=$changed;NewConfigurationFiles=$new;RestoredCommentOnlyHeaders=$headers;CompanionRemoved=$removed;TestSidecarRemoved=$sidecarRemoved;OwnedTestFilesRemoved=$ownedFilesRemoved;OwnedCleanupErrors=$ownedCleanupErrors;SidecarCleanupError=$sidecarCleanupError;InstalledDllHashAfter=$modHash}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $metadata.Session 'disk-protection-proof.json') -Encoding utf8
