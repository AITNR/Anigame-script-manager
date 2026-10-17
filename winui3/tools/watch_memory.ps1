<#
.SYNOPSIS
  外部采样 YukinoChan 进程的内存 / 线程 / 句柄曲线，用来判定「是不是泄漏」。

.DESCRIPTION
  程序自己每分钟打一行 [memory] 采样，但它只有瞬时值，看不出趋势；而且它跑在提权
  上下文里，外部工具读不到线程起始地址。这个脚本解决第一件事：把瞬时值连成曲线，
  并给出**线性增长斜率**。

  判读方法（重要，别只看绝对值）：
    · 线程数随时间单调上升 → 线程泄漏。正常应在一个水位上下波动。
    · Private 单调上升且不是台阶式（台阶 = 一次分配一颗大缓冲，正常）
      → 累积型泄漏。
    · 断开全部连接后 Private / 线程应回落到接近连接前的水平；不回落就是没回收。

  为什么不读 GPU committed：Get-Counter 的 GPU 计数器在非提权的 shell 里对提权进程
  经常返回空，脚本会尽力读、读不到就留空，不要把空值当成 0。

.EXAMPLE
  # 常规用法：先连上两路 1080p，再跑 30 分钟
  powershell -ExecutionPolicy Bypass -File winui3\tools\watch_memory.ps1 -DurationMinutes 30

.EXAMPLE
  # 快速确认「断开后有没有回落」：跑 5 分钟，中途手动断开连接
  powershell -ExecutionPolicy Bypass -File winui3\tools\watch_memory.ps1 -DurationMinutes 5 -IntervalSeconds 5
#>
[CmdletBinding()]
param(
    [string]$ProcessName = 'YukinoChan',
    [int]$IntervalSeconds = 15,
    [int]$DurationMinutes = 30,
    [string]$OutFile = ''
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($OutFile)) {
    $stamp = Get-Date -Format 'yyyyMMdd_HHmmss'
    $OutFile = Join-Path (Get-Location) "memory_watch_$stamp.csv"
}

function Get-ProcessSample {
    param([string]$Name)

    # 主控是 requireAdministrator，非提权 shell 读不到 StartTime / 线程起始地址，
    # 但 WS / Private / ThreadCount / HandleCount 这些计数是可读的（实测确认）。
    $procs = @(Get-Process -Name $Name -ErrorAction SilentlyContinue)
    if ($procs.Count -eq 0) { return $null }

    # 多个实例时取占用最大的那个（多画面 / 代理副本都在同一个 exe 名下）。
    $p = $procs | Sort-Object -Property WorkingSet64 -Descending | Select-Object -First 1

    $gpu = ''
    try {
        $counter = Get-Counter '\GPU Process Memory(*)\Local Usage' -ErrorAction Stop
        $match = $counter.CounterSamples | Where-Object { $_.InstanceName -match "pid_$($p.Id)_" }
        if ($match) {
            $sum = ($match | Measure-Object -Property CookedValue -Sum).Sum
            if ($sum -gt 0) { $gpu = [math]::Round($sum / 1MB, 1) }
        }
    }
    catch {
        # GPU 计数器读不到（权限 / 驱动不支持）不是错误，留空即可。
    }

    [pscustomobject]@{
        Time       = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss')
        Pid        = $p.Id
        PrivateMB  = [math]::Round($p.PrivateMemorySize64 / 1MB, 1)
        WorkingMB  = [math]::Round($p.WorkingSet64 / 1MB, 1)
        Threads    = $p.Threads.Count
        Handles    = $p.HandleCount
        GpuLocalMB = $gpu
        Instances  = $procs.Count
    }
}

function Show-Slope {
    param($Rows, [string]$Field, [string]$Unit)

    $values = @($Rows | ForEach-Object { $_.$Field } | Where-Object { $_ -ne '' -and $_ -ne $null })
    if ($values.Count -lt 3) { return }

    $first = [double]$values[0]
    $last = [double]$values[$values.Count - 1]
    $delta = $last - $first
    $minutes = $DurationMinutes
    $perHour = if ($minutes -gt 0) { [math]::Round($delta / $minutes * 60, 1) } else { 0 }

    $peak = ($values | Measure-Object -Maximum).Maximum
    $trough = ($values | Measure-Object -Minimum).Minimum

    "{0,-10} first={1}{2} last={3}{2} peak={4}{2} min={5}{2} delta={6}{2} (per hour: {7}{2})" -f `
        $Field, $first, $Unit, $last, $peak, $trough, $delta, $perHour
}

# ---------------- 主循环 ----------------

$deadline = (Get-Date).AddMinutes($DurationMinutes)
$rows = New-Object System.Collections.ArrayList

Write-Host "Sampling '$ProcessName' every ${IntervalSeconds}s for $DurationMinutes min -> $OutFile"
Write-Host 'Start the RDP connections now, and note the timestamp where they are up.'
Write-Host ''

$header = 'time,pid,private_mb,working_mb,threads,handles,gpu_local_mb,instances'
Set-Content -LiteralPath $OutFile -Value $header -Encoding UTF8

while ((Get-Date) -lt $deadline) {
    $sample = Get-ProcessSample -Name $ProcessName
    if ($null -eq $sample) {
        Write-Host "[$(Get-Date -Format 'HH:mm:ss')] no '$ProcessName' process found"
    }
    else {
        [void]$rows.Add($sample)
        $line = '{0},{1},{2},{3},{4},{5},{6},{7}' -f `
            $sample.Time, $sample.Pid, $sample.PrivateMB, $sample.WorkingMB,
            $sample.Threads, $sample.Handles, $sample.GpuLocalMB, $sample.Instances
        Add-Content -LiteralPath $OutFile -Value $line -Encoding UTF8

        Write-Host ("[{0}] pid={1} private={2}MB ws={3}MB threads={4} handles={5} gpu={6}MB" -f `
            (Get-Date -Format 'HH:mm:ss'), $sample.Pid, $sample.PrivateMB,
            $sample.WorkingMB, $sample.Threads, $sample.Handles,
            $(if ($sample.GpuLocalMB -eq '') { 'n/a' } else { $sample.GpuLocalMB }))
    }

    Start-Sleep -Seconds $IntervalSeconds
}

# ---------------- 摘要 ----------------

if ($rows.Count -eq 0) {
    Write-Host ''
    Write-Host 'No samples collected.'
    exit 1
}

Write-Host ''
Write-Host '--- summary ---'
Show-Slope -Rows $rows -Field 'PrivateMB' -Unit 'MB'
Show-Slope -Rows $rows -Field 'WorkingMB' -Unit 'MB'
Show-Slope -Rows $rows -Field 'Threads' -Unit ''
Show-Slope -Rows $rows -Field 'Handles' -Unit ''
if ($rows[0].GpuLocalMB -ne '') {
    Show-Slope -Rows $rows -Field 'GpuLocalMB' -Unit 'MB'
}

Write-Host ''
Write-Host 'Read it like this:'
Write-Host '  threads rising over time        -> thread leak (the usual suspect)'
Write-Host '  private rising, step-shaped     -> one-off buffer alloc, normal'
Write-Host '  private rising, smooth          -> accumulation, investigate'
Write-Host '  no drop after disconnecting     -> teardown does not release'
Write-Host ''
Write-Host "CSV: $OutFile"
