param([Parameter(Mandatory = $true)][string]$FfmpegPath)
$ErrorActionPreference = 'Stop'
$assets = Join-Path $PSScriptRoot '..\src\Blackbox.App\Assets\RecordingSetup'
New-Item -ItemType Directory -Path $assets -Force | Out-Null
foreach ($variant in @(@{ Width = 1280; Height = 720; Fps = 30; Name = 'moving-720p30.mp4' }, @{ Width = 1920; Height = 1080; Fps = 60; Name = 'moving-1080p60.mp4' })) {
    $source = "testsrc2=size=$($variant.Width)x$($variant.Height):rate=$($variant.Fps):duration=12"
    & $FfmpegPath -hide_banner -y -f lavfi -i $source -an -c:v libx264 -threads 2 -preset veryfast -crf 20 -pix_fmt yuv420p -movflags +faststart (Join-Path $assets $variant.Name)
    if ($LASTEXITCODE -ne 0) { throw "Could not generate $($variant.Name)" }
}
