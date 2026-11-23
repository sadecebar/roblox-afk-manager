param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [switch]$Publish,
    [string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot
$localSdk = Join-Path $PSScriptRoot '.dotnet\dotnet.exe'
$sdk = if (Test-Path -LiteralPath $localSdk) { $localSdk } else { 'dotnet' }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_CLI_HOME = Join-Path $PSScriptRoot '.cli'
$env:DOTNET_NOLOGO = '1'
& $sdk restore RobloxAfkManager.sln --configfile NuGet.Config
if ($LASTEXITCODE -ne 0) { throw 'Restore failed. Install the .NET 10 SDK for Windows x64.' }
& $sdk build RobloxAfkManager.sln -c $Configuration --no-restore
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
& $sdk "tests\RobloxAfkManager.Tests\bin\$Configuration\net10.0-windows\RobloxAfkManager.Tests.dll"
if ($LASTEXITCODE -ne 0) { throw 'Regression tests failed.' }
if ($Publish) {
    [xml]$appProject = Get-Content -LiteralPath 'src\RobloxAfkManager.App\RobloxAfkManager.App.csproj'
    $version = [string]$appProject.Project.PropertyGroup.Version
    $packageName = "RobloxAfkManager-v$version-win-x64"
    $destination = if (-not $OutputDirectory) { Join-Path $PSScriptRoot "artifacts\$packageName" }
        elseif ([System.IO.Path]::IsPathRooted($OutputDirectory)) { [System.IO.Path]::GetFullPath($OutputDirectory) }
        else { [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot $OutputDirectory)) }
    & $sdk publish src\RobloxAfkManager.App\RobloxAfkManager.App.csproj -c $Configuration --no-restore --self-contained false -o $destination -p:DebugType=None -p:DebugSymbols=false
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README.md') -Destination $destination
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Run.cmd') -Destination $destination
    # Ship only repository docs; local notes ignored by Git never enter a package.
    $docFiles = @(& git -C $PSScriptRoot ls-files -- docs 2>$null)
    if ($LASTEXITCODE -ne 0 -or $docFiles.Count -eq 0) { $docFiles = @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'docs') -File -Recurse | ForEach-Object { $_.FullName.Substring($PSScriptRoot.Length + 1) }) }
    $packagedDocs = Join-Path $destination 'docs'
    if (Test-Path -LiteralPath $packagedDocs) { Remove-Item -LiteralPath $packagedDocs -Recurse -Force }
    foreach ($file in $docFiles) {
        $target = Join-Path $destination $file
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot $file) -Destination $target
    }
    $archive = "$destination.zip"
    Compress-Archive -Path (Join-Path $destination '*') -DestinationPath $archive -Force
    Write-Output "Runnable application: $destination\RobloxAfkManager.exe"
    Write-Output "Archive: $archive"
}

