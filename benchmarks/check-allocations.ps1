<#
.SYNOPSIS
    Fails when a benchmark allocates more per operation than its committed baseline.

.DESCRIPTION
    Reads the BenchmarkDotNet JSON reports (run with `--exporters json`) and compares each benchmark's allocated bytes per
    operation with benchmarks/allocation-baseline.json. Allocations are deterministic for these benchmarks, unlike timings on
    shared CI runners, so they make a reliable regression gate for the profiler's hot paths (emit, store, redaction,
    analysis, component instrumentation).

    A benchmark regresses when it allocates more than baseline * (1 + tolerancePercent / 100) + minimumSlackBytes.
    Benchmarks missing from the baseline are reported but do not fail the check; run with -Update to record them.

.PARAMETER Results
    The BenchmarkDotNet results directory (default: BenchmarkDotNet.Artifacts/results).

.PARAMETER Baseline
    The baseline file (default: benchmarks/allocation-baseline.json next to this script).

.PARAMETER Update
    Writes the measured values as the new baseline instead of checking.

.EXAMPLE
    dotnet run -c Release --project benchmarks/BlazorProfiler.Benchmarks -- --job short --filter "*" --exporters json
    pwsh benchmarks/check-allocations.ps1
#>
param(
    [string]$Results = 'BenchmarkDotNet.Artifacts/results',
    [string]$Baseline = (Join-Path $PSScriptRoot 'allocation-baseline.json'),
    [switch]$Update
)

$ErrorActionPreference = 'Stop'

$reports = Get-ChildItem -Path $Results -Filter '*-report-full-compressed.json' -ErrorAction SilentlyContinue
if (-not $reports) {
    $reports = Get-ChildItem -Path $Results -Filter '*-report-full.json' -ErrorAction SilentlyContinue
}

if (-not $reports) {
    Write-Error "No BenchmarkDotNet JSON reports found in '$Results'. Run the benchmarks with --exporters json."
    exit 2
}

$measured = [ordered]@{}
foreach ($report in $reports) {
    $json = Get-Content -Raw -Path $report.FullName | ConvertFrom-Json
    foreach ($benchmark in $json.Benchmarks) {
        if ($null -ne $benchmark.Memory -and $null -ne $benchmark.Memory.BytesAllocatedPerOperation) {
            $measured[$benchmark.FullName] = [long]$benchmark.Memory.BytesAllocatedPerOperation
        }
    }
}

if ($measured.Count -eq 0) {
    Write-Error 'The reports contain no allocation data. The benchmarks need [MemoryDiagnoser].'
    exit 2
}

if ($Update) {
    $existing = if (Test-Path $Baseline) { Get-Content -Raw $Baseline | ConvertFrom-Json } else { $null }
    $document = [ordered]@{
        tolerancePercent  = if ($existing) { $existing.tolerancePercent } else { 10 }
        minimumSlackBytes = if ($existing) { $existing.minimumSlackBytes } else { 64 }
        benchmarks        = [ordered]@{}
    }
    foreach ($name in ($measured.Keys | Sort-Object)) {
        $document.benchmarks[$name] = $measured[$name]
    }

    $document | ConvertTo-Json -Depth 4 | Set-Content -Path $Baseline -Encoding utf8
    Write-Host "Wrote $($measured.Count) baseline value(s) to $Baseline."
    exit 0
}

if (-not (Test-Path $Baseline)) {
    Write-Error "Baseline file not found: $Baseline. Run with -Update to create it."
    exit 2
}

$base = Get-Content -Raw $Baseline | ConvertFrom-Json
$tolerance = [double]$base.tolerancePercent / 100
$slack = [long]$base.minimumSlackBytes
$failures = 0
$rows = foreach ($name in $measured.Keys) {
    $actual = $measured[$name]
    $expected = $base.benchmarks.$name
    if ($null -eq $expected) {
        [pscustomobject]@{ Benchmark = $name; Baseline = '-'; Measured = $actual; Status = 'new (not in baseline)' }
        continue
    }

    $limit = [long][math]::Ceiling([long]$expected * (1 + $tolerance)) + $slack
    $status = if ($actual -gt $limit) { $failures++; "REGRESSION (limit $limit)" } elseif ($actual -lt [long]$expected) { 'improved' } else { 'ok' }
    [pscustomobject]@{ Benchmark = $name; Baseline = [long]$expected; Measured = $actual; Status = $status }
}

$rows | Format-Table -AutoSize | Out-String -Width 220 | Write-Host
if ($failures -gt 0) {
    Write-Host "::error title=Allocation regression::$failures benchmark(s) allocate more than their baseline. If the increase is intended, run benchmarks/check-allocations.ps1 -Update and commit the baseline."
    exit 1
}

Write-Host 'No allocation regressions.'
exit 0
