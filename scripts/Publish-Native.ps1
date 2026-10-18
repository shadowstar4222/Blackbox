param(
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [string]$Version = '2.0.0-preview.17',
    [string]$FfmpegDirectory = "$env:LOCALAPPDATA\Blackbox\ffmpeg",
    [string]$VcRuntimeDirectory = "$env:WINDIR\System32"
)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw 'Use a new output directory. Existing builds are never overwritten.' }
foreach ($name in 'ffmpeg.exe', 'ffprobe.exe') {
    if (!(Test-Path -LiteralPath (Join-Path $FfmpegDirectory "bin\$name"))) { throw "Missing bundled video tool: $name" }
}
foreach ($name in 'vcruntime140.dll', 'vcruntime140_1.dll', 'msvcp140.dll', 'concrt140.dll') {
    if (!(Test-Path -LiteralPath (Join-Path $VcRuntimeDirectory $name))) { throw "Missing x64 Visual C++ runtime: $name" }
}
& dotnet publish (Join-Path $root 'src\Blackbox.App\Blackbox.App.csproj') -c Release -r win-x64 --self-contained true "-p:Version=$Version" -o $output
if ($LASTEXITCODE -ne 0) { throw 'Application publish failed.' }
$worker = Join-Path $output 'capture'
& dotnet publish (Join-Path $root 'src\Blackbox.Capture.Worker\Blackbox.Capture.Worker.csproj') -c Release -r win-x64 --self-contained true "-p:Version=$Version" -o $worker
if ($LASTEXITCODE -ne 0) { throw 'Recorder publish failed.' }
$tools = New-Item -ItemType Directory -Path (Join-Path $output 'tools\ffmpeg\bin') -Force
foreach ($name in 'ffmpeg.exe', 'ffprobe.exe') {
    Copy-Item -LiteralPath (Join-Path $FfmpegDirectory "bin\$name") -Destination $tools.FullName
}
foreach ($name in 'LICENSE', 'README.txt') {
    Copy-Item -LiteralPath (Join-Path $FfmpegDirectory $name) -Destination (Join-Path $output 'tools\ffmpeg')
}
foreach ($name in 'vcruntime140.dll', 'vcruntime140_1.dll', 'msvcp140.dll', 'concrt140.dll') {
    Copy-Item -LiteralPath (Join-Path $VcRuntimeDirectory $name) -Destination $worker
}
$notices = New-Item -ItemType Directory -Path (Join-Path $output 'licenses') -Force
Copy-Item -LiteralPath (Join-Path $root 'docs\licenses\ScreenRecorderLib.txt') -Destination $notices.FullName
Copy-Item -LiteralPath (Join-Path $root 'docs\licenses\SpeexDSPSharp.txt') -Destination $notices.FullName
Copy-Item -LiteralPath (Join-Path $root 'docs\licenses\SpeexDSP.txt') -Destination $notices.FullName
foreach ($name in 'LICENSE', 'NOTICE') { Copy-Item -LiteralPath (Join-Path $root $name) -Destination $output }
Copy-Item -LiteralPath (Join-Path $root 'docs\NATIVE-RECORDER.txt') -Destination (Join-Path $output 'START-HERE.txt')
Copy-Item -LiteralPath (Join-Path $root 'docs\native-recorder-validation.md') -Destination (Join-Path $output 'VALIDATION.md')
Copy-Item -LiteralPath (Join-Path $root 'docs\WHATS-NEW.txt') -Destination (Join-Path $output 'WHATS-NEW.txt')
Copy-Item -LiteralPath (Join-Path $root 'docs\maintenance-2026-10.md') -Destination (Join-Path $output 'MAINTENANCE.md')
Copy-Item -LiteralPath (Join-Path $root 'docs\product-direction.md') -Destination (Join-Path $output 'PRODUCT-DIRECTION.md')
Copy-Item -LiteralPath (Join-Path $root 'docs\compact-workspace-validation.md') -Destination (Join-Path $output 'UI-VALIDATION.md')
Copy-Item -LiteralPath (Join-Path $root 'docs\playback-optimization-validation.md') -Destination (Join-Path $output 'PLAYBACK-VALIDATION.md')
Copy-Item -LiteralPath (Join-Path $root 'docs\BENCHMARK-20261013.txt') -Destination $output
Copy-Item -LiteralPath (Join-Path $root 'docs\AUDIO-VIDEO-SYNC-20261016.txt') -Destination $output
Copy-Item -LiteralPath (Join-Path $root 'docs\PLAYBACK-TRANSITIONS-20261017.txt') -Destination $output
Copy-Item -LiteralPath (Join-Path $root 'docs\SEAMLESS-PLAYBACK-20261017.txt') -Destination $output
Copy-Item -LiteralPath (Join-Path $root 'docs\RECORDING-SETUP-20261017.txt') -Destination $output
Copy-Item -LiteralPath (Join-Path $root 'docs\UI-CLEANUP-20261017.txt') -Destination $output
Copy-Item -LiteralPath (Join-Path $root 'docs\PANELS-FRAME-STEP-20261017.txt') -Destination $output
Copy-Item -LiteralPath (Join-Path $root 'docs\AUTO-CONTROL-FIX-20261017.txt') -Destination $output
Copy-Item -LiteralPath (Join-Path $root 'docs\SETUP-AUTO-RECORD-20261017.txt') -Destination $output
Copy-Item -LiteralPath (Join-Path $root 'docs\DIAGNOSTICS-SCROLL-20261017.txt') -Destination $output
Copy-Item -LiteralPath (Join-Path $root 'docs\CONTROL-THEME-20261017.txt') -Destination $output
Copy-Item -LiteralPath (Join-Path $root 'docs\TIMELINE-LAYOUT-20261017.txt') -Destination $output
Copy-Item -LiteralPath (Join-Path $root 'docs\RELEASE-2.0.0-preview.17.txt') -Destination $output
if (Test-Path -LiteralPath (Join-Path $output 'Blackbox.Dock.dll')) { throw 'The deferred OBS dock must not be included in the native desktop package.' }
if (Test-Path -LiteralPath (Join-Path $output 'Microsoft.AspNetCore.dll')) { throw 'The native desktop package unexpectedly includes the dock web framework.' }
& (Join-Path $worker 'Blackbox.Capture.Worker.exe') --probe
if ($LASTEXITCODE -ne 0) { throw 'Packaged recorder prerequisite check failed.' }
Write-Output "Native preview built: $output"
