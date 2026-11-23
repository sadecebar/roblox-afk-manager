param([string[]]$ExtraSensitiveValues = @(), [switch]$SourceOnly)
$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
Push-Location -LiteralPath $repositoryRoot
try {
    if ($SourceOnly) {
        $files = @(Get-ChildItem -LiteralPath $repositoryRoot -File -Recurse -Force | ForEach-Object {
            $_.FullName.Substring($repositoryRoot.Length + 1).Replace('\', '/')
        } | Where-Object { $_ -notmatch '(^|/)(\.git|\.dotnet|\.cli|\.tools|\.vs|\.vscode|\.idea|\.agents|\.codex|artifacts|bin|obj|node_modules|TestResults|coverage)/' } | Sort-Object -Unique)
        $scope = 'source candidates (before Git initialization)'
    } else {
        & git rev-parse --is-inside-work-tree 2>$null | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Initialize a local Git repository or use -SourceOnly to inspect source candidates.' }
        # Check the repository's rules without relying on personal global exclusions.
        $files = @(& git -c core.quotepath=false -c core.excludesFile= ls-files --cached --others --exclude-standard | Sort-Object -Unique)
        if ($LASTEXITCODE -ne 0) { throw 'Could not enumerate the publishable Git files.' }
        $scope = 'publishable Git files'
    }
    $rules = [ordered]@{
        'personal home path' = '(?i)([A-Z]:[\\/]Users[\\/][^\\/\s]+|/(home|Users)/[^/\s]+/)'
        'private key' = '-----BEGIN (RSA |EC |OPENSSH |DSA )?PRIVATE KEY-----'
        'service token' = '(gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}|sk-proj-[A-Za-z0-9_-]{20,}|AKIA[A-Z0-9]{16})'
        'email address' = '(?i)\b[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}\b'
        'non-example tailnet address' = '(?i)(?<![a-z0-9.-])(?!your-pc\.your-tailnet\.ts\.net\b)[a-z0-9-]+\.[a-z0-9-]+\.ts\.net\b'
    }
    $profileName = if ($env:USERPROFILE) { Split-Path -Leaf $env:USERPROFILE } else { '' }
    $localNames = @($env:USERNAME, $profileName, $env:COMPUTERNAME) + $ExtraSensitiveValues
    $localNames = @($localNames | Where-Object { $_ -and $_.Length -ge 4 -and $_ -notmatch '^(User|Default|Admin|Administrator|SYSTEM|runneradmin)$' } | Sort-Object -Unique)
    $findings = @()
    foreach ($relativePath in $files) {
        $absolutePath = [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot $relativePath))
        if (-not $absolutePath.StartsWith($repositoryRoot + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) { throw 'Candidate escaped the repository.' }
        if (-not (Test-Path -LiteralPath $absolutePath -PathType Leaf)) { continue }
        if ($relativePath -match '(^|/)(artifacts|bin|obj|\.dotnet|\.cli|\.tools|\.agents|\.codex|node_modules|TestResults)/|\.(exe|dll|pdb|obj|lib|exp|zip|log|dmp|pfx|p12|pem|key|snk)$|(^|/)\.env($|\.)|(^|/)(client-names|workspace)\.json($|\.)') {
            $findings += "${relativePath}: generated output or local/private file"
        }
        $bytes = [System.IO.File]::ReadAllBytes($absolutePath)
        $texts = @([System.Text.Encoding]::UTF8.GetString($bytes), [System.Text.Encoding]::Unicode.GetString($bytes))
        foreach ($rule in $rules.GetEnumerator()) {
            if ($texts | Where-Object { $_ -match $rule.Value }) { $findings += "${relativePath}: $($rule.Key)" }
        }
        foreach ($value in $localNames) {
            if ($texts | Where-Object { $_ -match ('(?i)\b' + [regex]::Escape($value) + '\b') }) { $findings += "${relativePath}: machine-specific name or supplied sensitive value" }
        }
    }
    if ($findings.Count -gt 0) {
        $findings | Sort-Object -Unique | Write-Output
        throw 'Repository check failed. Review the listed files; sensitive values were not printed.'
    }
    Write-Output "PASS: $($files.Count) $scope checked; no matching private values or generated outputs."
} finally { Pop-Location }
