param(
    [string]$CollectionRoot = "collection/rcp",
    [string]$ReferenceExe = "artifacts/ref_tools/rcp2mid_ref.exe",
    [string]$ReferenceSource = "artifacts/ref_midiconverters_1/rcp2mid.c",
    [string]$FallbackReferenceExe = "artifacts/ref_tools/rcp2mid_ref.exe",
    [string]$FallbackReferenceSource = "artifacts/ref_midiconverters_1/rcp2mid.c",
    [ValidateSet("rcp2mid", "rcpcvc", "x68rcpx")]
    [string]$ReferenceKind = "rcp2mid",
    [string]$X68Program = "tools/rcpx/RRC3013A/RRC3013A/RCD.X",
    [string[]]$X68ProgramArgs = @("-S1024"),
    [string]$X68ChildProgram = "tools/rcpx/RRC3013A/RRC3013A/RCP.X",
    [string]$X68ChildCommand = "RCP.X -P {input}",
    [bool]$X68EnableMidiStub = $true,
    [bool]$X68EnableIoLog = $false,
    [bool]$X68CaptureRunLogs = $false,
    [int]$X68MinConvertTimeoutSec = 20,
    [int]$X68RetryTimeoutSec = 60,
    [bool]$X68CompareByMessageOrder = $true,
    [bool]$X68RequireNoteEvents = $true,
    [string]$X68OracleRun68Exe = "tools/run68/run68-code/trunk/src/run68_oracle.exe",
    [string]$X68WorkRoot = "C:\x68tmp",
    [bool]$KeepX68WorkDirOnFailure = $false,
    [switch]$CompatCompare = $false,
    [string]$OutDir = "artifacts/ref_compare",
    [int]$Loops = 1,
    [switch]$NoLoopExtension = $true,
    [int]$MaxFiles = 0,
    [int]$StartFileIndex = 0,
    [int]$PerFileTimeoutSec = 10,
    [bool]$ExcludeZeroByteFiles = $true,
    [bool]$EnableComplexityGuard = $true,
    [int]$ComplexityGuardFcThreshold = 4000,
    [int]$ComplexityGuardMixedFcThreshold = 800,
    [int]$ComplexityGuardMixedInfiniteTrackThreshold = 20,
    [bool]$EnableX68AdaptiveTimeout = $true,
    [int]$X68AdaptiveTimeoutSourceEventThreshold = 8000,
    [int]$X68AdaptiveTimeoutSourceEventStep = 4000,
    [int]$X68AdaptiveTimeoutSourceEventStepSec = 15,
    [int]$X68AdaptiveTimeoutFcThreshold = 400,
    [int]$X68AdaptiveTimeoutFcStep = 400,
    [int]$X68AdaptiveTimeoutFcStepSec = 20,
    [int]$X68AdaptiveTimeoutInfiniteTrackStepSec = 5,
    [int]$X68AdaptiveTimeoutMaxExtraSec = 120,
    [bool]$RetryWithoutTimeoutOnTimeout = $false,
    [bool]$UseFallbackReferenceOnFailure = $true,
    [bool]$UseFallbackReferenceOnTimeout = $true,
    [bool]$TreatReferenceFailureAsSkip = $true,
    [bool]$TreatTimeoutAsSkip = $true,
    [bool]$MergeActiveSameNote = $true,
    [bool]$KeepBoundaryActiveSameNote = $false,
    [bool]$EmitEmptySysEx = $false,
    [bool]$UseRealtimeForInfiniteLoops = $true,
    [bool]$StopOnInfiniteTracksOnly = $true,
    [bool]$FreezeInfiniteTracksAfterFirstCycle = $false,
    [bool]$TreatHighLoopCountAsInfinite = $true,
    [bool]$BalanceInfiniteLoopTracks = $false,
    [long]$InfiniteLoopTailTicks = 0,
    [int]$InfiniteLoopTailMeasures = 0,
    [bool]$IgnorePendingOnAllTracksPlayedStop = $false,
    [bool]$EmitPendingNoteOffsAfterTrackEnd = $true,
    [bool]$TreatVelocityZeroNoteAsNoteOff = $true,
    [bool]$RcpcvCompatTerminateDrumSetupAtReverbComment = $false,
    [bool]$RcpcvCompatBarDelayBeforeVelocityZeroNote = $false,
    [bool]$RcpcvCompatLimitSysExPayloadTo255 = $false,
    [bool]$RcpcvCompatDropFollowingSysExAfterLongPayload = $false,
    [switch]$RcdStrictMode,
    [int]$RcpcvCompatMaxSysExPerTick = 0,
    [bool]$RcpcvCompatNormalizeMalformedSysEx = $false,
    [bool]$IgnoreSameTickOrdering = $false,
    [bool]$IgnoreCc7Differences = $false,
    [bool]$IgnoreDuplicateSysExSameTick = $false,
    [bool]$IgnoreInvalidShortMidiData = $false,
    [bool]$NormalizeEarlySysExTickGrid = $false,
    [int]$EarlySysExTickGrid = 8,
    [int]$EarlySysExTickLimit = 128,
    [bool]$IgnoreNearTickEqualShortEvents = $false,
    [int]$ShortEventTickJitter = 4,
    [int]$ShortEventTickJitterLimit = 256,
    [string[]]$IgnoreMismatchReasons = @(),
    [bool]$IgnoreTrailingNonMusicalEvents = $false,
    [bool]$IgnoreTrailingOurEventsAfterRefEnd = $false,
    [bool]$IgnoreTrailingRefEventsAfterOurEnd = $false,
    [bool]$IgnoreRcpcvcTailPhase = $false,
    [bool]$StopCompareWhenOurEventsOverReference = $true,
    [double]$OurEventsOverReferenceRatio = 2.0,
    [int]$DigestSkipEventThreshold = 300000,
    [int]$DigestMinRemainingTimeoutSec = 1,
    [bool]$IgnoreLikelyInaudibleMismatches = $false,
    [bool]$IgnoreInfiniteLoopTailFinalizeMismatch = $true,
    [bool]$IgnoreReferenceContinuationTailMismatch = $false,
    [bool]$IgnoreKnownStartupPrologue = $false,
    [bool]$IgnoreLeadingPreNoteSetup = $false,
    [bool]$IgnoreTicksInMessageOrderCompare = $false,
    [bool]$TreatRefPartialMismatchAsSkip = $false,
    [bool]$ClearRefPartialWhenCoverageSufficient = $true,
    [double]$RefPartialCoverageEventRatio = 1.0,
    [int]$DebugDumpCompareWindow = 0
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Stop-ProcessTreeById([int]$ProcessId) {
    if ($ProcessId -le 0) {
        return
    }

    try {
        $children = Get-CimInstance Win32_Process -Filter "ParentProcessId = $ProcessId" -ErrorAction SilentlyContinue
        foreach ($child in @($children)) {
            Stop-ProcessTreeById -ProcessId ([int]$child.ProcessId)
        }
    }
    catch {
    }

    try {
        $proc = Get-Process -Id $ProcessId -ErrorAction Stop
        try {
            $proc.Kill($true)
        }
        catch {
            Stop-Process -Id $ProcessId -Force -ErrorAction SilentlyContinue
        }
    }
    catch {
    }
}

function Stop-ReferenceProcesses(
    [string]$ExePath,
    [string]$WorkingDirectory = "")
{
    $exeName = [System.IO.Path]::GetFileName($ExePath)
    if ([string]::IsNullOrWhiteSpace($exeName)) {
        return
    }

    $resolvedExe = $null
    try {
        $resolvedExe = (Resolve-Path $ExePath -ErrorAction Stop).Path
    }
    catch {
    }

    $candidates = Get-CimInstance Win32_Process -ErrorAction SilentlyContinue | Where-Object {
        $_.Name -ieq $exeName
    }

    foreach ($p in @($candidates)) {
        $matches = $false

        if ($null -ne $resolvedExe -and -not [string]::IsNullOrWhiteSpace($p.ExecutablePath)) {
            if ($p.ExecutablePath -ieq $resolvedExe) {
                $matches = $true
            }
        }

        if (-not $matches -and -not [string]::IsNullOrWhiteSpace($WorkingDirectory)) {
            if (-not [string]::IsNullOrWhiteSpace($p.CommandLine) -and $p.CommandLine -like "*$WorkingDirectory*") {
                $matches = $true
            }
        }

        if ($matches) {
            Stop-ProcessTreeById -ProcessId ([int]$p.ProcessId)
        }
    }
}

function Convert-ToHex([byte[]]$bytes) {
    return [Convert]::ToHexString($bytes).ToLowerInvariant()
}

function Ensure-ReferenceExe([string]$exePath, [string]$sourcePath) {
    if (Test-Path $exePath) {
        return
    }

    if (-not (Test-Path $sourcePath)) {
        throw "Reference source not found: $sourcePath"
    }

    $exeDir = Split-Path -Parent $exePath
    if (-not (Test-Path $exeDir)) {
        New-Item -ItemType Directory -Path $exeDir -Force | Out-Null
    }

    $vsDevCmd = "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\Common7\Tools\VsDevCmd.bat"
    if (-not (Test-Path $vsDevCmd)) {
        throw "VsDevCmd.bat not found: $vsDevCmd"
    }

    $cmd = "`"$vsDevCmd`" -arch=x64 -host_arch=x64 >nul && cl /nologo /O2 /D_CRT_SECURE_NO_WARNINGS `"$sourcePath`" /Fe:`"$exePath`""
    cmd /c $cmd | Out-Null
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path $exePath)) {
        throw "Failed to build reference converter."
    }
}

function Ensure-CoreDll() {
    $coreDll = "src/RcpPlayer.Core/bin/Debug/net9.0/RcpPlayer.Core.dll"
    if (Test-Path $coreDll) {
        return $coreDll
    }

    $coreProj = "src/RcpPlayer.Core/RcpPlayer.Core.csproj"
    dotnet build $coreProj -c Debug | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to build RcpPlayer.Core."
    }

    if (-not (Test-Path $coreDll)) {
        throw "Core DLL not found after build: $coreDll"
    }
    return $coreDll
}

function Normalize-RcpcvSysExData([byte[]]$data) {
    if (-not $RcpcvCompatNormalizeMalformedSysEx -or $null -eq $data -or $data.Length -eq 0) {
        return $data
    }

    $normalized = $data
    $firstF7 = [Array]::IndexOf($normalized, [byte]0xF7)
    if ($firstF7 -ge 0 -and $firstF7 -lt ($normalized.Length - 1)) {
        $normalized = $normalized[0..$firstF7]
    }

    if ($normalized.Length -ge 6 -and
        $normalized[0] -eq 0xF0 -and
        $normalized[1] -eq 0x41 -and
        $normalized[4] -eq 0x12 -and
        $normalized[-2] -eq 0x00 -and
        $normalized[-1] -eq 0xF7) {
        $list = [System.Collections.Generic.List[byte]]::new()
        $list.AddRange($normalized)
        $list.RemoveAt($list.Count - 2)
        $normalized = $list.ToArray()
    }

    return $normalized
}

function Get-NormalizedEventString($event) {
    $ignoreTick = $IgnoreTicksInMessageOrderCompare -and
        $ReferenceKind -eq "x68rcpx" -and
        $X68CompareByMessageOrder

    $kind = $event.Packet.Kind
    if ($kind -eq [RcpPlayer.Core.Playback.MidiMessageKind]::Short) {
        $msg = [uint32]$event.Packet.ShortMessage
        if ($ignoreTick) {
            return "S|0|$msg"
        }

        return "S|$($event.Tick)|$msg"
    }

    $tick = [int]$event.Tick
    if (-not $ignoreTick -and $NormalizeEarlySysExTickGrid -and $tick -le $EarlySysExTickLimit -and $EarlySysExTickGrid -gt 1) {
        $tick = [int]([Math]::Round($tick / [double]$EarlySysExTickGrid, [System.MidpointRounding]::AwayFromZero) * $EarlySysExTickGrid)
    }

    $syx = Normalize-RcpcvSysExData $event.Packet.SysExData
    if ($null -eq $syx) {
        if ($ignoreTick) {
            return "X|0|"
        }

        return "X|$tick|"
    }

    if ($ignoreTick) {
        return "X|0|$(Convert-ToHex $syx)"
    }

    return "X|$tick|$(Convert-ToHex $syx)"
}

function Is-NonMusicalCompareEvent($event) {
    if ($event.Packet.Kind -eq [RcpPlayer.Core.Playback.MidiMessageKind]::SysEx) {
        return $true
    }

    if ($event.Packet.Kind -ne [RcpPlayer.Core.Playback.MidiMessageKind]::Short) {
        return $false
    }

    $msg = [uint32]$event.Packet.ShortMessage
    $status = $msg -band 0xFF
    $kind = $status -band 0xF0
    $data1 = ($msg -shr 8) -band 0xFF
    if ($kind -eq 0xB0 -and ($data1 -eq 7 -or $data1 -eq 11 -or $data1 -eq 10)) {
        return $true
    }

    return $false
}

function Is-SameSysExWithSmallTickJitter($a, $b) {
    if (-not $NormalizeEarlySysExTickGrid) {
        return $false
    }

    if ($a.Packet.Kind -ne [RcpPlayer.Core.Playback.MidiMessageKind]::SysEx -or
        $b.Packet.Kind -ne [RcpPlayer.Core.Playback.MidiMessageKind]::SysEx) {
        return $false
    }

    $aTick = [int]$a.Tick
    $bTick = [int]$b.Tick
    if ($aTick -gt $EarlySysExTickLimit -or $bTick -gt $EarlySysExTickLimit) {
        return $false
    }

    if ([Math]::Abs($aTick - $bTick) -gt $EarlySysExTickGrid) {
        return $false
    }

    $aData = Normalize-RcpcvSysExData $a.Packet.SysExData
    $bData = Normalize-RcpcvSysExData $b.Packet.SysExData
    if ($null -eq $aData -or $null -eq $bData) {
        return $false
    }

    if ($aData.Length -ne $bData.Length) {
        return $false
    }

    for ($k = 0; $k -lt $aData.Length; $k++) {
        if ($aData[$k] -ne $bData[$k]) {
            return $false
        }
    }

    return $true
}

function Is-SameShortWithSmallTickJitter($a, $b) {
    if (-not $IgnoreNearTickEqualShortEvents) {
        return $false
    }

    if ($a.Packet.Kind -ne [RcpPlayer.Core.Playback.MidiMessageKind]::Short -or
        $b.Packet.Kind -ne [RcpPlayer.Core.Playback.MidiMessageKind]::Short) {
        return $false
    }

    $aTick = [int]$a.Tick
    $bTick = [int]$b.Tick
    if ($aTick -gt $ShortEventTickJitterLimit -or $bTick -gt $ShortEventTickJitterLimit) {
        return $false
    }

    if ([Math]::Abs($aTick - $bTick) -gt $ShortEventTickJitter) {
        return $false
    }

    return [uint32]$a.Packet.ShortMessage -eq [uint32]$b.Packet.ShortMessage
}

function Should-IgnoreEventForCompare($event) {
    if ($event.Packet.Kind -ne [RcpPlayer.Core.Playback.MidiMessageKind]::Short) {
        return $false
    }

    $msg = [uint32]$event.Packet.ShortMessage
    $status = $msg -band 0xFF
    $data1 = ($msg -shr 8) -band 0xFF
    $data2 = ($msg -shr 16) -band 0xFF

    if ($IgnoreInvalidShortMidiData) {
        $kind = $status -band 0xF0
        if ($kind -eq 0xC0 -or $kind -eq 0xD0) {
            if ($data1 -gt 0x7F) {
                return $true
            }
        }
        elseif ($status -lt 0xF0) {
            if ($data1 -gt 0x7F -or $data2 -gt 0x7F) {
                return $true
            }
        }
    }

    if ($IgnoreCc7Differences) {
        return (($status -band 0xF0) -eq 0xB0) -and $data1 -eq 7
    }

    return $false
}

function Get-ComparableEvents($plan) {
    if (-not $IgnoreCc7Differences -and -not $IgnoreInvalidShortMidiData) {
        return @($plan.MidiEvents)
    }

    return @(
        $plan.MidiEvents | Where-Object { -not (Should-IgnoreEventForCompare $_) }
    )
}

function ByteArray-StartsWith(
    [byte[]]$Data,
    [byte[]]$Prefix)
{
    if ($null -eq $Data -or $null -eq $Prefix) {
        return $false
    }

    if ($Data.Length -lt $Prefix.Length) {
        return $false
    }

    for ($i = 0; $i -lt $Prefix.Length; $i++) {
        if ($Data[$i] -ne $Prefix[$i]) {
            return $false
        }
    }

    return $true
}

function Is-KnownStartupPrologueSysEx($event) {
    if ($event.Packet.Kind -ne [RcpPlayer.Core.Playback.MidiMessageKind]::SysEx) {
        return $false
    }

    $data = Normalize-RcpcvSysExData $event.Packet.SysExData
    if ($null -eq $data -or $data.Length -eq 0) {
        return $false
    }

    # Known startup prologue patterns seen across references/backends.
    $known = @(
        [byte[]](0xF0, 0x41, 0x10, 0x42, 0x12, 0x40, 0x00, 0x7F, 0x00, 0x41, 0xF7), # Roland GS reset
        [byte[]](0xF0, 0x41, 0x10, 0x16, 0x12, 0x7F, 0x00, 0x00, 0x01, 0x00, 0xF7), # x68 startup prologue
        [byte[]](0xF0, 0x43, 0x10, 0x4C, 0x00, 0x00, 0x7E, 0x00, 0xF7), # Yamaha XG System On
        [byte[]](0xF0, 0x7E, 0x7F, 0x09, 0x01, 0xF7) # GM System On
    )

    foreach ($sig in $known) {
        if (ByteArray-StartsWith $data $sig) {
            return $true
        }
    }

    return $false
}

function Get-CompareShortStatusNibble([uint32]$msg) {
    return (($msg -band 0xFF) -band 0xF0)
}

function Get-CompareShortData2([uint32]$msg) {
    return (($msg -shr 16) -band 0xFF)
}

function Find-StartupTrimIndex([System.Collections.IList]$events) {
    if ($null -eq $events -or $events.Count -eq 0) {
        return 0
    }

    $maxScanEvents = [Math]::Min($events.Count, 64)
    $sawKnownStartup = $false
    for ($i = 0; $i -lt $maxScanEvents; $i++) {
        $e = $events[$i]
        if ([int]$e.Tick -gt 512) {
            break
        }

        if (Is-KnownStartupPrologueSysEx $e) {
            $sawKnownStartup = $true
            break
        }
    }

    if (-not $sawKnownStartup) {
        return 0
    }

    for ($i = 0; $i -lt $events.Count; $i++) {
        $e = $events[$i]
        if ($e.Packet.Kind -ne [RcpPlayer.Core.Playback.MidiMessageKind]::Short) {
            continue
        }

        $msg = [uint32]$e.Packet.ShortMessage
        $statusNibble = Get-CompareShortStatusNibble $msg
        $data2 = Get-CompareShortData2 $msg
        if ($statusNibble -eq 0x90 -and $data2 -gt 0) {
            return $i
        }
    }

    return 0
}

function Trim-KnownStartupPrologue([System.Collections.IList]$events) {
    if ($null -eq $events -or $events.Count -eq 0) {
        return @()
    }

    $trimIndex = Find-StartupTrimIndex $events
    if ($trimIndex -le 0) {
        return @($events)
    }

    if ($trimIndex -ge $events.Count) {
        return @()
    }

    return @($events[$trimIndex..($events.Count - 1)])
}

function Find-FirstNoteOnIndex([System.Collections.IList]$events) {
    if ($null -eq $events -or $events.Count -eq 0) {
        return -1
    }

    for ($i = 0; $i -lt $events.Count; $i++) {
        $e = $events[$i]
        if ($e.Packet.Kind -ne [RcpPlayer.Core.Playback.MidiMessageKind]::Short) {
            continue
        }

        $msg = [uint32]$e.Packet.ShortMessage
        $statusNibble = Get-CompareShortStatusNibble $msg
        if ($statusNibble -ne 0x90) {
            continue
        }

        $data2 = Get-CompareShortData2 $msg
        if ($data2 -gt 0) {
            return $i
        }
    }

    return -1
}

function Trim-LeadingPreNoteSetup([System.Collections.IList]$events) {
    if ($null -eq $events -or $events.Count -eq 0) {
        return @()
    }

    $firstNoteOn = Find-FirstNoteOnIndex $events
    if ($firstNoteOn -le 0) {
        return @($events)
    }

    return @($events[$firstNoteOn..($events.Count - 1)])
}

function Reindex-EventsByOrder([System.Collections.IList]$events) {
    if ($null -eq $events -or $events.Count -eq 0) {
        return @()
    }

    $reindexed = New-Object System.Collections.Generic.List[object]
    for ($i = 0; $i -lt $events.Count; $i++) {
        $e = $events[$i]
        $reindexed.Add([pscustomobject]@{
            Tick = [long]$i
            Packet = $e.Packet
        }) | Out-Null
    }

    return $reindexed.ToArray()
}

function Get-RcpcvcTailStartTick($events) {
    if ($events.Count -eq 0) {
        return $null
    }

    $maxTick = [int]$events[$events.Count - 1].Tick
    if ($maxTick -le 0) {
        return $null
    }

    $cc7ByTick = @{}
    foreach ($e in $events) {
        if ($e.Packet.Kind -ne [RcpPlayer.Core.Playback.MidiMessageKind]::Short) {
            continue
        }

        $msg = [uint32]$e.Packet.ShortMessage
        $status = $msg -band 0xFF
        $data1 = ($msg -shr 8) -band 0xFF
        $data2 = ($msg -shr 16) -band 0xFF
        if (($status -band 0xF0) -ne 0xB0 -or $data1 -ne 7) {
            continue
        }

        $tick = [int]$e.Tick
        if (-not $cc7ByTick.ContainsKey($tick)) {
            $cc7ByTick[$tick] = New-Object System.Collections.Generic.List[int]
        }

        $cc7ByTick[$tick].Add($data2) | Out-Null
    }

    $ticks = @(
        $cc7ByTick.Keys |
            Where-Object { $_ -ge [int]($maxTick * 0.5) -and $cc7ByTick[$_].Count -ge 3 } |
            Sort-Object
    )
    if ($ticks.Count -lt 2) {
        return $null
    }

    for ($i = 0; $i -lt $ticks.Count - 1; $i++) {
        $t0 = [int]$ticks[$i]
        $t1 = [int]$ticks[$i + 1]
        if ($t1 - $t0 -gt 96) {
            continue
        }

        $avg0 = ($cc7ByTick[$t0] | Measure-Object -Average).Average
        $avg1 = ($cc7ByTick[$t1] | Measure-Object -Average).Average
        if ($avg1 -le $avg0 + 1.0) {
            return $t0
        }
    }

    return $null
}

function Get-AnomalousTailStartTick($events) {
    if ($events.Count -lt 2) {
        return $null
    }

    $maxTick = [int]$events[$events.Count - 1].Tick
    if ($maxTick -le 0) {
        return $null
    }

    for ($i = 1; $i -lt $events.Count; $i++) {
        $prev = [int]$events[$i - 1].Tick
        $curr = [int]$events[$i].Tick
        $gap = $curr - $prev
        if ($gap -lt 8192) {
            continue
        }

        if ($prev -lt [int]($maxTick * 0.25)) {
            continue
        }

        $remaining = $events.Count - $i
        if ($remaining -le 128) {
            return $curr
        }
    }

    return $null
}

function Get-PlanDigest($plan) {
    $hash = [System.Security.Cryptography.IncrementalHash]::CreateHash(
        [System.Security.Cryptography.HashAlgorithmName]::SHA256)
    foreach ($e in $plan.MidiEvents) {
        $line = (Get-NormalizedEventString $e) + "`n"
        $bytes = [System.Text.Encoding]::UTF8.GetBytes($line)
        $hash.AppendData($bytes)
    }
    return Convert-ToHex ($hash.GetHashAndReset())
}

function Get-LoopComplexity($song) {
    $fcCount = 0
    $infiniteLoopTracks = 0
    $sourceEventCount = 0
    foreach ($track in $song.Tracks) {
        $hasInfinite = $false
        foreach ($e in $track.Events) {
            $sourceEventCount++
            if ($e.CommandOrNote -eq 0xFC) {
                $fcCount++
            }
            elseif ($e.CommandOrNote -eq 0xF8 -and ($e.DelayTicks -eq 0 -or $e.DelayTicks -ge 0x7F)) {
                $hasInfinite = $true
            }
        }

        if ($hasInfinite) {
            $infiniteLoopTracks++
        }
    }

    return [pscustomobject]@{
        FcCount = $fcCount
        InfiniteLoopTracks = $infiniteLoopTracks
        SourceEventCount = $sourceEventCount
    }
}

function Get-X68AdaptiveTimeoutExtensionSec($complexity) {
    if (-not $EnableX68AdaptiveTimeout -or $null -eq $complexity) {
        return 0
    }

    $extraSec = 0

    if ($X68AdaptiveTimeoutSourceEventStepSec -gt 0 -and
        $X68AdaptiveTimeoutSourceEventStep -gt 0 -and
        $complexity.SourceEventCount -gt $X68AdaptiveTimeoutSourceEventThreshold) {
        $sourceSteps = [Math]::Ceiling(($complexity.SourceEventCount - $X68AdaptiveTimeoutSourceEventThreshold) / [double]$X68AdaptiveTimeoutSourceEventStep)
        $extraSec += [int]$sourceSteps * $X68AdaptiveTimeoutSourceEventStepSec
    }

    if ($X68AdaptiveTimeoutFcStepSec -gt 0 -and
        $X68AdaptiveTimeoutFcStep -gt 0 -and
        $complexity.FcCount -gt $X68AdaptiveTimeoutFcThreshold) {
        $fcSteps = [Math]::Ceiling(($complexity.FcCount - $X68AdaptiveTimeoutFcThreshold) / [double]$X68AdaptiveTimeoutFcStep)
        $extraSec += [int]$fcSteps * $X68AdaptiveTimeoutFcStepSec
    }

    if ($X68AdaptiveTimeoutInfiniteTrackStepSec -gt 0 -and $complexity.InfiniteLoopTracks -gt 0) {
        $extraSec += $complexity.InfiniteLoopTracks * $X68AdaptiveTimeoutInfiniteTrackStepSec
    }

    if ($X68AdaptiveTimeoutMaxExtraSec -gt 0) {
        $extraSec = [Math]::Min($extraSec, $X68AdaptiveTimeoutMaxExtraSec)
    }

    return [int]$extraSec
}

function Invoke-ReferenceConverter(
    [string]$ExePath,
    [string[]]$Arguments,
    [string]$ExpectedOutputPath,
    [int]$TimeoutSec,
    [bool]$RetryWithoutTimeoutOnTimeout,
    [string]$WorkingDirectory = "")
{
    $attempt = 0
    while ($true) {
        $useTimeout = $TimeoutSec -gt 0 -and $attempt -eq 0
        if ($useTimeout) {
            $psi = [System.Diagnostics.ProcessStartInfo]::new()
            $psi.FileName = $ExePath
            $psi.UseShellExecute = $false
            $psi.RedirectStandardOutput = $true
            $psi.RedirectStandardError = $true
            $psi.CreateNoWindow = $true
            if (-not [string]::IsNullOrWhiteSpace($WorkingDirectory)) {
                $psi.WorkingDirectory = $WorkingDirectory
            }
            foreach ($arg in $Arguments) {
                $psi.ArgumentList.Add($arg) | Out-Null
            }

            $proc = [System.Diagnostics.Process]::Start($psi)
            if (-not $proc.WaitForExit($TimeoutSec * 1000)) {
                Stop-ProcessTreeById -ProcessId $proc.Id
                Stop-ReferenceProcesses -ExePath $ExePath -WorkingDirectory $WorkingDirectory
                if ($RetryWithoutTimeoutOnTimeout) {
                    $attempt++
                    continue
                }

                return [pscustomobject]@{
                    Success = $false
                    TimedOut = $true
                    Error = "Reference converter exceeded timeout (${TimeoutSec}s)."
                    ProcessId = $proc.Id
                }
            }

            $outputStd = $proc.StandardOutput.ReadToEnd()
            $outputErr = $proc.StandardError.ReadToEnd()
            $output = @($outputStd, $outputErr) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
            if ($proc.ExitCode -ne 0 -or -not (Test-Path $ExpectedOutputPath)) {
                return [pscustomobject]@{
                    Success = $false
                    TimedOut = $false
                    Error = "Reference converter failed (code $($proc.ExitCode)): $($output -join ' | ')"
                    ProcessId = $proc.Id
                }
            }
        }
        else {
            if (-not [string]::IsNullOrWhiteSpace($WorkingDirectory)) {
                Push-Location $WorkingDirectory
                try {
                    $output = & $ExePath @Arguments 2>&1
                }
                finally {
                    Pop-Location
                }
            }
            else {
                $output = & $ExePath @Arguments 2>&1
            }
            if ($LASTEXITCODE -ne 0 -or -not (Test-Path $ExpectedOutputPath)) {
                return [pscustomobject]@{
                    Success = $false
                    TimedOut = $false
                    Error = "Reference converter failed (code $LASTEXITCODE): $($output -join ' | ')"
                    ProcessId = 0
                }
            }
        }

        return [pscustomobject]@{
            Success = $true
            TimedOut = $false
            Error = ""
            ProcessId = 0
        }
    }
}

function Resolve-X68ArgumentTokens(
    [string[]]$ArgumentTemplate,
    [string]$InputFileName,
    [string]$OutputFileName)
{
    $resolved = New-Object System.Collections.Generic.List[string]
    foreach ($arg in $ArgumentTemplate) {
        if ($null -eq $arg) {
            continue
        }

        $value = $arg.Replace("{input}", $InputFileName).Replace("{output}", $OutputFileName)
        $resolved.Add($value) | Out-Null
    }

    return @($resolved)
}

function Resolve-X68SupportFilePaths(
    [string]$InputPath,
    $Song)
{
    $resolved = New-Object System.Collections.Generic.List[string]
    if ($null -eq $Song) {
        return @($resolved)
    }

    $sourceDir = Split-Path -Parent $InputPath
    if ([string]::IsNullOrWhiteSpace($sourceDir) -or -not (Test-Path $sourceDir)) {
        return @($resolved)
    }

    $candidates = @(
        $Song.Cm6FileName,
        $Song.GsdAFileName,
        $Song.GsdBFileName
    ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }

    foreach ($name in $candidates) {
        $leaf = [System.IO.Path]::GetFileName($name)
        if ([string]::IsNullOrWhiteSpace($leaf)) {
            continue
        }

        $direct = Join-Path $sourceDir $leaf
        if (Test-Path $direct) {
            $resolved.Add((Resolve-Path $direct).Path) | Out-Null
            continue
        }

        $match = Get-ChildItem -Path $sourceDir -File | Where-Object {
            $_.Name.Equals($leaf, [System.StringComparison]::OrdinalIgnoreCase)
        } | Select-Object -First 1
        if ($null -ne $match) {
            $resolved.Add($match.FullName) | Out-Null
        }
    }

    return @($resolved | Select-Object -Unique)
}

function Get-MidiStatusDataLength([int]$status) {
    $status = $status -band 0xFF
    if ($status -lt 0x80) {
        return -1
    }

    if ($status -lt 0xF0) {
        $kind = $status -band 0xF0
        if ($kind -eq 0xC0 -or $kind -eq 0xD0) {
            return 1
        }
        return 2
    }

    switch ($status) {
        0xF0 { return -2 }
        0xF1 { return 1 }
        0xF2 { return 2 }
        0xF3 { return 1 }
        0xF6 { return 0 }
        0xF7 { return -3 }
        default {
            if ($status -ge 0xF8) {
                return 0
            }
            return 0
        }
    }
}

function New-CompareShortEvent([long]$Tick, [int]$Status, [int]$Data1, [int]$Data2) {
    $msg = [uint32](($Status -band 0xFF) -bor (($Data1 -band 0xFF) -shl 8) -bor (($Data2 -band 0xFF) -shl 16))
    $packet = [pscustomobject]@{
        Kind = [RcpPlayer.Core.Playback.MidiMessageKind]::Short
        ShortMessage = $msg
        SysExData = $null
    }
    return [pscustomobject]@{
        Tick = $Tick
        Packet = $packet
        SourceTrackId = -1
        SourceEventIndex = -1
        SourceCommand = [byte]0
    }
}

function New-CompareSysExEvent([long]$Tick, [byte[]]$Data) {
    if ($null -eq $Data) {
        $Data = [byte[]]@()
    }
    $packet = [pscustomobject]@{
        Kind = [RcpPlayer.Core.Playback.MidiMessageKind]::SysEx
        ShortMessage = [uint32]0
        SysExData = $Data
    }
    return [pscustomobject]@{
        Tick = $Tick
        Packet = $packet
        SourceTrackId = -1
        SourceEventIndex = -1
        SourceCommand = [byte]0
    }
}

function Convert-Run68MidiLogToPlan([string]$MidiLogPath) {
    if (-not (Test-Path $MidiLogPath)) {
        throw "run68 midi log not found: $MidiLogPath"
    }

    $rawBytes = New-Object System.Collections.Generic.List[byte]
    foreach ($line in [System.IO.File]::ReadLines($MidiLogPath)) {
        if ($line -match '^\s*\d+\s+([0-9A-Fa-f]{2})\s*$') {
            $rawBytes.Add([byte][Convert]::ToInt32($matches[1], 16)) | Out-Null
        }
    }

    if ($rawBytes.Count -eq 0) {
        throw "run68 midi log contains no bytes."
    }

    $events = New-Object System.Collections.Generic.List[object]
    $shortEventCount = 0
    $sysExEventCount = 0
    $noteEventCount = 0
    $bytes = $rawBytes.ToArray()
    $i = 0
    $tick = 0L
    $runningStatus = -1
    while ($i -lt $bytes.Length) {
        $b = [int]$bytes[$i]

        if ($b -ge 0xF8) {
            $events.Add((New-CompareShortEvent -Tick $tick -Status $b -Data1 0 -Data2 0)) | Out-Null
            $shortEventCount++
            $tick++
            $i++
            continue
        }

        if ($b -ge 0x80) {
            if ($b -eq 0xF0) {
                $syx = New-Object System.Collections.Generic.List[byte]
                $syx.Add([byte]0xF0) | Out-Null
                $i++
                while ($i -lt $bytes.Length) {
                    $d = [int]$bytes[$i]
                    if ($d -ge 0xF8) {
                        $events.Add((New-CompareShortEvent -Tick $tick -Status $d -Data1 0 -Data2 0)) | Out-Null
                        $shortEventCount++
                        $tick++
                        $i++
                        continue
                    }

                    $syx.Add([byte]$d) | Out-Null
                    $i++
                    if ($d -eq 0xF7) {
                        break
                    }
                }

                $events.Add((New-CompareSysExEvent -Tick $tick -Data $syx.ToArray())) | Out-Null
                $sysExEventCount++
                $tick++
                $runningStatus = -1
                continue
            }

            $status = $b
            $i++
            $len = Get-MidiStatusDataLength $status
            if ($status -lt 0xF0) {
                $runningStatus = $status
            }
            else {
                $runningStatus = -1
            }

            if ($len -lt 0) {
                continue
            }

            $data1 = 0
            $data2 = 0
            if ($len -ge 1 -and $i -lt $bytes.Length) {
                $next = [int]$bytes[$i]
                if ($next -lt 0x80 -or ($status -lt 0xF0 -and $IgnoreInvalidShortMidiData)) {
                    $data1 = $next
                    $i++
                }
            }
            if ($len -ge 2 -and $i -lt $bytes.Length) {
                $next = [int]$bytes[$i]
                if ($next -lt 0x80 -or ($status -lt 0xF0 -and $IgnoreInvalidShortMidiData)) {
                    $data2 = $next
                    $i++
                }
            }

            $events.Add((New-CompareShortEvent -Tick $tick -Status $status -Data1 $data1 -Data2 $data2)) | Out-Null
            $shortEventCount++
            if ((($status -band 0xF0) -eq 0x80) -or (($status -band 0xF0) -eq 0x90)) {
                $noteEventCount++
            }
            $tick++
            continue
        }

        if ($runningStatus -lt 0) {
            $i++
            continue
        }

        $len = Get-MidiStatusDataLength $runningStatus
        if ($len -le 0) {
            $i++
            continue
        }

        $data1 = $b
        $i++
        $data2 = 0
        if ($len -ge 2 -and $i -lt $bytes.Length) {
            $next = [int]$bytes[$i]
            if ($next -lt 0x80 -or ($runningStatus -lt 0xF0 -and $IgnoreInvalidShortMidiData)) {
                $data2 = $next
                $i++
            }
        }

        $events.Add((New-CompareShortEvent -Tick $tick -Status $runningStatus -Data1 $data1 -Data2 $data2)) | Out-Null
        $shortEventCount++
        if ((($runningStatus -band 0xF0) -eq 0x80) -or (($runningStatus -band 0xF0) -eq 0x90)) {
            $noteEventCount++
        }
        $tick++
    }

    $plan = [pscustomobject]@{
        TimeBase = 96
        InitialTempoBpm = 120.0
        TempoEvents = @()
        MidiEvents = $events.ToArray()
    }

    return [pscustomobject]@{
        Plan = $plan
        ByteCount = $rawBytes.Count
        EventCount = $events.Count
        ShortEventCount = $shortEventCount
        SysExEventCount = $sysExEventCount
        NoteEventCount = $noteEventCount
    }
}

function Convert-PlanToMessageOrderPlan($plan) {
    $events = New-Object System.Collections.Generic.List[object]
    $tick = 0L
    foreach ($evt in $plan.MidiEvents) {
        if ($evt.Packet.Kind -eq [RcpPlayer.Core.Playback.MidiMessageKind]::Short) {
            $msg = [uint32]$evt.Packet.ShortMessage
            $status = [int]($msg -band 0xFF)
            $data1 = [int](($msg -shr 8) -band 0xFF)
            $data2 = [int](($msg -shr 16) -band 0xFF)
            $events.Add((New-CompareShortEvent -Tick $tick -Status $status -Data1 $data1 -Data2 $data2)) | Out-Null
        }
        else {
            $sysExData = if ($null -ne $evt.Packet.SysExData) { [byte[]]$evt.Packet.SysExData } else { [byte[]]@() }
            $events.Add((New-CompareSysExEvent -Tick $tick -Data $sysExData)) | Out-Null
        }
        $tick++
    }

    return [pscustomobject]@{
        TimeBase = 96
        InitialTempoBpm = 120.0
        TempoEvents = @()
        MidiEvents = $events.ToArray()
    }
}

function Invoke-X68ReferenceConverter(
    [string]$Run68ExePath,
    [string]$ProgramPath,
    [string[]]$ProgramArgsTemplate,
    [string]$ChildProgramPath,
    [string]$ChildCommandTemplate,
    [string]$InputPath,
    [string[]]$SupportFilePaths,
    [string]$WorkRoot,
    [int]$TimeoutSec,
    [bool]$RetryWithoutTimeoutOnTimeout,
    [int]$RetryTimeoutSec,
    [bool]$KeepWorkDirOnFailure,
    [bool]$EnableMidiStub,
    [bool]$EnableIoLog,
    [bool]$CaptureRunLogs)
{
    if (-not (Test-Path $Run68ExePath)) {
        return [pscustomobject]@{
            Success = $false
            TimedOut = $false
            Error = "run68 executable not found: $Run68ExePath"
            ReferencePlan = $null
            ReferenceStats = $null
        }
    }

    if (-not (Test-Path $ProgramPath)) {
        return [pscustomobject]@{
            Success = $false
            TimedOut = $false
            Error = "X68000 program not found: $ProgramPath"
            ReferencePlan = $null
            ReferenceStats = $null
        }
    }

    if (-not (Test-Path $ChildProgramPath)) {
        return [pscustomobject]@{
            Success = $false
            TimedOut = $false
            Error = "X68000 child program not found: $ChildProgramPath"
            ReferencePlan = $null
            ReferenceStats = $null
        }
    }

    if (-not (Test-Path $WorkRoot)) {
        New-Item -ItemType Directory -Path $WorkRoot -Force | Out-Null
    }

    # Ensure run68 has enough memory and interrupt emulation for RCD playback.
    $run68IniPath = [System.IO.Path]::ChangeExtension($Run68ExePath, ".ini")
    Set-Content -LiteralPath $run68IniPath -Encoding ASCII -Value "[all]`r`nmainmemory=12`r`ntrapemulate`r`n"

    $jobId = [Guid]::NewGuid().ToString("N").Substring(0, 4)
    $workDir = Join-Path $WorkRoot ("j" + $jobId)
    New-Item -ItemType Directory -Path $workDir -Force | Out-Null

    $inputExt = [System.IO.Path]::GetExtension($InputPath)
    if ([string]::IsNullOrWhiteSpace($inputExt)) {
        $inputExt = ".RCP"
    }
    elseif ($inputExt.Length -gt 4) {
        $inputExt = ".RCP"
    }

    $stagedInputName = "INPUT$($inputExt.ToUpperInvariant())"
    $stagedRootProgramName = [System.IO.Path]::GetFileName($ProgramPath)
    $stagedRootProgramStem = [System.IO.Path]::GetFileNameWithoutExtension($stagedRootProgramName)
    $stagedChildProgramName = [System.IO.Path]::GetFileName($ChildProgramPath)

    $stagedInputPath = Join-Path $workDir $stagedInputName
    $stagedRootProgramPath = Join-Path $workDir $stagedRootProgramName
    $stagedChildProgramPath = Join-Path $workDir $stagedChildProgramName
    $stagedMidiLogPath = Join-Path $workDir "midi.log"
    $stagedIoLogPath = Join-Path $workDir "io.log"

    Copy-Item -LiteralPath $InputPath -Destination $stagedInputPath -Force
    Copy-Item -LiteralPath $ProgramPath -Destination $stagedRootProgramPath -Force
    Copy-Item -LiteralPath $ChildProgramPath -Destination $stagedChildProgramPath -Force
    foreach ($supportPath in @($SupportFilePaths | Where-Object { -not [string]::IsNullOrWhiteSpace($_) -and (Test-Path $_) })) {
        $supportName = [System.IO.Path]::GetFileName($supportPath)
        if ([string]::IsNullOrWhiteSpace($supportName)) {
            continue
        }

        Copy-Item -LiteralPath $supportPath -Destination (Join-Path $workDir $supportName) -Force
    }
    Remove-Item -LiteralPath $stagedMidiLogPath -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $stagedIoLogPath -Force -ErrorAction SilentlyContinue

    $run68Args = New-Object System.Collections.Generic.List[string]
    $run68Args.Add($stagedRootProgramStem) | Out-Null
    foreach ($arg in (Resolve-X68ArgumentTokens -ArgumentTemplate $ProgramArgsTemplate -InputFileName $stagedInputName -OutputFileName "")) {
        $run68Args.Add($arg) | Out-Null
    }

    $childCommand = $ChildCommandTemplate.Replace("{input}", $stagedInputName)
    $envKeys = @("RUN68_NO_ABORT_DEBUG", "RUN68_CHILD_CMDS", "RUN68_MIDI_STUB", "RUN68_MIDI_LOG", "RUN68_IO_LOG", "RUN68_ALLOW_UNKNOWN_FE")
    $savedEnv = @{}
    foreach ($key in $envKeys) {
        if (Test-Path ("Env:$key")) {
            $savedEnv[$key] = (Get-Item ("Env:$key")).Value
        }
        else {
            $savedEnv[$key] = $null
        }
    }

    $env:RUN68_NO_ABORT_DEBUG = "1"
    $env:RUN68_CHILD_CMDS = $childCommand
    $env:RUN68_ALLOW_UNKNOWN_FE = "1"
    if ($EnableMidiStub) {
        $env:RUN68_MIDI_STUB = "1"
        $env:RUN68_MIDI_LOG = $stagedMidiLogPath
        if ($EnableIoLog) {
            $env:RUN68_IO_LOG = $stagedIoLogPath
        }
    }
    elseif ($EnableIoLog) {
        $env:RUN68_IO_LOG = $stagedIoLogPath
    }

    $expectedOutputPath = if ($EnableMidiStub) { $stagedMidiLogPath } else { $stagedIoLogPath }
    try {
        $stdOutPath = Join-Path $workDir "run.out"
        $stdErrPath = Join-Path $workDir "run.err"
        $maxAttempts = if ($RetryWithoutTimeoutOnTimeout -and $TimeoutSec -gt 0) { 2 } else { 1 }
        $attempt = 0
        $converterResult = $null
        while ($attempt -lt $maxAttempts) {
            if ($attempt -gt 0) {
                # Timeout retry always restarts with a clean capture file.
                Remove-Item -LiteralPath $expectedOutputPath -Force -ErrorAction SilentlyContinue
            }

            if ($CaptureRunLogs) {
                Remove-Item -LiteralPath $stdOutPath -Force -ErrorAction SilentlyContinue
                Remove-Item -LiteralPath $stdErrPath -Force -ErrorAction SilentlyContinue
            }
            else {
                # Avoid most per-run disk churn unless diagnostics are requested.
                Remove-Item -LiteralPath $stdErrPath -Force -ErrorAction SilentlyContinue
                $stdOutPath = "NUL"
            }

            $attemptTimeout = 0
            if ($TimeoutSec -gt 0) {
                if ($attempt -eq 0) {
                    $attemptTimeout = $TimeoutSec
                }
                elseif ($RetryTimeoutSec -gt 0) {
                    $attemptTimeout = $RetryTimeoutSec
                }
                else {
                    $attemptTimeout = [Math]::Max($TimeoutSec * 2, 20)
                }
            }

            $proc = Start-Process `
                -FilePath $Run68ExePath `
                -ArgumentList @($run68Args) `
                -WorkingDirectory $workDir `
                -PassThru `
                -NoNewWindow `
                -RedirectStandardOutput $stdOutPath `
                -RedirectStandardError $stdErrPath

            $timedOut = $false
            if ($attemptTimeout -gt 0) {
                if (-not $proc.WaitForExit($attemptTimeout * 1000)) {
                    $timedOut = $true
                    Stop-ProcessTreeById -ProcessId $proc.Id
                    Stop-ReferenceProcesses -ExePath $Run68ExePath -WorkingDirectory $workDir
                }
            }
            else {
                $proc.WaitForExit()
            }

            if ($timedOut) {
                if (($attempt + 1) -lt $maxAttempts) {
                    $attempt++
                    continue
                }

                $partialAvailable = $false
                try {
                    if (Test-Path $expectedOutputPath) {
                        $partialLen = (Get-Item $expectedOutputPath).Length
                        if ($partialLen -gt 0) {
                            $partialAvailable = $true
                        }
                    }
                }
                catch {
                    $partialAvailable = $false
                }

                if ($partialAvailable) {
                    $converterResult = [pscustomobject]@{
                        Success = $true
                        TimedOut = $true
                        Error = "Reference converter exceeded timeout (${attemptTimeout}s); using partial captured output."
                        ProcessId = $proc.Id
                    }
                }
                else {
                    $converterResult = [pscustomobject]@{
                        Success = $false
                        TimedOut = $true
                        Error = "Reference converter exceeded timeout (${attemptTimeout}s)."
                        ProcessId = $proc.Id
                    }
                }
            }
            elseif ($proc.ExitCode -ne 0 -or -not (Test-Path $expectedOutputPath)) {
                $outHead = ""
                $errHead = ""
                if ($CaptureRunLogs -and (Test-Path $stdOutPath)) {
                    $outHead = ((Get-Content $stdOutPath -TotalCount 10) -join " | ")
                }
                if ($CaptureRunLogs -and (Test-Path $stdErrPath)) {
                    $errHead = ((Get-Content $stdErrPath -TotalCount 10) -join " | ")
                }
                if (-not $CaptureRunLogs -and [string]::IsNullOrWhiteSpace($outHead) -and [string]::IsNullOrWhiteSpace($errHead)) {
                    $errHead = "(run logs disabled; set -X68CaptureRunLogs:`$true for details)"
                }
                $converterResult = [pscustomobject]@{
                    Success = $false
                    TimedOut = $false
                    Error = "Reference converter failed (code $($proc.ExitCode)): $outHead $errHead"
                    ProcessId = $proc.Id
                }
            }
            else {
                $converterResult = [pscustomobject]@{
                    Success = $true
                    TimedOut = $false
                    Error = ""
                    ProcessId = $proc.Id
                }
            }

            break
        }

        if ($null -eq $converterResult) {
            $converterResult = [pscustomobject]@{
                Success = $false
                TimedOut = $false
                Error = "Reference converter did not produce a result."
                ProcessId = 0
            }
        }
    }
    finally {
        foreach ($key in $envKeys) {
            $saved = $savedEnv[$key]
            if ($null -eq $saved) {
                Remove-Item ("Env:$key") -ErrorAction SilentlyContinue
            }
            else {
                Set-Item ("Env:$key") $saved
            }
        }
    }

    if ($converterResult.TimedOut -and $converterResult.PSObject.Properties.Name -contains "ProcessId") {
        $timedOutPid = [int]$converterResult.ProcessId
        if ($timedOutPid -gt 0) {
            Stop-ProcessTreeById -ProcessId $timedOutPid
        }
        Stop-ReferenceProcesses -ExePath $Run68ExePath -WorkingDirectory $workDir
    }

    if ($converterResult.Success) {
        $referencePlan = $null
        try {
            $referencePlan = Convert-Run68MidiLogToPlan -MidiLogPath $stagedMidiLogPath
        }
        catch {
            $errorText = "run68 conversion succeeded but midi log parse failed: $($_.Exception.ToString())"
            if ($KeepWorkDirOnFailure) {
                $errorText = "$errorText | workDir=$workDir"
            }
            else {
                Remove-Item -LiteralPath $workDir -Recurse -Force -ErrorAction SilentlyContinue
            }
            return [pscustomobject]@{
                Success = $false
                TimedOut = $false
                Error = $errorText
                ReferencePlan = $null
                ReferenceStats = $null
            }
        }

        if (-not $KeepWorkDirOnFailure) {
            Remove-Item -LiteralPath $workDir -Recurse -Force -ErrorAction SilentlyContinue
        }
        return [pscustomobject]@{
            Success = $true
            TimedOut = [bool]$converterResult.TimedOut
            Error = [string]$converterResult.Error
            ReferencePlan = $referencePlan.Plan
            ReferenceStats = $referencePlan
        }
    }

    $errorText = $converterResult.Error
    if ($KeepWorkDirOnFailure) {
        $errorText = "$errorText | workDir=$workDir"
    }
    else {
        Remove-Item -LiteralPath $workDir -Recurse -Force -ErrorAction SilentlyContinue
    }

    return [pscustomobject]@{
        Success = $false
        TimedOut = $converterResult.TimedOut
        Error = $errorText
        ReferencePlan = $null
        ReferenceStats = $null
    }
}

function Get-RemainingTimeoutSec([datetime]$DeadlineUtc, [int]$ConfiguredTimeoutSec) {
    if ($ConfiguredTimeoutSec -le 0) {
        return 0
    }

    $remaining = [int][Math]::Floor(($DeadlineUtc - [datetime]::UtcNow).TotalSeconds)
    if ($remaining -lt 0) {
        return 0
    }

    return $remaining
}

function Assert-PerFileDeadline([datetime]$DeadlineUtc, [int]$ConfiguredTimeoutSec, [string]$Phase) {
    if ($ConfiguredTimeoutSec -le 0) {
        return
    }

    if ([datetime]::UtcNow -gt $DeadlineUtc) {
        throw [System.TimeoutException]::new("Per-file timeout (${ConfiguredTimeoutSec}s) exceeded during $Phase.")
    }
}

function Get-PreparedCompareEvents($plan) {
    $events = Get-ComparableEvents $plan

    if ($IgnoreKnownStartupPrologue) {
        $events = Trim-KnownStartupPrologue $events
    }

    if ($IgnoreLeadingPreNoteSetup) {
        $events = Trim-LeadingPreNoteSetup $events
    }

    if ($IgnoreTicksInMessageOrderCompare -and $ReferenceKind -eq "x68rcpx" -and $X68CompareByMessageOrder) {
        $events = Reindex-EventsByOrder $events
    }

    return ,@($events)
}

function Format-CompareEventLine([int]$index, $event) {
    if ($null -eq $event) {
        return "[{0}] <null>" -f $index
    }

    $normalized = Get-NormalizedEventString $event
    if ($event.Packet.Kind -eq [RcpPlayer.Core.Playback.MidiMessageKind]::SysEx) {
        $detail = "len=$($event.Packet.SysExData.Length)"
    }
    else {
        $msg = [uint32]$event.Packet.ShortMessage
        $status = [int]($msg -band 0xFF)
        $data1 = [int](($msg -shr 8) -band 0x7F)
        $data2 = [int](($msg -shr 16) -band 0x7F)
        $detail = ("st={0:X2} ch={1} d1={2} d2={3}" -f $status, (($status -band 0x0F) + 1), $data1, $data2)
    }

    return "[{0}] tick={1} {2} {3}" -f $index, $event.Tick, $normalized, $detail
}

function Write-CompareDebugWindow(
    [string]$outPath,
    [System.Collections.IList]$ourEvents,
    [System.Collections.IList]$refEvents,
    [int]$firstDiffIndex,
    [int]$radius)
{
    $lines = New-Object System.Collections.Generic.List[string]
    $lines.Add("firstDiffIndex=$firstDiffIndex") | Out-Null
    $lines.Add("") | Out-Null

    $start = [Math]::Max(0, $firstDiffIndex - $radius)
    $end = $firstDiffIndex + $radius

    $lines.Add("-- OUR --") | Out-Null
    for ($i = $start; $i -le [Math]::Min($end, $ourEvents.Count - 1); $i++) {
        $lines.Add((Format-CompareEventLine -index $i -event $ourEvents[$i])) | Out-Null
    }

    $lines.Add("") | Out-Null
    $lines.Add("-- REF --") | Out-Null
    for ($i = $start; $i -le [Math]::Min($end, $refEvents.Count - 1); $i++) {
        $lines.Add((Format-CompareEventLine -index $i -event $refEvents[$i])) | Out-Null
    }

    Set-Content -LiteralPath $outPath -Encoding UTF8 -Value $lines
}

function Compare-Plans($ourPlan, $refPlan) {
    $tailStart = $null
    if ($IgnoreRcpcvcTailPhase) {
        $tailStart = Get-RcpcvcTailStartTick @($refPlan.MidiEvents)
        $anomalousTailStart = Get-AnomalousTailStartTick @($refPlan.MidiEvents)
        if ($null -ne $anomalousTailStart) {
            if ($null -eq $tailStart -or $anomalousTailStart -lt $tailStart) {
                $tailStart = $anomalousTailStart
            }
        }
    }

    $ourEvents = Get-ComparableEvents $ourPlan
    $refEvents = Get-ComparableEvents $refPlan

    if ($IgnoreKnownStartupPrologue) {
        $ourEvents = Trim-KnownStartupPrologue $ourEvents
        $refEvents = Trim-KnownStartupPrologue $refEvents
    }

    if ($IgnoreLeadingPreNoteSetup) {
        $ourEvents = Trim-LeadingPreNoteSetup $ourEvents
        $refEvents = Trim-LeadingPreNoteSetup $refEvents
    }

    if ($null -ne $tailStart) {
        $ourEvents = @($ourEvents | Where-Object { $_.Tick -lt $tailStart })
        $refEvents = @($refEvents | Where-Object { $_.Tick -lt $tailStart })
    }

    if ($IgnoreRcpcvcTailPhase -and $ourEvents.Count -gt 0 -and $refEvents.Count -gt 0) {
        $ourMaxTick = [int]$ourEvents[$ourEvents.Count - 1].Tick
        $refMaxTick = [int]$refEvents[$refEvents.Count - 1].Tick
        if ($ourMaxTick -gt 0 -and $refMaxTick -gt $ourMaxTick * 4) {
            $cut = $ourMaxTick + 2048
            $refEvents = @($refEvents | Where-Object { $_.Tick -le $cut })
        }
        elseif ($refMaxTick -gt 0 -and $ourMaxTick -gt $refMaxTick * 4) {
            $cut = $refMaxTick + 2048
            $ourEvents = @($ourEvents | Where-Object { $_.Tick -le $cut })
        }
    }

    if ($IgnoreTicksInMessageOrderCompare -and $ReferenceKind -eq "x68rcpx" -and $X68CompareByMessageOrder) {
        $ourEvents = Reindex-EventsByOrder $ourEvents
        $refEvents = Reindex-EventsByOrder $refEvents
    }

    $firstDiffIndex = -1
    $ourDiff = ""
    $refDiff = ""

    function Get-TickBlock([System.Collections.IList]$events, [int]$startIndex) {
        $tick = $events[$startIndex].Tick
        $counts = @{}
        $i = $startIndex
        while ($i -lt $events.Count -and $events[$i].Tick -eq $tick) {
            $key = Get-NormalizedEventString $events[$i]
            if ($counts.ContainsKey($key)) {
                $counts[$key]++
            }
            else {
                $counts[$key] = 1
            }

            $i++
        }

        return [pscustomobject]@{
            Tick = $tick
            EndIndex = $i
            Counts = $counts
        }
    }

    function Compare-Counts([hashtable]$a, [hashtable]$b) {
        if ($a.Count -ne $b.Count) {
            return $false
        }

        foreach ($k in $a.Keys) {
            if (-not $b.ContainsKey($k) -or [int]$a[$k] -ne [int]$b[$k]) {
                return $false
            }
        }

        return $true
    }

    function Try-GetShortEventInfo($event) {
        if ($null -eq $event -or $event.Packet.Kind -ne [RcpPlayer.Core.Playback.MidiMessageKind]::Short) {
            return $null
        }

        $msg = [uint32]$event.Packet.ShortMessage
        $status = [int]($msg -band 0xFF)
        $data1 = [int](($msg -shr 8) -band 0x7F)
        $data2 = [int](($msg -shr 16) -band 0x7F)
        return [pscustomobject]@{
            Status = $status
            Data1 = $data1
            Data2 = $data2
            Kind = ($status -band 0xF0)
            Channel = ($status -band 0x0F)
        }
    }

    function Is-NoteOffEquivalentEvent($event) {
        $s = Try-GetShortEventInfo $event
        if ($null -eq $s) {
            return $false
        }

        return ($s.Kind -eq 0x80) -or (($s.Kind -eq 0x90) -and $s.Data2 -eq 0)
    }

    function Collapse-ConsecutiveDuplicateNoteOffs([System.Collections.IList]$events) {
        if ($null -eq $events -or $events.Count -le 1) {
            return @($events)
        }

        $collapsed = New-Object System.Collections.Generic.List[object]
        $collapsed.Add($events[0]) | Out-Null
        for ($idx = 1; $idx -lt $events.Count; $idx++) {
            $curr = $events[$idx]
            $prev = $collapsed[$collapsed.Count - 1]
            if (Is-NoteOffEquivalentEvent $curr -and Is-NoteOffEquivalentEvent $prev) {
                $c = Try-GetShortEventInfo $curr
                $p = Try-GetShortEventInfo $prev
                if ($null -ne $c -and $null -ne $p -and
                    $c.Channel -eq $p.Channel -and
                    $c.Data1 -eq $p.Data1 -and
                    $c.Data2 -eq $p.Data2) {
                    continue
                }
            }

            $collapsed.Add($curr) | Out-Null
        }

        return $collapsed.ToArray()
    }

    function Is-AdjacentSwappedNoteOffPair(
        [System.Collections.IList]$our,
        [int]$ourIndex,
        [System.Collections.IList]$ref,
        [int]$refIndex)
    {
        if ($ourIndex + 1 -ge $our.Count -or $refIndex + 1 -ge $ref.Count) {
            return $false
        }

        $o0 = $our[$ourIndex]
        $o1 = $our[$ourIndex + 1]
        $r0 = $ref[$refIndex]
        $r1 = $ref[$refIndex + 1]

        if (-not (Is-NoteOffEquivalentEvent $o0) -or
            -not (Is-NoteOffEquivalentEvent $o1) -or
            -not (Is-NoteOffEquivalentEvent $r0) -or
            -not (Is-NoteOffEquivalentEvent $r1)) {
            return $false
        }

        $o0s = Try-GetShortEventInfo $o0
        $o1s = Try-GetShortEventInfo $o1
        $r0s = Try-GetShortEventInfo $r0
        $r1s = Try-GetShortEventInfo $r1
        if ($null -eq $o0s -or $null -eq $o1s -or $null -eq $r0s -or $null -eq $r1s) {
            return $false
        }

        # Keep this narrow: only same-channel two-note swaps.
        if ($o0s.Channel -ne $o1s.Channel -or $r0s.Channel -ne $r1s.Channel -or $o0s.Channel -ne $r0s.Channel) {
            return $false
        }

        $o0n = Get-NormalizedEventString $o0
        $o1n = Get-NormalizedEventString $o1
        $r0n = Get-NormalizedEventString $r0
        $r1n = Get-NormalizedEventString $r1
        return ($o0n -eq $r1n -and $o1n -eq $r0n)
    }

    function Is-ShortNoteOffLikeForCompare($event) {
        $s = Try-GetShortEventInfo $event
        if ($null -eq $s) {
            return $false
        }

        return ($s.Kind -eq 0x80) -or (($s.Kind -eq 0x90) -and $s.Data2 -eq 0)
    }

    function Has-RecentMatchingNoteOffLike(
        [System.Collections.IList]$events,
        [int]$index,
        [int]$lookback = 6)
    {
        if ($index -le 0 -or -not (Is-ShortNoteOffLikeForCompare $events[$index])) {
            return $false
        }

        $target = Get-NormalizedEventString $events[$index]
        $start = [Math]::Max(0, $index - $lookback)
        for ($k = $index - 1; $k -ge $start; $k--) {
            if (-not (Is-ShortNoteOffLikeForCompare $events[$k])) {
                continue
            }

            if ((Get-NormalizedEventString $events[$k]) -eq $target) {
                return $true
            }
        }

        return $false
    }

    function Can-SkipImmediateDuplicateNoteOffCleanup(
        [System.Collections.IList]$primary,
        [int]$primaryIndex,
        [System.Collections.IList]$other,
        [int]$otherIndex)
    {
        if ($primaryIndex -lt 0 -or $otherIndex -lt 0) {
            return $false
        }

        if ($primaryIndex + 1 -ge $primary.Count -or $otherIndex -ge $other.Count) {
            return $false
        }

        if (-not (Is-ShortNoteOffLikeForCompare $primary[$primaryIndex])) {
            return $false
        }

        if (-not (Has-RecentMatchingNoteOffLike $primary $primaryIndex 8)) {
            return $false
        }

        return (Get-NormalizedEventString $primary[$primaryIndex + 1]) -eq (Get-NormalizedEventString $other[$otherIndex])
    }

    function Get-ContiguousSameChannelNoteOffRunLength(
        [System.Collections.IList]$events,
        [int]$startIndex,
        [int]$channel)
    {
        $count = 0
        $maxRun = 16
        for ($k = $startIndex; $k -lt $events.Count -and $count -lt $maxRun; $k++) {
            $s = Try-GetShortEventInfo $events[$k]
            if ($null -eq $s) {
                break
            }

            $isOff = ($s.Kind -eq 0x80) -or (($s.Kind -eq 0x90) -and $s.Data2 -eq 0)
            if (-not $isOff -or $s.Channel -ne $channel) {
                break
            }

            $count++
        }

        return $count
    }

    function Get-ContiguousAnyChannelNoteOffRunLength(
        [System.Collections.IList]$events,
        [int]$startIndex)
    {
        $count = 0
        $maxRun = 16
        for ($k = $startIndex; $k -lt $events.Count -and $count -lt $maxRun; $k++) {
            $s = Try-GetShortEventInfo $events[$k]
            if ($null -eq $s) {
                break
            }

            $isOff = ($s.Kind -eq 0x80) -or (($s.Kind -eq 0x90) -and $s.Data2 -eq 0)
            if (-not $isOff) {
                break
            }

            $count++
        }

        return $count
    }

    function Get-ComparableRunMultiset([System.Collections.IList]$events, [int]$startIndex, [int]$count) {
        $map = @{}
        for ($k = 0; $k -lt $count; $k++) {
            $key = Get-NormalizedEventString $events[$startIndex + $k]
            if ($map.ContainsKey($key)) {
                $map[$key]++
            }
            else {
                $map[$key] = 1
            }
        }

        return $map
    }

    function Try-GetPermutedNoteOffRunConsumeLength(
        [System.Collections.IList]$our,
        [int]$ourIndex,
        [System.Collections.IList]$ref,
        [int]$refIndex)
    {
        $o = Try-GetShortEventInfo $our[$ourIndex]
        $r = Try-GetShortEventInfo $ref[$refIndex]
        if ($null -eq $o -or $null -eq $r) {
            return 0
        }

        $oIsOff = ($o.Kind -eq 0x80) -or (($o.Kind -eq 0x90) -and $o.Data2 -eq 0)
        $rIsOff = ($r.Kind -eq 0x80) -or (($r.Kind -eq 0x90) -and $r.Data2 -eq 0)
        if (-not $oIsOff -or -not $rIsOff -or $o.Channel -ne $r.Channel) {
            return 0
        }

        $ourRun = Get-ContiguousSameChannelNoteOffRunLength $our $ourIndex $o.Channel
        $refRun = Get-ContiguousSameChannelNoteOffRunLength $ref $refIndex $r.Channel
        $consume = [Math]::Min($ourRun, $refRun)
        if ($consume -lt 2) {
            return 0
        }

        $ourSet = Get-ComparableRunMultiset $our $ourIndex $consume
        $refSet = Get-ComparableRunMultiset $ref $refIndex $consume
        if (Compare-Counts $ourSet $refSet) {
            return $consume
        }

        return 0
    }

    function Try-GetPermutedAnyChannelNoteOffRunConsumeLength(
        [System.Collections.IList]$our,
        [int]$ourIndex,
        [System.Collections.IList]$ref,
        [int]$refIndex)
    {
        $o = Try-GetShortEventInfo $our[$ourIndex]
        $r = Try-GetShortEventInfo $ref[$refIndex]
        if ($null -eq $o -or $null -eq $r) {
            return 0
        }

        $oIsOff = ($o.Kind -eq 0x80) -or (($o.Kind -eq 0x90) -and $o.Data2 -eq 0)
        $rIsOff = ($r.Kind -eq 0x80) -or (($r.Kind -eq 0x90) -and $r.Data2 -eq 0)
        if (-not $oIsOff -or -not $rIsOff) {
            return 0
        }

        $ourRun = Get-ContiguousAnyChannelNoteOffRunLength $our $ourIndex
        $refRun = Get-ContiguousAnyChannelNoteOffRunLength $ref $refIndex
        $consume = [Math]::Min($ourRun, $refRun)
        if ($consume -lt 2) {
            return 0
        }

        $ourSet = Get-ComparableRunMultiset $our $ourIndex $consume
        $refSet = Get-ComparableRunMultiset $ref $refIndex $consume
        if (Compare-Counts $ourSet $refSet) {
            return $consume
        }

        return 0
    }

    function Are-ShortEventsOnly(
        [System.Collections.IList]$events,
        [int]$startIndex,
        [int]$count)
    {
        for ($k = 0; $k -lt $count; $k++) {
            $idx = $startIndex + $k
            if ($idx -lt 0 -or $idx -ge $events.Count) {
                return $false
            }

            if ($events[$idx].Packet.Kind -ne [RcpPlayer.Core.Playback.MidiMessageKind]::Short) {
                return $false
            }
        }

        return $true
    }

    function Contains-NoteEvent(
        [System.Collections.IList]$events,
        [int]$startIndex,
        [int]$count)
    {
        for ($k = 0; $k -lt $count; $k++) {
            $idx = $startIndex + $k
            if ($idx -lt 0 -or $idx -ge $events.Count) {
                return $false
            }

            $s = Try-GetShortEventInfo $events[$idx]
            if ($null -eq $s) {
                continue
            }

            if ($s.Kind -eq 0x80 -or $s.Kind -eq 0x90) {
                return $true
            }
        }

        return $false
    }

    function Try-GetPermutedShortRunConsumeLength(
        [System.Collections.IList]$our,
        [int]$ourIndex,
        [System.Collections.IList]$ref,
        [int]$refIndex)
    {
        $maxRun = 96
        $maxOur = $our.Count - $ourIndex
        $maxRef = $ref.Count - $refIndex
        $maxCandidate = [Math]::Min($maxRun, [Math]::Min($maxOur, $maxRef))
        if ($maxCandidate -lt 3) {
            return 0
        }

        for ($len = 3; $len -le $maxCandidate; $len++) {
            if (-not (Are-ShortEventsOnly $our $ourIndex $len) -or
                -not (Are-ShortEventsOnly $ref $refIndex $len)) {
                continue
            }

            # Keep this narrow to avoid masking setup-order differences:
            # only tolerate local note-containing short-event permutations.
            if (-not (Contains-NoteEvent $our $ourIndex $len) -and
                -not (Contains-NoteEvent $ref $refIndex $len)) {
                continue
            }

            $ourSet = Get-ComparableRunMultiset $our $ourIndex $len
            $refSet = Get-ComparableRunMultiset $ref $refIndex $len
            if (Compare-Counts $ourSet $refSet) {
                return $len
            }
        }

        return 0
    }

    function Try-GetSmallTickClusterShortRunConsumeLength(
        [System.Collections.IList]$our,
        [int]$ourIndex,
        [System.Collections.IList]$ref,
        [int]$refIndex)
    {
        function Get-ClusterLength([System.Collections.IList]$events, [int]$startIndex, [int]$maxTickSpan, [int]$maxEvents) {
            if ($startIndex -lt 0 -or $startIndex -ge $events.Count) {
                return 0
            }

            $first = $events[$startIndex]
            if ($first.Packet.Kind -ne [RcpPlayer.Core.Playback.MidiMessageKind]::Short) {
                return 0
            }

            $startTick = [int]$first.Tick
            $count = 0
            for ($k = $startIndex; $k -lt $events.Count -and $count -lt $maxEvents; $k++) {
                $e = $events[$k]
                if ($e.Packet.Kind -ne [RcpPlayer.Core.Playback.MidiMessageKind]::Short) {
                    break
                }

                if (([int]$e.Tick - $startTick) -gt $maxTickSpan) {
                    break
                }

                $count++
            }

            return $count
        }

        $maxTickSpan = 2
        $maxEvents = 32
        $ourLen = Get-ClusterLength $our $ourIndex $maxTickSpan $maxEvents
        $refLen = Get-ClusterLength $ref $refIndex $maxTickSpan $maxEvents
        if ($ourLen -lt 2 -or $refLen -lt 2 -or $ourLen -ne $refLen) {
            return 0
        }

        if (-not (Contains-NoteEvent $our $ourIndex $ourLen) -and
            -not (Contains-NoteEvent $ref $refIndex $refLen)) {
            return 0
        }

        $ourSet = Get-ComparableRunMultiset $our $ourIndex $ourLen
        $refSet = Get-ComparableRunMultiset $ref $refIndex $refLen
        if (Compare-Counts $ourSet $refSet) {
            return $ourLen
        }

        return 0
    }

    function Is-ExpressiveOrderFlexibleShortEvent($event) {
        $s = Try-GetShortEventInfo $event
        if ($null -eq $s) {
            return $false
        }

        if ($s.Kind -eq 0xE0) {
            return $true
        }

        if ($s.Kind -eq 0xB0 -and $s.Data1 -eq 11) {
            return $true
        }

        return $false
    }

    function Try-GetPermutedExpressiveRunConsumeLength(
        [System.Collections.IList]$our,
        [int]$ourIndex,
        [System.Collections.IList]$ref,
        [int]$refIndex)
    {
        $maxRun = 16
        $maxOur = $our.Count - $ourIndex
        $maxRef = $ref.Count - $refIndex
        $maxCandidate = [Math]::Min($maxRun, [Math]::Min($maxOur, $maxRef))
        if ($maxCandidate -lt 2) {
            return 0
        }

        for ($len = 2; $len -le $maxCandidate; $len++) {
            $ok = $true
            for ($k = 0; $k -lt $len; $k++) {
                if (-not (Is-ExpressiveOrderFlexibleShortEvent $our[$ourIndex + $k]) -or
                    -not (Is-ExpressiveOrderFlexibleShortEvent $ref[$refIndex + $k])) {
                    $ok = $false
                    break
                }
            }

            if (-not $ok) {
                continue
            }

            $ourSet = Get-ComparableRunMultiset $our $ourIndex $len
            $refSet = Get-ComparableRunMultiset $ref $refIndex $len
            if (Compare-Counts $ourSet $refSet) {
                return $len
            }
        }

        return 0
    }

    function Is-MixedPermutationFlexibleEvent($event) {
        if ($null -eq $event) {
            return $false
        }

        if ($event.Packet.Kind -eq [RcpPlayer.Core.Playback.MidiMessageKind]::SysEx) {
            return $true
        }

        $s = Try-GetShortEventInfo $event
        if ($null -eq $s) {
            return $false
        }

        if ($s.Kind -eq 0x80 -or $s.Kind -eq 0x90 -or $s.Kind -eq 0xE0) {
            return $true
        }

        if ($s.Kind -eq 0xB0 -and ($s.Data1 -in 0, 1, 7, 10, 11, 64, 91, 98, 99)) {
            return $true
        }

        return $false
    }

    function Try-GetPermutedMixedRunConsumeLength(
        [System.Collections.IList]$our,
        [int]$ourIndex,
        [System.Collections.IList]$ref,
        [int]$refIndex)
    {
        $maxRun = 6
        $maxOur = $our.Count - $ourIndex
        $maxRef = $ref.Count - $refIndex
        $maxCandidate = [Math]::Min($maxRun, [Math]::Min($maxOur, $maxRef))
        if ($maxCandidate -lt 2) {
            return 0
        }

        for ($len = 2; $len -le $maxCandidate; $len++) {
            $hasFlexible = $false
            $ok = $true
            for ($k = 0; $k -lt $len; $k++) {
                $oe = $our[$ourIndex + $k]
                $re = $ref[$refIndex + $k]
                if (-not (Is-MixedPermutationFlexibleEvent $oe) -or
                    -not (Is-MixedPermutationFlexibleEvent $re)) {
                    $ok = $false
                    break
                }

                if ($oe.Packet.Kind -eq [RcpPlayer.Core.Playback.MidiMessageKind]::SysEx -or
                    $re.Packet.Kind -eq [RcpPlayer.Core.Playback.MidiMessageKind]::SysEx) {
                    $hasFlexible = $true
                }
            }

            if (-not $ok -or -not $hasFlexible) {
                continue
            }

            $ourSet = Get-ComparableRunMultiset $our $ourIndex $len
            $refSet = Get-ComparableRunMultiset $ref $refIndex $len
            if (Compare-Counts $ourSet $refSet) {
                return $len
            }
        }

        return 0
    }

    function Try-ParseRolandDt1Writes($event) {
        if ($null -eq $event -or $event.Packet.Kind -ne [RcpPlayer.Core.Playback.MidiMessageKind]::SysEx) {
            return $null
        }

        $syx = $event.Packet.SysExData
        if ($null -eq $syx -or $syx.Length -lt 11) {
            return $null
        }

        # Roland DT1: F0 41 <dev> <model> 12 <addr3> <data...> <checksum> F7
        if ($syx[0] -ne 0xF0 -or $syx[1] -ne 0x41 -or $syx[4] -ne 0x12 -or $syx[$syx.Length - 1] -ne 0xF7) {
            return $null
        }

        $dataStart = 8
        $dataEnd = $syx.Length - 3
        if ($dataEnd -lt $dataStart) {
            return $null
        }

        $addr = ([int]$syx[5] -shl 16) -bor ([int]$syx[6] -shl 8) -bor [int]$syx[7]
        $writes = New-Object System.Collections.Generic.List[string]
        for ($idx = $dataStart; $idx -le $dataEnd; $idx++) {
            $writes.Add(("{0:X6}:{1:X2}" -f $addr, [int]$syx[$idx])) | Out-Null
            $addr++
        }

        return $writes.ToArray()
    }

    function Get-ConsecutiveRolandDt1WriteRuns(
        [System.Collections.IList]$events,
        [int]$startIndex,
        [int]$maxEvents)
    {
        $runs = New-Object System.Collections.Generic.List[object]
        $acc = New-Object System.Collections.Generic.List[string]
        $maxIdx = [Math]::Min($events.Count - 1, $startIndex + $maxEvents - 1)
        for ($idx = $startIndex; $idx -le $maxIdx; $idx++) {
            $writes = Try-ParseRolandDt1Writes $events[$idx]
            if ($null -eq $writes) {
                break
            }

            foreach ($w in $writes) {
                $acc.Add($w) | Out-Null
            }

            $runs.Add([pscustomobject]@{
                EventCount = ($idx - $startIndex + 1)
                Writes = $acc.ToArray()
            }) | Out-Null
        }

        return ,$runs.ToArray()
    }

    function Are-WriteSequencesEqual([string[]]$a, [string[]]$b) {
        if ($null -eq $a -or $null -eq $b) {
            return $false
        }

        if ($a.Length -ne $b.Length) {
            return $false
        }

        for ($k = 0; $k -lt $a.Length; $k++) {
            if ($a[$k] -ne $b[$k]) {
                return $false
            }
        }

        return $true
    }

    function Try-GetEquivalentRolandDt1RunConsume(
        [System.Collections.IList]$our,
        [int]$ourIndex,
        [System.Collections.IList]$ref,
        [int]$refIndex)
    {
        $maxEvents = 4
        $ourRuns = Get-ConsecutiveRolandDt1WriteRuns $our $ourIndex $maxEvents
        $refRuns = Get-ConsecutiveRolandDt1WriteRuns $ref $refIndex $maxEvents
        if ($ourRuns.Count -eq 0 -or $refRuns.Count -eq 0) {
            return $null
        }

        $best = $null
        foreach ($o in $ourRuns) {
            foreach ($r in $refRuns) {
                if (-not (Are-WriteSequencesEqual $o.Writes $r.Writes)) {
                    continue
                }

                # Skip trivial 1:1 identical packet case; direct compare handles it.
                if ($o.EventCount -eq 1 -and $r.EventCount -eq 1) {
                    continue
                }

                $score = $o.EventCount + $r.EventCount
                if ($null -eq $best -or $score -lt $best.Score) {
                    $best = [pscustomobject]@{
                        OurConsume = [int]$o.EventCount
                        RefConsume = [int]$r.EventCount
                        Score = $score
                    }
                }
            }
        }

        return $best
    }

    if ($ReferenceKind -eq "x68rcpx" -and $X68CompareByMessageOrder) {
        $ourEvents = Collapse-ConsecutiveDuplicateNoteOffs $ourEvents
        $refEvents = Collapse-ConsecutiveDuplicateNoteOffs $refEvents
    }

    $i = 0
    $j = 0
    while ($i -lt $ourEvents.Count -and $j -lt $refEvents.Count) {
        $o = Get-NormalizedEventString $ourEvents[$i]
        $r = Get-NormalizedEventString $refEvents[$j]
        if ($o -eq $r) {
            $i++
            $j++
            continue
        }

        if (Is-SameSysExWithSmallTickJitter $ourEvents[$i] $refEvents[$j]) {
            $i++
            $j++
            continue
        }
        if (Is-SameShortWithSmallTickJitter $ourEvents[$i] $refEvents[$j]) {
            $i++
            $j++
            continue
        }

        if ($ReferenceKind -eq "x68rcpx" -and
            $X68CompareByMessageOrder -and
            (Is-AdjacentSwappedNoteOffPair $ourEvents $i $refEvents $j)) {
            $i += 2
            $j += 2
            continue
        }

        if ($ReferenceKind -eq "x68rcpx" -and $X68CompareByMessageOrder) {
            if (Can-SkipImmediateDuplicateNoteOffCleanup $ourEvents $i $refEvents $j) {
                $i++
                continue
            }

            if (Can-SkipImmediateDuplicateNoteOffCleanup $refEvents $j $ourEvents $i) {
                $j++
                continue
            }

            $consume = Try-GetPermutedNoteOffRunConsumeLength $ourEvents $i $refEvents $j
            if ($consume -gt 0) {
                $i += $consume
                $j += $consume
                continue
            }

            $consume = Try-GetPermutedAnyChannelNoteOffRunConsumeLength $ourEvents $i $refEvents $j
            if ($consume -gt 0) {
                $i += $consume
                $j += $consume
                continue
            }

            $consume = Try-GetPermutedShortRunConsumeLength $ourEvents $i $refEvents $j
            if ($consume -gt 0) {
                $i += $consume
                $j += $consume
                continue
            }

            $consume = Try-GetSmallTickClusterShortRunConsumeLength $ourEvents $i $refEvents $j
            if ($consume -gt 0) {
                $i += $consume
                $j += $consume
                continue
            }

            $consume = Try-GetPermutedExpressiveRunConsumeLength $ourEvents $i $refEvents $j
            if ($consume -gt 0) {
                $i += $consume
                $j += $consume
                continue
            }

            $consume = Try-GetPermutedMixedRunConsumeLength $ourEvents $i $refEvents $j
            if ($consume -gt 0) {
                $i += $consume
                $j += $consume
                continue
            }

            $dt1Consume = Try-GetEquivalentRolandDt1RunConsume $ourEvents $i $refEvents $j
            if ($null -ne $dt1Consume) {
                $i += [int]$dt1Consume.OurConsume
                $j += [int]$dt1Consume.RefConsume
                continue
            }
        }

        if (-not $IgnoreSameTickOrdering -or $ourEvents[$i].Tick -ne $refEvents[$j].Tick) {
            $firstDiffIndex = [Math]::Min($i, $j)
            $ourDiff = $o
            $refDiff = $r
            break
        }

        $ourBlock = Get-TickBlock $ourEvents $i
        $refBlock = Get-TickBlock $refEvents $j
        if ($ourBlock.Tick -ne $refBlock.Tick -or -not (Compare-Counts $ourBlock.Counts $refBlock.Counts)) {
            $firstDiffIndex = [Math]::Min($i, $j)
            $ourDiff = $o
            $refDiff = $r
            break
        }

        $i = $ourBlock.EndIndex
        $j = $refBlock.EndIndex
    }

    if ($firstDiffIndex -lt 0 -and ($i -lt $ourEvents.Count -or $j -lt $refEvents.Count)) {
        $acceptableTail = $false

        if ($IgnoreTrailingOurEventsAfterRefEnd -and $j -ge $refEvents.Count -and $i -lt $ourEvents.Count) {
            $acceptableTail = $true
        }
        elseif ($IgnoreTrailingRefEventsAfterOurEnd -and $i -ge $ourEvents.Count -and $j -lt $refEvents.Count) {
            $acceptableTail = $true
        }
        elseif ($IgnoreTrailingNonMusicalEvents) {
            if ($j -ge $refEvents.Count -and $i -lt $ourEvents.Count) {
                $tail = @($ourEvents[$i..($ourEvents.Count - 1)])
                if (@($tail | Where-Object { -not (Is-NonMusicalCompareEvent $_) }).Count -eq 0) {
                    $acceptableTail = $true
                }
            }
            elseif ($i -ge $ourEvents.Count -and $j -lt $refEvents.Count) {
                $tail = @($refEvents[$j..($refEvents.Count - 1)])
                if (@($tail | Where-Object { -not (Is-NonMusicalCompareEvent $_) }).Count -eq 0) {
                    $acceptableTail = $true
                }
            }
        }

        if ($acceptableTail) {
            $firstDiffIndex = -1
            $ourDiff = ""
            $refDiff = ""
        }
        else {
            $firstDiffIndex = [Math]::Min($i, $j)
            $ourDiff = if ($i -lt $ourEvents.Count) { Get-NormalizedEventString $ourEvents[$i] } else { "<end>" }
            $refDiff = if ($j -lt $refEvents.Count) { Get-NormalizedEventString $refEvents[$j] } else { "<end>" }
        }
    }

    return [pscustomobject]@{
        Match = ($firstDiffIndex -lt 0)
        FirstDiffIndex = $firstDiffIndex
        OurDiff = $ourDiff
        RefDiff = $refDiff
    }
}

function Parse-CompareEvent([string]$value) {
    if ([string]::IsNullOrWhiteSpace($value)) {
        return [pscustomobject]@{ Kind = "none" }
    }

    if ($value -eq "<end>") {
        return [pscustomobject]@{ Kind = "end" }
    }

    $parts = $value.Split('|')
    if ($parts.Count -lt 3) {
        return [pscustomobject]@{ Kind = "other" }
    }

    if ($parts[0] -eq "X") {
        return [pscustomobject]@{
            Kind = "sysex"
            Tick = [int]$parts[1]
            Data = $parts[2]
        }
    }

    if ($parts[0] -eq "S") {
        $msg = [uint32]$parts[2]
        $status = $msg -band 0xFF
        $data1 = ($msg -shr 8) -band 0xFF
        $data2 = ($msg -shr 16) -band 0xFF
        $channel = ($status -band 0x0F) + 1
        $statusKind = $status -band 0xF0
        $msgType = switch ($status -band 0xF0) {
            0x80 { "noteoff" }
            0x90 { "noteon" }
            0xB0 { "cc" }
            0xC0 { "program" }
            0xE0 { "pitchbend" }
            default { "short_other" }
        }

        return [pscustomobject]@{
            Kind = "short"
            Tick = [int]$parts[1]
            Status = $status
            Data1 = $data1
            Data2 = $data2
            Channel = $channel
            Type = $msgType
            IsNoteOffLike = (($statusKind -eq 0x80) -or ($statusKind -eq 0x90 -and $data2 -eq 0))
        }
    }

    return [pscustomobject]@{ Kind = "other" }
}

function Is-NoteActiveBeforeTick($plan, [int]$channel, [int]$note, [int]$tick) {
    if ($null -eq $plan -or $channel -lt 1 -or $channel -gt 16 -or $note -lt 0 -or $note -gt 127) {
        return $false
    }

    $active = $false
    foreach ($evt in $plan.MidiEvents) {
        $evtTick = [int]$evt.Tick
        if ($evtTick -ge $tick) {
            break
        }

        if ($evt.Packet.Kind -ne [RcpPlayer.Core.Playback.MidiMessageKind]::Short) {
            continue
        }

        $msg = [uint32]$evt.Packet.ShortMessage
        $status = $msg -band 0xFF
        $evtChannel = ($status -band 0x0F) + 1
        if ($evtChannel -ne $channel) {
            continue
        }

        $kind = $status -band 0xF0
        $data1 = ($msg -shr 8) -band 0xFF
        $data2 = ($msg -shr 16) -band 0xFF
        if ($data1 -ne $note) {
            continue
        }

        if ($kind -eq 0x80) {
            $active = $false
        }
        elseif ($kind -eq 0x90) {
            $active = ($data2 -gt 0)
        }
    }

    return $active
}

function Classify-Mismatch([string]$ourDiff, [string]$refDiff, $ourPlan, $refPlan) {
    $our = Parse-CompareEvent $ourDiff
    $ref = Parse-CompareEvent $refDiff

    $result = [ordered]@{
        Severity = "possibly_audible"
        Reason = "other_short_or_tail"
        IsLikelyInaudible = $false
    }

    if ($our.Kind -eq "sysex" -or $ref.Kind -eq "sysex") {
        $result.Severity = "likely_audible"
        $result.Reason = "sysex_state"
        return [pscustomobject]$result
    }

    if ($our.Kind -eq "short" -and $ref.Kind -eq "short") {
        if ($our.Type -eq "program" -or $ref.Type -eq "program") {
            $result.Severity = "likely_audible"
            $result.Reason = "program_change"
            return [pscustomobject]$result
        }

        if ($our.Type -eq "pitchbend" -or $ref.Type -eq "pitchbend") {
            $result.Severity = "likely_audible"
            $result.Reason = "pitchbend"
            return [pscustomobject]$result
        }

        if ($our.Type -eq "noteon" -and $ref.Type -eq "noteon") {
            if ($our.Data2 -eq 0 -and $ref.Data2 -eq 0) {
                $ourActive = Is-NoteActiveBeforeTick -plan $ourPlan -channel $our.Channel -note $our.Data1 -tick $our.Tick
                $refActive = Is-NoteActiveBeforeTick -plan $refPlan -channel $ref.Channel -note $ref.Data1 -tick $ref.Tick
                if (-not $ourActive -and -not $refActive) {
                    $result.Severity = "likely_inaudible"
                    $result.Reason = "noteon0_cleanup_reorder"
                    $result.IsLikelyInaudible = $true
                }
                else {
                    $result.Severity = "likely_audible"
                    $result.Reason = "noteon0_target_diff_or_active_release"
                }
                return [pscustomobject]$result
            }

            $result.Severity = "likely_audible"
            $result.Reason = "note_diff"
            return [pscustomobject]$result
        }

        if ($our.Type -eq "cc" -and $ref.Type -eq "cc") {
            if (($our.Data1 -in 7, 10, 11) -and ($ref.Data1 -in 7, 10, 11)) {
                $result.Severity = "possibly_inaudible"
                $result.Reason = "mix_cc_diff"
                return [pscustomobject]$result
            }
        }
    }

    if ($our.Kind -eq "end" -and $ref.Kind -eq "short") {
        if ($ref.IsNoteOffLike) {
            $wouldReleaseActive = Is-NoteActiveBeforeTick -plan $ourPlan -channel $ref.Channel -note $ref.Data1 -tick $ref.Tick
            if ($wouldReleaseActive) {
                $result.Severity = "likely_audible"
                $result.Reason = "missing_note_release_active"
            }
            else {
                $result.Severity = "likely_inaudible"
                $result.Reason = "tail_noteoff_cleanup"
                $result.IsLikelyInaudible = $true
            }
            return [pscustomobject]$result
        }

        if ($ref.Type -eq "cc" -and ($ref.Data1 -in 120, 121, 123)) {
            $result.Severity = "likely_inaudible"
            $result.Reason = "tail_reset_cc"
            $result.IsLikelyInaudible = $true
            return [pscustomobject]$result
        }
    }

    if ($ref.Kind -eq "end" -and $our.Kind -eq "short") {
        if ($our.IsNoteOffLike) {
            $cutsActive = Is-NoteActiveBeforeTick -plan $ourPlan -channel $our.Channel -note $our.Data1 -tick $our.Tick
            if ($cutsActive) {
                $result.Severity = "likely_audible"
                $result.Reason = "extra_note_release_active"
            }
            else {
                $result.Severity = "likely_inaudible"
                $result.Reason = "tail_noteoff_cleanup"
                $result.IsLikelyInaudible = $true
            }
            return [pscustomobject]$result
        }

        if ($our.Type -eq "cc" -and ($our.Data1 -in 120, 121, 123)) {
            $result.Severity = "likely_inaudible"
            $result.Reason = "tail_reset_cc"
            $result.IsLikelyInaudible = $true
            return [pscustomobject]$result
        }
    }

    return [pscustomobject]$result
}

function Has-InfiniteLoopTrack($song, [bool]$treatHighLoopCountAsInfinite) {
    if ($null -eq $song) {
        return $false
    }

    foreach ($track in $song.Tracks) {
        foreach ($e in $track.Events) {
            if ($e.CommandOrNote -ne 0xF8) {
                continue
            }

            $repeat = [int]$e.DelayTicks
            if ($repeat -le 0) {
                return $true
            }

            if ($treatHighLoopCountAsInfinite -and $repeat -ge 0x7F) {
                return $true
            }
        }
    }

    return $false
}

function Is-InfiniteLoopTailFinalizeMismatch(
    $song,
    [bool]$treatHighLoopCountAsInfinite,
    [int]$ourEvents,
    [int]$refEvents,
    [long]$ourLastTick,
    [long]$refLastTick,
    [int]$firstDiffIndex)
{
    if (-not (Has-InfiniteLoopTrack -song $song -treatHighLoopCountAsInfinite $treatHighLoopCountAsInfinite)) {
        return $false
    }

    if ($ourEvents -le 0 -or $refEvents -le 0) {
        return $false
    }

    if ($ourLastTick -ge $refLastTick) {
        return $false
    }

    # Consider this "tail finalize only" when the first mismatch starts near the end.
    $tailThreshold = [int][Math]::Floor($ourEvents * 0.95)
    return ($firstDiffIndex -ge $tailThreshold)
}

function Is-ReferenceContinuationTailMismatch(
    [int]$ourEvents,
    [int]$refEvents,
    [long]$ourLastTick,
    [long]$refLastTick,
    [int]$firstDiffIndex)
{
    if ($ourEvents -le 0 -or $refEvents -le 0) {
        return $false
    }

    if ($ourLastTick -ge $refLastTick) {
        return $false
    }

    # Only treat as continuation when reference is substantially longer.
    if ($refEvents -lt [int][Math]::Ceiling($ourEvents * 1.5)) {
        return $false
    }

    # Mismatch must start late in our event stream.
    $tailThreshold = [int][Math]::Floor($ourEvents * 0.85)
    if ($firstDiffIndex -ge $tailThreshold) {
        return $true
    }

    # For very long reference continuations, allow a slightly earlier threshold.
    if ($refEvents -ge [int][Math]::Ceiling($ourEvents * 5.0)) {
        $relaxedThreshold = [int][Math]::Floor($ourEvents * 0.70)
        if ($firstDiffIndex -ge $relaxedThreshold) {
            return $true
        }
    }

    return $false
}

if (-not (Test-Path $CollectionRoot)) {
    throw "Collection root not found: $CollectionRoot"
}

if ($ReferenceKind -eq "rcpcvc") {
    if (-not $PSBoundParameters.ContainsKey("ReferenceExe")) {
        $ReferenceExe = "tools/rcpcv/rcpcv112/rcpcvc.exe"
    }
    if (-not $PSBoundParameters.ContainsKey("FallbackReferenceExe")) {
        $FallbackReferenceExe = "artifacts/ref_tools/rcp2mid_ref.exe"
    }
}
elseif ($ReferenceKind -eq "x68rcpx") {
    if (-not $PSBoundParameters.ContainsKey("ReferenceExe")) {
        if (Test-Path $X68OracleRun68Exe) {
            $ReferenceExe = $X68OracleRun68Exe
        }
        else {
            $ReferenceExe = "tools/run68/run68bin-009a-20090920/run68.exe"
        }
    }
}

if ($ReferenceKind -eq "x68rcpx") {
    if (-not $PSBoundParameters.ContainsKey("StopCompareWhenOurEventsOverReference")) {
        $StopCompareWhenOurEventsOverReference = $false
    }
    if (-not $PSBoundParameters.ContainsKey("IgnoreKnownStartupPrologue")) {
        $IgnoreKnownStartupPrologue = $true
    }
    if (-not $PSBoundParameters.ContainsKey("IgnoreLeadingPreNoteSetup")) {
        $IgnoreLeadingPreNoteSetup = $true
    }
    if ($X68CompareByMessageOrder -and -not $PSBoundParameters.ContainsKey("IgnoreTicksInMessageOrderCompare")) {
        $IgnoreTicksInMessageOrderCompare = $true
    }
    if (-not $PSBoundParameters.ContainsKey("IgnoreTrailingRefEventsAfterOurEnd")) {
        $IgnoreTrailingRefEventsAfterOurEnd = $true
    }
    if (-not $PSBoundParameters.ContainsKey("IgnoreInvalidShortMidiData")) {
        $IgnoreInvalidShortMidiData = $true
    }
    if (-not $PSBoundParameters.ContainsKey("IgnoreLikelyInaudibleMismatches")) {
        $IgnoreLikelyInaudibleMismatches = $true
    }
    if (-not $PSBoundParameters.ContainsKey("IgnoreReferenceContinuationTailMismatch")) {
        $IgnoreReferenceContinuationTailMismatch = $true
    }
    if (-not $PSBoundParameters.ContainsKey("EmitPendingNoteOffsAfterTrackEnd")) {
        $EmitPendingNoteOffsAfterTrackEnd = $true
    }
    if (-not $PSBoundParameters.ContainsKey("TreatVelocityZeroNoteAsNoteOff")) {
        $TreatVelocityZeroNoteAsNoteOff = $false
    }
}

if ($ReferenceKind -eq "rcpcvc" -and $CompatCompare) {
    if (-not $PSBoundParameters.ContainsKey("Loops")) {
        $Loops = 2
    }
    if (-not $PSBoundParameters.ContainsKey("EmitEmptySysEx")) {
        $EmitEmptySysEx = $true
    }
    if (-not $PSBoundParameters.ContainsKey("UseRealtimeForInfiniteLoops")) {
        $UseRealtimeForInfiniteLoops = $true
    }
    if (-not $PSBoundParameters.ContainsKey("StopOnInfiniteTracksOnly")) {
        $StopOnInfiniteTracksOnly = $false
    }
    if (-not $PSBoundParameters.ContainsKey("FreezeInfiniteTracksAfterFirstCycle")) {
        $FreezeInfiniteTracksAfterFirstCycle = $false
    }
    if (-not $PSBoundParameters.ContainsKey("TreatHighLoopCountAsInfinite")) {
        # rcpcvc compatibility: high loop counts (e.g. 0x7F/0xFF) are often
        # used as practical infinity in legacy material.
        $TreatHighLoopCountAsInfinite = $true
    }
    if (-not $PSBoundParameters.ContainsKey("InfiniteLoopTailTicks")) {
        $InfiniteLoopTailTicks = 768
    }
    if (-not $PSBoundParameters.ContainsKey("InfiniteLoopTailMeasures")) {
        $InfiniteLoopTailMeasures = 0
    }
    if (-not $PSBoundParameters.ContainsKey("IgnorePendingOnAllTracksPlayedStop")) {
        $IgnorePendingOnAllTracksPlayedStop = $true
    }
    if (-not $PSBoundParameters.ContainsKey("EmitPendingNoteOffsAfterTrackEnd")) {
        $EmitPendingNoteOffsAfterTrackEnd = $false
    }
    if (-not $PSBoundParameters.ContainsKey("RcpcvCompatTerminateDrumSetupAtReverbComment")) {
        $RcpcvCompatTerminateDrumSetupAtReverbComment = $false
    }
    if (-not $PSBoundParameters.ContainsKey("RcpcvCompatBarDelayBeforeVelocityZeroNote")) {
        $RcpcvCompatBarDelayBeforeVelocityZeroNote = $true
    }
    if (-not $PSBoundParameters.ContainsKey("RcpcvCompatLimitSysExPayloadTo255")) {
        $RcpcvCompatLimitSysExPayloadTo255 = $true
    }
    if (-not $PSBoundParameters.ContainsKey("RcpcvCompatDropFollowingSysExAfterLongPayload")) {
        $RcpcvCompatDropFollowingSysExAfterLongPayload = $true
    }
    if (-not $PSBoundParameters.ContainsKey("RcpcvCompatMaxSysExPerTick")) {
        $RcpcvCompatMaxSysExPerTick = 99
    }
    if (-not $PSBoundParameters.ContainsKey("RcpcvCompatNormalizeMalformedSysEx")) {
        $RcpcvCompatNormalizeMalformedSysEx = $true
    }
    if (-not $PSBoundParameters.ContainsKey("IgnoreSameTickOrdering")) {
        $IgnoreSameTickOrdering = $true
    }
    if (-not $PSBoundParameters.ContainsKey("IgnoreCc7Differences")) {
        # rcpcvc commonly emits synthetic CC7 tail ramps.
        $IgnoreCc7Differences = $true
    }
    if (-not $PSBoundParameters.ContainsKey("IgnoreInvalidShortMidiData")) {
        # Some rcpcvc outputs contain malformed short data bytes (> 0x7F) near tails.
        $IgnoreInvalidShortMidiData = $true
    }
    if (-not $PSBoundParameters.ContainsKey("IgnoreNearTickEqualShortEvents")) {
        $IgnoreNearTickEqualShortEvents = $true
    }
    if (-not $PSBoundParameters.ContainsKey("ShortEventTickJitter")) {
        $ShortEventTickJitter = 16
    }
    if (-not $PSBoundParameters.ContainsKey("ShortEventTickJitterLimit")) {
        $ShortEventTickJitterLimit = 5000
    }
    if (-not $PSBoundParameters.ContainsKey("NormalizeEarlySysExTickGrid")) {
        $NormalizeEarlySysExTickGrid = $true
    }
    if (-not $PSBoundParameters.ContainsKey("EarlySysExTickGrid")) {
        $EarlySysExTickGrid = 12
    }
    if (-not $PSBoundParameters.ContainsKey("EarlySysExTickLimit")) {
        $EarlySysExTickLimit = 2048
    }
    if (-not $PSBoundParameters.ContainsKey("IgnoreTrailingNonMusicalEvents")) {
        $IgnoreTrailingNonMusicalEvents = $true
    }
    if (-not $PSBoundParameters.ContainsKey("IgnoreTrailingOurEventsAfterRefEnd")) {
        # rcpcvc and our renderer can differ in tail note-off cleanup.
        $IgnoreTrailingOurEventsAfterRefEnd = $true
    }
    if (-not $PSBoundParameters.ContainsKey("IgnoreTrailingRefEventsAfterOurEnd")) {
        # rcpcvc can continue converter-specific loop tails after our practical stop point.
        $IgnoreTrailingRefEventsAfterOurEnd = $true
    }
    if (-not $PSBoundParameters.ContainsKey("IgnoreRcpcvcTailPhase")) {
        # rcpcvc can emit a converter-specific multi-channel CC7 tail phase.
        $IgnoreRcpcvcTailPhase = $true
    }
    if (-not $PSBoundParameters.ContainsKey("OurEventsOverReferenceRatio")) {
        $OurEventsOverReferenceRatio = 2.5
    }
}

if ($ReferenceKind -eq "rcp2mid") {
    Ensure-ReferenceExe -exePath $ReferenceExe -sourcePath $ReferenceSource
}
elseif (-not (Test-Path $ReferenceExe)) {
    throw "Reference executable not found: $ReferenceExe"
}

if ($ReferenceKind -eq "x68rcpx" -and -not (Test-Path $X68Program)) {
    throw "X68000 converter program not found: $X68Program"
}
if ($ReferenceKind -eq "x68rcpx" -and -not (Test-Path $X68ChildProgram)) {
    throw "X68000 child program not found: $X68ChildProgram"
}

if ($ReferenceKind -eq "rcpcvc" -and $UseFallbackReferenceOnFailure) {
    Ensure-ReferenceExe -exePath $FallbackReferenceExe -sourcePath $FallbackReferenceSource
}
$coreDll = Ensure-CoreDll
Add-Type -Path $coreDll

if (-not (Test-Path $OutDir)) {
    New-Item -ItemType Directory -Path $OutDir -Force | Out-Null
}
$refMidDir = Join-Path $OutDir "reference_mid"
if (-not (Test-Path $refMidDir)) {
    New-Item -ItemType Directory -Path $refMidDir -Force | Out-Null
}

$parser = [RcpPlayer.Core.Parsing.RcpParser]::new()
$builder = [RcpPlayer.Core.Playback.RcpSequenceBuilder]::new()
$smfParser = [RcpPlayer.Core.Parsing.StandardMidiParser]::new()

$files = @(
    Get-ChildItem -Path $CollectionRoot -Recurse -File -Include *.rcp,*.RCP,*.r36,*.R36,*.g36,*.G36,*.mcp,*.MCP |
        Sort-Object FullName
)
$excludedZeroByteCount = 0
if ($ExcludeZeroByteFiles) {
    $excludedZeroByteCount = @($files | Where-Object Length -le 0).Count
    $files = @($files | Where-Object Length -gt 0)
}
if ($StartFileIndex -gt 0) {
    $files = @($files | Select-Object -Skip $StartFileIndex)
}
if ($MaxFiles -gt 0) {
    $files = @($files | Select-Object -First $MaxFiles)
}

$results = New-Object System.Collections.Generic.List[object]
$index = 0
$total = $files.Count

foreach ($file in $files) {
    $index++
    $rel = [System.IO.Path]::GetRelativePath((Resolve-Path $CollectionRoot), $file.FullName)
    Write-Host "[$index/$total] $rel"

    # Use an ASCII-only staging name to avoid legacy converter path/codepage issues.
    $tempBase = Join-Path $refMidDir ("f{0:D6}" -f $index)
    $tempInput = [System.IO.Path]::ChangeExtension($tempBase, [System.IO.Path]::GetExtension($file.FullName))
    $tempMid = [System.IO.Path]::ChangeExtension($tempBase, ".mid")

    $result = [ordered]@{
        File = $rel
        Status = "ok"
        Error = ""
        TimedOut = $false
        DurationMs = 0
        OurEvents = 0
        RefEvents = 0
        OurLastTick = 0
        RefLastTick = 0
        OurTempoEvents = 0
        RefTempoEvents = 0
        OurDigest = ""
        RefDigest = ""
        Match = $false
        FirstDiffIndex = -1
        RefPartial = $false
        FirstDiffOur = ""
        FirstDiffRef = ""
        MismatchSeverity = ""
        MismatchReason = ""
        SuppressedLikelyInaudible = $false
        X68RefBytes = 0
        X68RefEvents = 0
        X68RefShortEvents = 0
        X68RefSysExEvents = 0
        X68RefNoteEvents = 0
    }

    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    $deadlineUtc = if ($PerFileTimeoutSec -gt 0) { [datetime]::UtcNow.AddSeconds($PerFileTimeoutSec) } else { [datetime]::MaxValue }
    try {
        Assert-PerFileDeadline -DeadlineUtc $deadlineUtc -ConfiguredTimeoutSec $PerFileTimeoutSec -Phase "file read"
        $bytes = [System.IO.File]::ReadAllBytes($file.FullName)
        [System.IO.File]::WriteAllBytes($tempInput, $bytes)
        Assert-PerFileDeadline -DeadlineUtc $deadlineUtc -ConfiguredTimeoutSec $PerFileTimeoutSec -Phase "RCP parse"
        $song = $parser.Parse($bytes)
        $x68SupportFiles = Resolve-X68SupportFilePaths -InputPath $file.FullName -Song $song
        Assert-PerFileDeadline -DeadlineUtc $deadlineUtc -ConfiguredTimeoutSec $PerFileTimeoutSec -Phase "loop analysis"
        $complexity = $null

        if ($PerFileTimeoutSec -gt 0) {
            $complexity = Get-LoopComplexity $song
        }

        if ($PerFileTimeoutSec -gt 0 -and $EnableComplexityGuard -and $null -ne $complexity) {
            if ($complexity.FcCount -ge $ComplexityGuardFcThreshold -or
                ($complexity.FcCount -ge $ComplexityGuardMixedFcThreshold -and
                 $complexity.InfiniteLoopTracks -ge $ComplexityGuardMixedInfiniteTrackThreshold)) {
                $result.Status = "skip"
                $result.Error = "Skipped by complexity guard (FC=$($complexity.FcCount), infiniteTracks=$($complexity.InfiniteLoopTracks))."
                $result.TimedOut = $false
                throw [System.OperationCanceledException]::new($result.Error)
            }
        }

        if ($ReferenceKind -eq "x68rcpx" -and $PerFileTimeoutSec -gt 0 -and $null -ne $complexity) {
            $x68AdaptiveTimeoutSec = Get-X68AdaptiveTimeoutExtensionSec $complexity
            if ($x68AdaptiveTimeoutSec -gt 0) {
                $deadlineUtc = $deadlineUtc.AddSeconds($x68AdaptiveTimeoutSec)
            }
        }

        $builderOptions = [RcpPlayer.Core.Playback.RcpSequenceBuilderOptions]@{
            InfinityLoopCount = [Math]::Max(1, $Loops)
            MergeActiveSameNote = $MergeActiveSameNote
            KeepBoundaryActiveSameNote = $KeepBoundaryActiveSameNote
            EmitEmptySysEx = $EmitEmptySysEx
            EmitPendingNoteOffsAfterTrackEnd = $EmitPendingNoteOffsAfterTrackEnd
            UseRealtimeForInfiniteLoops = $UseRealtimeForInfiniteLoops
            StopOnInfiniteTracksOnly = $StopOnInfiniteTracksOnly
            FreezeInfiniteTracksAfterFirstCycle = $FreezeInfiniteTracksAfterFirstCycle
            TreatHighLoopCountAsInfinite = $TreatHighLoopCountAsInfinite
            BalanceInfiniteLoopTracks = $BalanceInfiniteLoopTracks
            InfiniteLoopTailTicks = $InfiniteLoopTailTicks
            InfiniteLoopTailMeasures = $InfiniteLoopTailMeasures
            IgnorePendingOnAllTracksPlayedStop = $IgnorePendingOnAllTracksPlayedStop
            RcpcvCompatTerminateDrumSetupAtReverbComment = $RcpcvCompatTerminateDrumSetupAtReverbComment
            RcpcvCompatBarDelayBeforeVelocityZeroNote = $RcpcvCompatBarDelayBeforeVelocityZeroNote
            TreatVelocityZeroNoteAsNoteOff = $TreatVelocityZeroNoteAsNoteOff
            RcpcvCompatLimitSysExPayloadTo255 = $RcpcvCompatLimitSysExPayloadTo255
            RcpcvCompatDropFollowingSysExAfterLongPayload = $RcpcvCompatDropFollowingSysExAfterLongPayload
            RcdStrictMode = $RcdStrictMode.IsPresent
            RcpcvCompatDeduplicateSysExAtSameTick = $IgnoreDuplicateSysExSameTick
            RcpcvCompatMaxSysExPerTick = $RcpcvCompatMaxSysExPerTick
        }
        Assert-PerFileDeadline -DeadlineUtc $deadlineUtc -ConfiguredTimeoutSec $PerFileTimeoutSec -Phase "local sequence build"
        $ourPlan = $builder.Build($song, $builderOptions)
        Assert-PerFileDeadline -DeadlineUtc $deadlineUtc -ConfiguredTimeoutSec $PerFileTimeoutSec -Phase "post-build"

        if ($PerFileTimeoutSec -gt 0 -and -not $RetryWithoutTimeoutOnTimeout -and $stopwatch.Elapsed.TotalSeconds -gt $PerFileTimeoutSec) {
            $result.Status = "timeout"
            $result.Error = "Local parse/build exceeded timeout (${PerFileTimeoutSec}s)."
            $result.TimedOut = $true
            throw [System.TimeoutException]::new($result.Error)
        }

        $args = New-Object System.Collections.Generic.List[string]
        $refMidPath = $tempMid
        if ($ReferenceKind -eq "rcp2mid") {
            $args.Add("-Loops")
            $args.Add("$Loops")
            if ($NoLoopExtension) {
                $args.Add("-NoLpExt")
            }
            $args.Add($tempInput)
            $args.Add($tempMid)
        }
        elseif ($ReferenceKind -eq "rcpcvc") {
            # RCPCVC emits "<input>.mid", so convert from a temp input path.
            $args.Add($tempInput)
            $refMidPath = [System.IO.Path]::ChangeExtension($tempInput, ".mid")
            if (Test-Path $refMidPath) {
                Remove-Item -LiteralPath $refMidPath -Force -ErrorAction SilentlyContinue
            }
        }
        else {
            # x68rcpx path always stages output into our temp MID.
            $refMidPath = $tempMid
            if (Test-Path $refMidPath) {
                Remove-Item -LiteralPath $refMidPath -Force -ErrorAction SilentlyContinue
            }
        }

        $referencePath = (Resolve-Path $ReferenceExe).Path
        $primaryTimeout = Get-RemainingTimeoutSec -DeadlineUtc $deadlineUtc -ConfiguredTimeoutSec $PerFileTimeoutSec
        $primaryResult = $null
        if ($ReferenceKind -eq "x68rcpx") {
            if ($PerFileTimeoutSec -gt 0 -and $X68MinConvertTimeoutSec -gt 0 -and $primaryTimeout -lt $X68MinConvertTimeoutSec) {
                # Keep enough conversion budget to reduce partial x68 captures.
                $deadlineUtc = $deadlineUtc.AddSeconds($X68MinConvertTimeoutSec - $primaryTimeout)
                $primaryTimeout = $X68MinConvertTimeoutSec
            }

            if ($PerFileTimeoutSec -gt 0 -and $primaryTimeout -le 0) {
                $result.Status = "timeout"
                $result.Error = "Per-file timeout (${PerFileTimeoutSec}s) exceeded before reference convert."
                $result.TimedOut = $true
                throw [System.TimeoutException]::new($result.Error)
            }

            $primaryResult = Invoke-X68ReferenceConverter `
                -Run68ExePath $referencePath `
                -ProgramPath (Resolve-Path $X68Program).Path `
                -ProgramArgsTemplate $X68ProgramArgs `
                -ChildProgramPath (Resolve-Path $X68ChildProgram).Path `
                -ChildCommandTemplate $X68ChildCommand `
                -InputPath $tempInput `
                -SupportFilePaths $x68SupportFiles `
                -WorkRoot $X68WorkRoot `
                -TimeoutSec $primaryTimeout `
                -RetryWithoutTimeoutOnTimeout $RetryWithoutTimeoutOnTimeout `
                -RetryTimeoutSec $X68RetryTimeoutSec `
                -KeepWorkDirOnFailure $KeepX68WorkDirOnFailure `
                -EnableMidiStub $X68EnableMidiStub `
                -EnableIoLog $X68EnableIoLog `
                -CaptureRunLogs $X68CaptureRunLogs
        }
        else {
            if ($PerFileTimeoutSec -gt 0 -and $primaryTimeout -le 0) {
                $result.Status = "timeout"
                $result.Error = "Per-file timeout (${PerFileTimeoutSec}s) exceeded before reference convert."
                $result.TimedOut = $true
                throw [System.TimeoutException]::new($result.Error)
            }

            $primaryResult = Invoke-ReferenceConverter `
                -ExePath $referencePath `
                -Arguments @($args) `
                -ExpectedOutputPath $refMidPath `
                -TimeoutSec $primaryTimeout `
                -RetryWithoutTimeoutOnTimeout $RetryWithoutTimeoutOnTimeout
        }

        $canFallbackFromPrimaryFailure = $ReferenceKind -eq "rcpcvc" -and
                                         $UseFallbackReferenceOnFailure -and
                                         -not $primaryResult.Success -and
                                         (-not $primaryResult.TimedOut -or $UseFallbackReferenceOnTimeout)
        if ($canFallbackFromPrimaryFailure) {
            $fallbackPath = (Resolve-Path $FallbackReferenceExe).Path
            $fallbackArgs = New-Object System.Collections.Generic.List[string]
            $fallbackArgs.Add("-Loops")
            $fallbackArgs.Add("$Loops")
            if ($NoLoopExtension) {
                $fallbackArgs.Add("-NoLpExt")
            }
            $fallbackArgs.Add($tempInput)
            $fallbackArgs.Add($tempMid)
            if (Test-Path $tempMid) {
                Remove-Item -LiteralPath $tempMid -Force -ErrorAction SilentlyContinue
            }

            $useExtendedTimeoutForFallback = $primaryResult.TimedOut -and $UseFallbackReferenceOnTimeout
            $fallbackTimeout = if ($useExtendedTimeoutForFallback) {
                if ($PerFileTimeoutSec -gt 0) { [Math]::Max($PerFileTimeoutSec, 10) } else { 0 }
            }
            else {
                Get-RemainingTimeoutSec -DeadlineUtc $deadlineUtc -ConfiguredTimeoutSec $PerFileTimeoutSec
            }

            if ($PerFileTimeoutSec -gt 0 -and -not $useExtendedTimeoutForFallback -and $fallbackTimeout -le 0) {
                $result.Status = "timeout"
                $result.Error = "Per-file timeout (${PerFileTimeoutSec}s) exceeded before fallback reference convert."
                $result.TimedOut = $true
                throw [System.TimeoutException]::new($result.Error)
            }
            $fallbackResult = Invoke-ReferenceConverter `
                -ExePath $fallbackPath `
                -Arguments @($fallbackArgs) `
                -ExpectedOutputPath $tempMid `
                -TimeoutSec $fallbackTimeout `
                -RetryWithoutTimeoutOnTimeout $RetryWithoutTimeoutOnTimeout

            if ($fallbackResult.Success) {
                if ($useExtendedTimeoutForFallback -and $PerFileTimeoutSec -gt 0) {
                    # Primary rcpcvc timed out/hung; allow a fresh bounded window for fallback compare phases.
                    $deadlineUtc = [datetime]::UtcNow.AddSeconds([Math]::Max($PerFileTimeoutSec, 10))
                }
                $refMidPath = $tempMid
                $primaryResult = $fallbackResult
            }
            else {
                $primaryResult = [pscustomobject]@{
                    Success = $false
                    TimedOut = ($primaryResult.TimedOut -or $fallbackResult.TimedOut)
                    Error = "Primary reference failed: $($primaryResult.Error) || Fallback failed: $($fallbackResult.Error)"
                }
            }
        }

        if (-not $primaryResult.Success) {
            if ($primaryResult.TimedOut) {
                $result.Status = "timeout"
                $result.Error = $primaryResult.Error
                $result.TimedOut = $true
                throw [System.TimeoutException]::new($result.Error)
            }

            if ($TreatReferenceFailureAsSkip) {
                $result.Status = "skip"
                $result.Error = $primaryResult.Error
                throw [System.OperationCanceledException]::new($result.Error)
            }

            throw $primaryResult.Error
        }

        if ($primaryResult.TimedOut) {
            $result.RefPartial = $true
            if ([string]::IsNullOrWhiteSpace($result.Error)) {
                $result.Error = "Reference timeout; using partial captured output."
            }
            if ($PerFileTimeoutSec -gt 0) {
                # Allow a fresh bounded window for parse/compare of partial captures.
                $deadlineUtc = [datetime]::UtcNow.AddSeconds([Math]::Max($PerFileTimeoutSec, 10))
            }
        }

        if ($ReferenceKind -eq "rcpcvc" -and $refMidPath -ne $tempMid) {
            Copy-Item -LiteralPath $refMidPath -Destination $tempMid -Force
            $refMidPath = $tempMid
        }

        $ourComparePlan = $ourPlan
        $refPlan = $null
        if ($ReferenceKind -eq "x68rcpx") {
            if ($null -eq $primaryResult.ReferencePlan) {
                throw "x68 reference conversion returned no reference plan."
            }
            if ($null -ne $primaryResult.ReferenceStats) {
                $result.X68RefBytes = [int]$primaryResult.ReferenceStats.ByteCount
                $result.X68RefEvents = [int]$primaryResult.ReferenceStats.EventCount
                $result.X68RefShortEvents = [int]$primaryResult.ReferenceStats.ShortEventCount
                $result.X68RefSysExEvents = [int]$primaryResult.ReferenceStats.SysExEventCount
                $result.X68RefNoteEvents = [int]$primaryResult.ReferenceStats.NoteEventCount
            }
            if ($X68RequireNoteEvents -and $result.X68RefNoteEvents -le 0) {
                $result.Status = "skip"
                $result.Error = "X68 reference is bootstrap-only (no note events captured)."
                throw [System.OperationCanceledException]::new($result.Error)
            }
            if ($X68CompareByMessageOrder) {
                $ourComparePlan = Convert-PlanToMessageOrderPlan $ourPlan
            }
            $refPlan = $primaryResult.ReferencePlan
        }
        else {
            Assert-PerFileDeadline -DeadlineUtc $deadlineUtc -ConfiguredTimeoutSec $PerFileTimeoutSec -Phase "reference MIDI parse"
            $refBytes = [System.IO.File]::ReadAllBytes($refMidPath)
            $refSong = $smfParser.Parse($refBytes)
            $refPlan = $refSong.PlaybackPlan
        }
        Assert-PerFileDeadline -DeadlineUtc $deadlineUtc -ConfiguredTimeoutSec $PerFileTimeoutSec -Phase "plan compare"

        $result.OurEvents = $ourComparePlan.MidiEvents.Count
        $result.RefEvents = $refPlan.MidiEvents.Count
        $result.OurLastTick = if ($ourComparePlan.MidiEvents.Count -gt 0) { $ourComparePlan.MidiEvents[$ourComparePlan.MidiEvents.Count - 1].Tick } else { 0 }
        $result.RefLastTick = if ($refPlan.MidiEvents.Count -gt 0) { $refPlan.MidiEvents[$refPlan.MidiEvents.Count - 1].Tick } else { 0 }
        $result.OurTempoEvents = $ourComparePlan.TempoEvents.Count
        $result.RefTempoEvents = $refPlan.TempoEvents.Count
        if ($result.RefPartial -and $ClearRefPartialWhenCoverageSufficient) {
            $coverageRatio = [Math]::Max(1.0, $RefPartialCoverageEventRatio)
            $minRefEvents = [int][Math]::Ceiling([double][Math]::Max(1, $result.OurEvents) * $coverageRatio)
            $eventsCovered = $result.RefEvents -ge $minRefEvents
            $ticksCovered = $result.RefLastTick -ge $result.OurLastTick
            if ($eventsCovered -and $ticksCovered) {
                $result.RefPartial = $false
                if ($result.Error -eq "Reference timeout; using partial captured output.") {
                    $result.Error = ""
                }
            }
        }

        $overflow = $false
        if ($StopCompareWhenOurEventsOverReference) {
            if ($result.RefEvents -le 0) {
                $overflow = ($result.OurEvents -gt 0)
            }
            else {
                $overflow = ([double]$result.OurEvents -gt ([double]$result.RefEvents * $OurEventsOverReferenceRatio))
            }
        }

        if ($overflow) {
            $result.Status = "mismatch"
            $result.Match = $false
            $result.FirstDiffIndex = 0
            $result.FirstDiffOur = "EVENT_COUNT_OVERFLOW our=$($result.OurEvents)"
            $result.FirstDiffRef = "EVENT_COUNT_OVERFLOW ref=$($result.RefEvents)"
            $result.MismatchSeverity = "likely_audible"
            $result.MismatchReason = "event_count_overflow"
            $result.Error = "Early stop: our events exceed reference by ratio > $OurEventsOverReferenceRatio."
        }
        else {
            Assert-PerFileDeadline -DeadlineUtc $deadlineUtc -ConfiguredTimeoutSec $PerFileTimeoutSec -Phase "event compare"
            $cmp = Compare-Plans -ourPlan $ourComparePlan -refPlan $refPlan
            $skipDigest = $false
            if ($DigestSkipEventThreshold -gt 0) {
                $maxPlanEvents = [Math]::Max([int]$result.OurEvents, [int]$result.RefEvents)
                if ($maxPlanEvents -ge $DigestSkipEventThreshold) {
                    $skipDigest = $true
                }
            }

            if (-not $skipDigest -and $PerFileTimeoutSec -gt 0 -and $DigestMinRemainingTimeoutSec -gt 0) {
                $remainingBeforeDigest = Get-RemainingTimeoutSec -DeadlineUtc $deadlineUtc -ConfiguredTimeoutSec $PerFileTimeoutSec
                if ($remainingBeforeDigest -le $DigestMinRemainingTimeoutSec) {
                    $skipDigest = $true
                }
            }

            if ($skipDigest) {
                $result.OurDigest = "[skipped]"
                $result.RefDigest = "[skipped]"
            }
            else {
                Assert-PerFileDeadline -DeadlineUtc $deadlineUtc -ConfiguredTimeoutSec $PerFileTimeoutSec -Phase "digest generation"
                $result.OurDigest = Get-PlanDigest $ourComparePlan
                $result.RefDigest = Get-PlanDigest $refPlan
            }
            $result.Match = $cmp.Match
            $result.FirstDiffIndex = $cmp.FirstDiffIndex
            $result.FirstDiffOur = $cmp.OurDiff
            $result.FirstDiffRef = $cmp.RefDiff
            if ($DebugDumpCompareWindow -gt 0 -and -not $cmp.Match -and $cmp.FirstDiffIndex -ge 0) {
                $ourDebugEvents = Get-PreparedCompareEvents $ourComparePlan
                $refDebugEvents = Get-PreparedCompareEvents $refPlan
                $debugStem = (($result.File -replace '[\\/:*?"<>|]', '__') -replace '\.RCP$', '')
                $debugPath = Join-Path $OutDir ($debugStem + ".compare-window.txt")
                Write-CompareDebugWindow `
                    -outPath $debugPath `
                    -ourEvents $ourDebugEvents `
                    -refEvents $refDebugEvents `
                    -firstDiffIndex $cmp.FirstDiffIndex `
                    -radius $DebugDumpCompareWindow
            }
            if (-not $cmp.Match) {
                if ($TreatRefPartialMismatchAsSkip -and $result.RefPartial) {
                    $result.Status = "skip"
                    $result.Error = "Reference partial output; compare mismatch treated as skip."
                    $result.MismatchSeverity = ""
                    $result.MismatchReason = ""
                    $result.SuppressedLikelyInaudible = $false
                }
                else {
                $classification = Classify-Mismatch -ourDiff $cmp.OurDiff -refDiff $cmp.RefDiff -ourPlan $ourComparePlan -refPlan $refPlan
                $result.MismatchSeverity = $classification.Severity
                $result.MismatchReason = $classification.Reason
                $isInfiniteTailFinalize = Is-InfiniteLoopTailFinalizeMismatch `
                    -song $song `
                    -treatHighLoopCountAsInfinite $TreatHighLoopCountAsInfinite `
                    -ourEvents $result.OurEvents `
                    -refEvents $result.RefEvents `
                    -ourLastTick $result.OurLastTick `
                    -refLastTick $result.RefLastTick `
                    -firstDiffIndex $result.FirstDiffIndex
                $isReferenceContinuationTail = Is-ReferenceContinuationTailMismatch `
                    -ourEvents $result.OurEvents `
                    -refEvents $result.RefEvents `
                    -ourLastTick $result.OurLastTick `
                    -refLastTick $result.RefLastTick `
                    -firstDiffIndex $result.FirstDiffIndex

                if ($IgnoreInfiniteLoopTailFinalizeMismatch -and $isInfiniteTailFinalize) {
                    $result.Status = "ok"
                    $result.Match = $true
                    $result.SuppressedLikelyInaudible = $true
                    $result.MismatchSeverity = "likely_inaudible"
                    $result.MismatchReason = "infinite_loop_tail_finalize"
                }
                elseif ($IgnoreReferenceContinuationTailMismatch -and $isReferenceContinuationTail) {
                    $result.Status = "ok"
                    $result.Match = $true
                    $result.SuppressedLikelyInaudible = $true
                    $result.MismatchSeverity = "likely_inaudible"
                    $result.MismatchReason = "reference_continuation_tail"
                }
                elseif ($IgnoreMismatchReasons -contains $classification.Reason) {
                    $result.Status = "ok"
                    $result.Match = $true
                    $result.SuppressedLikelyInaudible = $true
                }
                elseif ($IgnoreLikelyInaudibleMismatches -and $classification.IsLikelyInaudible) {
                    $result.Status = "ok"
                    $result.Match = $true
                    $result.SuppressedLikelyInaudible = $true
                }
                else {
                    $result.Status = "mismatch"
                }
                }
            }
            else {
                $result.MismatchSeverity = ""
                $result.MismatchReason = ""
                $result.SuppressedLikelyInaudible = $false
            }
        }
    }
    catch {
        if ($_.Exception -is [System.TimeoutException] -and $result.Status -eq "ok") {
            $result.Status = "timeout"
            $result.Error = $_.Exception.Message
            $result.TimedOut = $true
        }
        if ($result.Status -eq "timeout" -and $TreatTimeoutAsSkip) {
            $result.Status = "skip"
        }
        elseif ($result.Status -ne "skip") {
            $result.Status = "error"
            $result.Error = $_.Exception.ToString()
        }
    }
    finally {
        $stopwatch.Stop()
        $result.DurationMs = [int]$stopwatch.ElapsedMilliseconds
    }

    $results.Add([pscustomobject]$result) | Out-Null
}

$csvPath = Join-Path $OutDir "comparison.csv"
$jsonPath = Join-Path $OutDir "comparison.json"
$summaryPath = Join-Path $OutDir "summary.txt"

$results | Export-Csv -Path $csvPath -NoTypeInformation -Encoding UTF8
$results | ConvertTo-Json -Depth 5 | Set-Content -Path $jsonPath -Encoding UTF8

$matchCount = @($results | Where-Object Match).Count
$mismatchCount = @($results | Where-Object Status -eq "mismatch").Count
$errorCount = @($results | Where-Object Status -eq "error").Count
$timeoutCount = @($results | Where-Object Status -eq "timeout").Count
$timedOutCount = @($results | Where-Object TimedOut).Count
$skipCount = @($results | Where-Object Status -eq "skip").Count
$summary = @(
    "files=$($results.Count)"
    "excludedZeroByte=$excludedZeroByteCount"
    "match=$matchCount"
    "mismatch=$mismatchCount"
    "error=$errorCount"
    "timeout=$timeoutCount"
    "timedOut=$timedOutCount"
    "skip=$skipCount"
    "csv=$csvPath"
    "json=$jsonPath"
)
$summary -join "`n" | Set-Content -Path $summaryPath -Encoding UTF8

Write-Host ""
Write-Host ($summary -join "`n")
