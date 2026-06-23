param(
    [switch]$SkipWebBuild,
    [switch]$IncludeWebBuild,
    [switch]$IncludeWebTests
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot

function Invoke-Step {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][scriptblock]$Script
    )

    Write-Host ""
    Write-Host "== $Name ==" -ForegroundColor Cyan
    & $Script
}

Push-Location $repo
try {
    Invoke-Step "Core typecheck" {
        Push-Location (Join-Path $repo "sir-thaddeus-core")
        try { npm run typecheck } finally { Pop-Location }
    }

    Invoke-Step "Health Pack typecheck" {
        Push-Location (Join-Path $repo "thaddeus-health-pack")
        try { npm run typecheck } finally { Pop-Location }
    }

    Invoke-Step "Core tests" {
        Push-Location (Join-Path $repo "sir-thaddeus-core")
        try { npm test } finally { Pop-Location }
    }

    Invoke-Step "Health Pack tests" {
        Push-Location (Join-Path $repo "thaddeus-health-pack")
        try { npm test } finally { Pop-Location }
    }

    Invoke-Step "Runtime module tests" {
        dotnet test (Join-Path $repo "tests/runtime/Thaddeus.Runtime.Tests.csproj") `
            -p:SkipWebBuild=true `
            --filter ModuleRuntimeServiceTests
    }

    if ($IncludeWebBuild -and -not $SkipWebBuild) {
        Invoke-Step "Web build" {
            Push-Location (Join-Path $repo "web")
            try { npm run build } finally { Pop-Location }
        }
    }

    if ($IncludeWebTests) {
        Invoke-Step "Web Modules e2e" {
            Push-Location (Join-Path $repo "web")
            try { npm run test:e2e -- tests/e2e/modules.smoke.spec.ts } finally { Pop-Location }
        }
    }

    Invoke-Step "Runtime API smoke" {
        dotnet build (Join-Path $repo "src/Thaddeus.Runtime/Thaddeus.Runtime.csproj") -p:SkipWebBuild=true

        $temp = Join-Path ([System.IO.Path]::GetTempPath()) ("thaddeus-module-smoke-" + [guid]::NewGuid().ToString("N"))
        New-Item -ItemType Directory -Force -Path $temp | Out-Null
        $lock = Join-Path $temp "runtime.lock.json"
        $healthData = Join-Path $temp "health-pack"
        New-Item -ItemType Directory -Force -Path $healthData | Out-Null
        $envNames = @(
            "HEALTH_DATA_PROVIDER",
            "HEALTH_STORE_PATH",
            "HEALTH_PROVIDER_CONFIG_PATH",
            "HEALTH_TOKEN_STORE_PATH",
            "HEALTH_AUDIT_PATH",
            "GOOGLE_HEALTH_CLIENT_ID",
            "GOOGLE_HEALTH_CLIENT_SECRET",
            "GOOGLE_HEALTH_ACCESS_TOKEN",
            "GOOGLE_HEALTH_REFRESH_TOKEN"
        )
        $oldEnv = @{}
        foreach ($name in $envNames) {
            $oldEnv[$name] = [Environment]::GetEnvironmentVariable($name, "Process")
        }
        $proc = $null
        try {
            $env:HEALTH_DATA_PROVIDER = "mock"
            $env:HEALTH_STORE_PATH = Join-Path $healthData "health-store.json"
            $env:HEALTH_PROVIDER_CONFIG_PATH = Join-Path $healthData "provider-config.json"
            $env:HEALTH_TOKEN_STORE_PATH = Join-Path $healthData "provider-tokens.local.json"
            $env:HEALTH_AUDIT_PATH = Join-Path $healthData "health-audit.jsonl"
            Remove-Item Env:\GOOGLE_HEALTH_CLIENT_ID -ErrorAction SilentlyContinue
            Remove-Item Env:\GOOGLE_HEALTH_CLIENT_SECRET -ErrorAction SilentlyContinue
            Remove-Item Env:\GOOGLE_HEALTH_ACCESS_TOKEN -ErrorAction SilentlyContinue
            Remove-Item Env:\GOOGLE_HEALTH_REFRESH_TOKEN -ErrorAction SilentlyContinue

            $proc = Start-Process `
                -FilePath "dotnet" `
                -ArgumentList @("run", "--no-build", "--project", "src/Thaddeus.Runtime/Thaddeus.Runtime.csproj", "--", "--test-mode", "--lock-file=$lock") `
                -WorkingDirectory $repo `
                -PassThru `
                -WindowStyle Hidden

            $deadline = [DateTimeOffset]::UtcNow.AddSeconds(45)
            while (-not (Test-Path $lock)) {
                if ($proc.HasExited) { throw "Runtime exited before writing lock file." }
                if ([DateTimeOffset]::UtcNow -gt $deadline) { throw "Timed out waiting for runtime lock file." }
                Start-Sleep -Milliseconds 250
            }

            $meta = Get-Content $lock -Raw | ConvertFrom-Json
            $base = "http://127.0.0.1:$($meta.port)"
            $headers = @{
                Authorization = "Bearer $($meta.token)"
                "X-Thaddeus-Token" = "$($meta.token)"
            }

            $modules = Invoke-RestMethod -Method Get -Uri "$base/api/modules" -Headers $headers
            $health = @($modules.modules | Where-Object { $_.id -eq "com.thaddeus.health" })[0]
            if (-not $health) { throw "Health Pack was not visible through /api/modules." }

            Invoke-RestMethod -Method Post -Uri "$base/api/modules/com.thaddeus.health/approve" -Headers $headers | Out-Null

            $jsonHeaders = $headers.Clone()
            $jsonHeaders["Content-Type"] = "application/json"
            $body = @{ arguments = @{} } | ConvertTo-Json -Depth 8
            $mockConfigBody = @{
                arguments = @{
                    selectedProvider = "mock"
                }
            } | ConvertTo-Json -Depth 8

            $provider = Invoke-RestMethod `
                -Method Post `
                -Uri "$base/api/modules/com.thaddeus.health/tools/health.provider_status/invoke" `
                -Headers $jsonHeaders `
                -Body $body
            if (-not $provider.ok) { throw "provider_status did not return ok." }

            $secretStore = Invoke-RestMethod `
                -Method Post `
                -Uri "$base/api/modules/com.thaddeus.health/tools/health.secret_store_status/invoke" `
                -Headers $jsonHeaders `
                -Body $body
            if (-not $secretStore.ok -or [string]::IsNullOrWhiteSpace($secretStore.json.backend)) {
                throw "secret_store_status did not return a backend."
            }

            $setProvider = Invoke-RestMethod `
                -Method Post `
                -Uri "$base/api/modules/com.thaddeus.health/tools/health.set_provider_config/invoke" `
                -Headers $jsonHeaders `
                -Body $mockConfigBody
            if (-not $setProvider.ok) { throw "set_provider_config did not return ok." }

            $backfillBody = @{
                arguments = @{
                    days = 3
                    throughDate = "2026-06-03"
                }
            } | ConvertTo-Json -Depth 8
            $backfill = Invoke-RestMethod `
                -Method Post `
                -Uri "$base/api/modules/com.thaddeus.health/tools/health.backfill/invoke" `
                -Headers $jsonHeaders `
                -Body $backfillBody
            if (-not $backfill.ok -or $backfill.json.snapshotsStored -lt 3) {
                throw "mock backfill did not store expected snapshots."
            }

            $brief = Invoke-RestMethod `
                -Method Post `
                -Uri "$base/api/modules/com.thaddeus.health/tools/health.get_morning_strategy_brief/invoke" `
                -Headers $jsonHeaders `
                -Body $body
            if (-not $brief.ok -or [string]::IsNullOrWhiteSpace($brief.content)) {
                throw "morning_strategy_brief did not return content."
            }

            $auditBody = @{
                arguments = @{
                    limit = 20
                }
            } | ConvertTo-Json -Depth 8
            $providerAudit = Invoke-RestMethod `
                -Method Post `
                -Uri "$base/api/modules/com.thaddeus.health/tools/health.provider_audit_events/invoke" `
                -Headers $jsonHeaders `
                -Body $auditBody
            if (-not $providerAudit.ok -or -not $providerAudit.content.Contains("health.brief_generated")) {
                throw "provider audit events did not include expected Health Pack activity."
            }

            $threadBody = @{ title = "Module verification" } | ConvertTo-Json -Depth 4
            $thread = Invoke-RestMethod -Method Post -Uri "$base/api/threads" -Headers $jsonHeaders -Body $threadBody
            $messageBody = @{ text = "Give me my morning health brief" } | ConvertTo-Json -Depth 4
            Invoke-RestMethod `
                -Method Post `
                -Uri "$base/api/threads/$($thread.id)/messages" `
                -Headers $jsonHeaders `
                -Body $messageBody | Out-Null

            $assistantText = ""
            $deadline2 = [DateTimeOffset]::UtcNow.AddSeconds(60)
            while ([DateTimeOffset]::UtcNow -lt $deadline2) {
                Start-Sleep -Milliseconds 500
                $currentThread = Invoke-RestMethod -Method Get -Uri "$base/api/threads/$($thread.id)" -Headers $headers
                $assistant = @($currentThread.messages | Where-Object { $_.role -eq "Assistant" })[-1]
                if ($assistant -and -not [string]::IsNullOrWhiteSpace($assistant.text)) {
                    $assistantText = $assistant.text
                    break
                }
            }
            if ($assistantText -notmatch "morning health brief|Readiness") {
                throw "chat routing smoke did not produce a health brief response."
            }

            Write-Host "Health Pack smoke passed through runtime API." -ForegroundColor Green
        }
        finally {
            if ($proc -and -not $proc.HasExited) {
                try { Stop-Process -Id $proc.Id -Force } catch { }
            }
            try { Remove-Item -LiteralPath $temp -Recurse -Force } catch { }
            foreach ($name in $envNames) {
                if ($null -eq $oldEnv[$name]) {
                    [Environment]::SetEnvironmentVariable($name, $null, "Process")
                } else {
                    [Environment]::SetEnvironmentVariable($name, [string]$oldEnv[$name], "Process")
                }
            }
        }
    }
}
finally {
    Pop-Location
}
