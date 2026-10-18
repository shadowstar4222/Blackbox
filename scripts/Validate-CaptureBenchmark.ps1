param(
    [Parameter(Mandatory = $true)][string]$InputDirectory,
    [Parameter(Mandatory = $true)][string]$FfmpegDirectory,
    [Parameter(Mandatory = $true)][string]$Output
)
$ErrorActionPreference = 'Stop'
$inputRoot = (Resolve-Path -LiteralPath $InputDirectory).Path
$ffmpeg = Join-Path $FfmpegDirectory 'ffmpeg.exe'
$ffprobe = Join-Path $FfmpegDirectory 'ffprobe.exe'
if (Get-ChildItem -LiteralPath $inputRoot -Filter '*.active.mkv' -Recurse) { throw 'Unfinalized capture files remain.' }
$files = @(Get-ChildItem -LiteralPath $inputRoot -Filter '*.mkv' -Recurse)
if ($files.Count -eq 0) { throw 'No recordings to validate.' }
$results = [Collections.Generic.List[object]]::new()
$outputPath = [IO.Path]::GetFullPath($Output)
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($outputPath)) | Out-Null
foreach ($file in $files) {
    $probeText = & $ffprobe -v error -count_frames -show_entries 'stream=codec_type,codec_name,width,height,r_frame_rate,avg_frame_rate,nb_read_frames,sample_rate,channels:format=duration' -of json $file.FullName
    if ($LASTEXITCODE -ne 0) { throw "Probe failed: $($file.FullName)" }
    $probe = ($probeText -join "`n") | ConvertFrom-Json
    $video = @($probe.streams | Where-Object codec_type -eq video)
    if ($video.Count -ne 1 -or [long]$video[0].nb_read_frames -le 0) { throw "Missing video frames: $($file.FullName)" }
    $log = $outputPath + '.decode.log'
    $hashLines = & $ffmpeg -nostdin -hide_banner -v info -xerror -threads 2 -i $file.FullName -map 0:v:0 -vf 'scale=64:36,blackdetect=d=1:pix_th=0.1,fps=2' -an -f framemd5 - 2> $log
    if ($LASTEXITCODE -ne 0) { throw "Decode failed: $($file.FullName). See $log" }
    if (Select-String -LiteralPath $log -SimpleMatch 'black_start:') { throw "Unexpected black video: $($file.FullName)" }
    $hashes = @($hashLines | Where-Object { $_ -match '^0,' } | ConvertFrom-Csv -Header stream,dts,pts,duration,size,hash | Select-Object -ExpandProperty hash -Unique)
    $duration = [double]::Parse($probe.format.duration, [Globalization.CultureInfo]::InvariantCulture)
    if ($duration -gt 1.5 -and $hashes.Count -lt 2) { throw "Frozen test-pattern video: $($file.FullName)" }
    $audio = @($probe.streams | Where-Object codec_type -eq audio)
    if ($audio.Count -gt 0) {
        & $ffmpeg -nostdin -v error -xerror -threads 2 -i $file.FullName -vn -f null - 2> $log
        if ($LASTEXITCODE -ne 0) { throw "Audio decode failed: $($file.FullName)" }
    }
    $results.Add([pscustomobject]@{ File = $file.FullName; DurationSeconds = $duration;
        Width = $video[0].width; Height = $video[0].height; FrameRate = $video[0].avg_frame_rate;
        Frames = [long]$video[0].nb_read_frames; DistinctSampledFrames = $hashes.Count; Audio = $audio; DecodePassed = $true })
    $results | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $outputPath -Encoding utf8
}
Write-Output "PASS: $($results.Count) finalized files probed, decoded, and checked for black/frozen test-pattern video."
