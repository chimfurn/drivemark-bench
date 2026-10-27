# Generate SYNTHETIC demo logs for the DriveMark chart module.
# Rationale: we must not run fio against the user's disks during development,
# so this produces data in the EXACT fio-3.37 text-log format
# (see FIO/iolog.c flush_samples(), fio 3.37):
#     time_ms, value, ddir(0=read,1=write), block_size, priority
# The values are deterministic math, not measured performance.
param(
    [string]$OutDir = ''
)
$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($OutDir)) {
    $OutDir = Join-Path (Split-Path $PSScriptRoot -Parent) 'docs\demo_logs'
}
New-Item -ItemType Directory -Force $OutDir | Out-Null

function Write-LogFile {
    param([string]$Path, [System.Collections.Generic.List[string]]$Lines)
    $utf8 = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText($Path, ($Lines -join "`n") + "`n", $utf8)
    Write-Output ("wrote {0} ({1} lines)" -f (Split-Path $Path -Leaf), $Lines.Count)
}

# ---------- Dataset 1: SSD mixed randrw 4K (read ~380 MiB/s, write ~117 MiB/s)
$bw = [System.Collections.Generic.List[string]]::new()
$iops = [System.Collections.Generic.List[string]]::new()
for ($t = 1000; $t -le 60000; $t += 1000) {
    # 5s ramp-up, a dip around 34-37s (background interference), two wobble periods
    $ramp = [Math]::Min(1.0, $t / 5000.0)
    $dip = if ($t -ge 34000 -and $t -le 37000) { 0.78 } else { 1.0 }
    $wobble = 1 + 0.015 * [Math]::Sin($t / 3100.0) + 0.006 * [Math]::Sin($t / 700.0)
    $readBwKiB = [int](380000 * $ramp * $dip * $wobble)
    $readIops = [int]($readBwKiB / 4)
    $writeBwKiB = [int](120000 * $ramp * $dip * (1 + 0.02 * [Math]::Sin($t / 2300.0 + 1.1)))
    $writeIops = [int]($writeBwKiB / 4)
    $bw.Add(("{0}, {1}, 0, 4096, 0" -f $t, $readBwKiB))
    $bw.Add(("{0}, {1}, 1, 4096, 0" -f $t, $writeBwKiB))
    $iops.Add(("{0}, {1}, 0, 4096, 0" -f $t, $readIops))
    $iops.Add(("{0}, {1}, 1, 4096, 0" -f $t, $writeIops))
}
Write-LogFile -Path (Join-Path $OutDir 'ssd-mixed_bw.1.log') -Lines $bw
Write-LogFile -Path (Join-Path $OutDir 'ssd-mixed_iops.1.log') -Lines $iops

# ---------- Dataset 2: HDD sequential write 1M (~150 MiB/s, very low IOPS)
$bw2 = [System.Collections.Generic.List[string]]::new()
$iops2 = [System.Collections.Generic.List[string]]::new()
for ($t = 1000; $t -le 60000; $t += 1000) {
    $ramp = [Math]::Min(1.0, $t / 4000.0)
    $zone = 1 + 0.08 * [Math]::Sin($t / 9000.0)
    $seek = 1 + 0.05 * [Math]::Sin($t / 1300.0)
    $bwKiB = [int](150000 * $ramp * $zone * $seek)
    $iopsVal = [Math]::Max(1, [int]($bwKiB / 1024))
    $bw2.Add(("{0}, {1}, 1, 1048576, 0" -f $t, $bwKiB))
    $iops2.Add(("{0}, {1}, 1, 1048576, 0" -f $t, $iopsVal))
}
Write-LogFile -Path (Join-Path $OutDir 'hdd-write_bw.1.log') -Lines $bw2
Write-LogFile -Path (Join-Path $OutDir 'hdd-write_iops.1.log') -Lines $iops2

Write-Output "DONE"
