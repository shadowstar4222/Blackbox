param(
    [Parameter(Mandatory = $true)][string]$PackageDirectory,
    [Parameter(Mandatory = $true)][string]$CaptureMatrixDirectory,
    [Parameter(Mandatory = $true)][string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$package = (Resolve-Path -LiteralPath $PackageDirectory).Path
$capture = (Resolve-Path -LiteralPath $CaptureMatrixDirectory).Path
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw 'Use a fresh playback matrix directory.' }
[IO.Directory]::CreateDirectory($output) | Out-Null
$previousProfile = $env:BLACKBOX_DATA_DIRECTORY
try {
    foreach ($name in '720p30', '1080p60', '1440p60', '4k30') {
        $directory = Join-Path $output $name
        [IO.Directory]::CreateDirectory($directory) | Out-Null
        $segments = @(Get-ChildItem -LiteralPath (Join-Path $capture "$name-r1") -Filter '*.mkv' | Sort-Object Name)
        if ($segments.Count -eq 0 -or $segments.Name -like '*.active.mkv') { throw "Missing finalized test input: $name" }
        $concat = Join-Path $directory 'source.ffconcat'
        $lines = foreach ($repeat in 1..6) {
            foreach ($segment in $segments) { "file '$($segment.FullName.Replace('\','/').Replace("'", "'\''"))'" }
        }
        $lines | Set-Content -LiteralPath $concat -Encoding utf8
        $source = Join-Path $directory 'source.mkv'
        & (Join-Path $package 'tools/ffmpeg/bin/ffmpeg.exe') -v error -n -f concat -safe 0 -i $concat -f lavfi -i 'anullsrc=r=48000:cl=stereo' -t 120 -map 0:v:0 -map 1:a:0 -c:v copy -c:a aac -b:a 160k $source
        if ($LASTEXITCODE -ne 0) { throw "Playback source remux failed: $name" }
        $config = Join-Path $directory 'config.json'
        @{ Source=$source; Output=(Join-Path $directory 'results.json'); SampleSeconds=15;
            OpenCloseCycles=$(if ($name -eq '1080p60') { 30 } else { 2 }) } |
            ConvertTo-Json | Set-Content -LiteralPath $config -Encoding utf8
        $env:BLACKBOX_DATA_DIRECTORY = Join-Path $directory 'profile'
        $process = Start-Process -FilePath (Join-Path $package 'Blackbox.App.exe') -ArgumentList @('--performance-benchmark', ('"'+$config+'"')) -WindowStyle Hidden -PassThru
        try {
            if (!$process.WaitForExit(180000)) { $process.Kill($true); throw "Playback benchmark timed out: $name" }
            if ($process.ExitCode -ne 0) { throw "Playback benchmark failed: $name. Inspect the isolated profile logs." }
        }
        finally { $process.Dispose() }
        Write-Output "PASS: $name playback and player ownership checks."
    }
}
finally { $env:BLACKBOX_DATA_DIRECTORY = $previousProfile }
