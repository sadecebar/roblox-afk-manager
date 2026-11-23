param([ValidateSet('Debug', 'Release')][string]$Configuration = 'Release', [switch]$Package, [switch]$RealPreviews, [switch]$Pointer, [switch]$AccountNames)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $projectRoot
$runner = Join-Path $PSScriptRoot "RobloxAfkManager.Tests\bin\$Configuration\net10.0-windows\RobloxAfkManager.Tests.exe"
if (-not (Test-Path -LiteralPath $runner)) { throw 'Run .\build.ps1 first.' }
if (Test-Path -LiteralPath (Join-Path $projectRoot '.dotnet\dotnet.exe')) { $env:DOTNET_ROOT_X64 = Join-Path $projectRoot '.dotnet' }
$testArgs = @('--remote-http', '--ui', '--native')
if ($Package) { $testArgs += '--package' }
if ($RealPreviews) { $testArgs += '--real-previews' }
if ($Pointer) { $testArgs += '--drag' }
if ($AccountNames) { $testArgs += '--name-native', '--name-network' }
& $runner @testArgs
if ($LASTEXITCODE -ne 0) { throw 'Desktop smoke tests failed.' }
