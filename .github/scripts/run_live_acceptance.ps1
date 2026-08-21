param(
    [string]$UnityPath,
    [switch]$StartLocalStack,
    [switch]$ResetLocalDatabase
)

$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$cliVersion = "2.115.0"

function Invoke-SupabaseCli {
    param([string[]]$Arguments)

    $npx = Get-Command npx.cmd -ErrorAction SilentlyContinue
    if (-not $npx) {
        throw "Node.js and npx are required to run the local Supabase CLI."
    }

    $output = & $npx.Source -y ("supabase@" + $cliVersion) @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) {
        $output |
            Where-Object { $_ -notmatch '(?i)(anon|service.role|publishable|secret).{0,20}key' } |
            ForEach-Object {
                $_ -replace 'eyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+', '[redacted token]' `
                   -replace 'sb_(publishable|secret)_[A-Za-z0-9_-]+', '[redacted key]'
            } |
            Write-Output
        throw "Supabase CLI failed: supabase $($Arguments -join ' ')"
    }
}

function Import-LocalSupabaseEnvironment {
    Test-ContainerRuntime | Out-Null
    $status = & npx.cmd -y ("supabase@" + $cliVersion) status -o env 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "The local Supabase stack is not running. Use -StartLocalStack or set the SUPABASE_TEST_* variables for an isolated test project."
    }

    $values = @{}
    foreach ($line in $status) {
        if ($line -match '^([A-Z0-9_]+)="?(.*?)"?$') {
            $values[$matches[1]] = $matches[2].TrimEnd('"')
        }
    }
    if (-not $values.ContainsKey("API_URL") -or -not $values.ContainsKey("ANON_KEY")) {
        throw "Supabase CLI status did not provide API_URL and ANON_KEY."
    }

    $env:SUPABASE_TEST_URL = $values["API_URL"]
    $env:SUPABASE_TEST_PUBLISHABLE_KEY = $values["ANON_KEY"]
}

function Test-ContainerRuntime {
    if (Get-Command docker -ErrorAction SilentlyContinue) { return $true }
    if (Get-Command podman -ErrorAction SilentlyContinue) { return $true }

    $installedPodman = Join-Path $env:ProgramFiles "RedHat\Podman\podman.exe"
    if (Test-Path -LiteralPath $installedPodman) {
        $podmanDirectory = Split-Path -Parent $installedPodman
        $env:PATH = $podmanDirectory + [IO.Path]::PathSeparator + $env:PATH
        return $true
    }

    # Podman Machine and Docker Desktop both expose this Docker-compatible pipe on Windows.
    return Test-Path -LiteralPath "\\.\pipe\docker_engine"
}

Push-Location $repoRoot
try {
    if ($StartLocalStack -or $ResetLocalDatabase) {
        if (-not (Test-ContainerRuntime)) {
            throw "A Docker-compatible container runtime is required for local Supabase."
        }
    }
    if ($StartLocalStack) {
        Invoke-SupabaseCli -Arguments @("start")
    }
    if ($ResetLocalDatabase) {
        Invoke-SupabaseCli -Arguments @("db", "reset", "--local")
    }

    if ([string]::IsNullOrWhiteSpace($env:SUPABASE_TEST_URL) -or
        [string]::IsNullOrWhiteSpace($env:SUPABASE_TEST_PUBLISHABLE_KEY)) {
        Import-LocalSupabaseEnvironment
    }

    if (-not $UnityPath) {
        $versionLine = Get-Content (Join-Path $repoRoot "ProjectSettings\ProjectVersion.txt") |
            Where-Object { $_ -like "m_EditorVersion:*" } | Select-Object -First 1
        $version = ($versionLine -split ':', 2)[1].Trim()
        $UnityPath = "C:\Program Files\Unity\Hub\Editor\$version\Editor\Unity.exe"
    }
    if (-not (Test-Path -LiteralPath $UnityPath)) {
        throw "Unity was not found at $UnityPath. Pass -UnityPath with the installed editor executable."
    }
    if (Test-Path -LiteralPath (Join-Path $repoRoot "Temp\UnityLockfile")) {
        throw "Close the Unity Editor before running live acceptance."
    }

    $outputDirectory = Join-Path $repoRoot ".utmp"
    New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null
    $resultsPath = Join-Path $outputDirectory "live-acceptance.xml"
    $logPath = Join-Path $outputDirectory "live-acceptance.log"
    $arguments = @(
        "-runTests",
        "-batchmode",
        "-projectPath", ('"' + $repoRoot + '"'),
        "-testPlatform", "PlayMode",
        "-testFilter", "Supabase.Unity.LiveTests.SupabaseLiveAcceptanceTests",
        "-testResults", ('"' + $resultsPath + '"'),
        "-logFile", ('"' + $logPath + '"')
    )

    $process = Start-Process -FilePath $UnityPath -ArgumentList $arguments -PassThru -WindowStyle Hidden
    while (-not $process.HasExited) {
        Start-Sleep -Seconds 1
        $process.Refresh()
    }
    if (-not (Test-Path -LiteralPath $resultsPath)) {
        if (Test-Path -LiteralPath $logPath) {
            Get-Content -LiteralPath $logPath -Tail 100
        }
        throw "Unity produced no live acceptance results (exit $($process.ExitCode))."
    }

    [xml]$document = Get-Content -Raw -LiteralPath $resultsPath
    $run = $document.'test-run'
    $total = [int]$run.total
    $passed = [int]$run.passed
    $failed = [int]$run.failed
    $skipped = [int]$run.skipped + [int]$run.inconclusive
    if ($total -eq 0 -or $failed -gt 0 -or $skipped -gt 0 -or $passed -ne $total) {
        Select-Xml -Xml $document -XPath '//test-case[@result="Failed"]' |
            ForEach-Object { Write-Output $_.Node.fullname }
        throw "Live acceptance did not pass: total=$total passed=$passed failed=$failed skipped=$skipped."
    }

    Write-Output "Live Supabase acceptance passed: $passed/$total PlayMode test."
    Write-Output "Results: $resultsPath"
}
finally {
    Pop-Location
}
