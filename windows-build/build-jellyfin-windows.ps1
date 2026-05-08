[CmdletBinding()]
param(
    [string]$ServerRepo  = (Split-Path $PSScriptRoot -Parent),
    [string]$WebRepo     = (Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) 'jellyfin-web'),
    [string]$OutputRoot  = (Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) 'build'),
    [ValidateSet('x64','arm64')][string]$Architecture = 'x64',
    [switch]$SkipWeb,
    [switch]$SkipServer,
    [switch]$SkipFFmpeg,
    [switch]$NoZip,
    [string]$VersionSuffix,
    [string]$FFmpegVersion = '7.x'
)

$ErrorActionPreference = 'Stop'
$ProgressPreference    = 'SilentlyContinue'

$Rid       = "win-$Architecture"
$StageDir  = Join-Path $OutputRoot 'jellyfin'
$WebDist   = Join-Path $WebRepo 'dist'

if (-not $VersionSuffix) {
    $tag = & git -C $ServerRepo describe --tags --abbrev=0 2>$null
    $VersionSuffix = if ($tag) { $tag.TrimStart('v') } else { 'dev' }
}

Write-Host "==> Output:        $StageDir"
Write-Host "==> Architecture:  $Rid"
Write-Host "==> Version:       $VersionSuffix"

if (-not $SkipServer) {
    if (Test-Path $StageDir) {
        Remove-Item -Recurse -Force $StageDir
    }
}
New-Item -ItemType Directory -Force -Path $StageDir | Out-Null

if (-not $SkipServer) {
    Write-Host "==> dotnet publish Jellyfin.Server"
    & dotnet publish (Join-Path $ServerRepo 'Jellyfin.Server\Jellyfin.Server.csproj') `
        -c Release -r $Rid --self-contained `
        -p:UseAppHost=true -p:DebugSymbols=false -p:DebugType=none -p:GenerateDocumentationFile=false `
        -o $StageDir --nologo
    if ($LASTEXITCODE -ne 0) { throw "Server publish failed" }

    Write-Host "==> dotnet publish Jellyfin.ProxyConfig"
    & dotnet publish (Join-Path $ServerRepo 'Jellyfin.ProxyConfig\Jellyfin.ProxyConfig.csproj') `
        -c Release -r $Rid --self-contained false `
        -p:UseAppHost=true -p:DebugSymbols=false -p:DebugType=none `
        -o $StageDir --nologo
    if ($LASTEXITCODE -ne 0) { throw "ProxyConfig publish failed" }
}

if (-not $SkipWeb) {
    Write-Host "==> npm ci (jellyfin-web)"
    Push-Location $WebRepo
    try {
        $env:NPM_CONFIG_ENGINE_STRICT = 'false'
        & npm ci --no-audit --no-fund
        if ($LASTEXITCODE -ne 0) { throw "npm ci failed" }

        Write-Host "==> npm run build:production"
        & npm run build:production
        if ($LASTEXITCODE -ne 0) { throw "web build failed" }
    } finally {
        Pop-Location
    }
}

$WebTarget = Join-Path $StageDir 'jellyfin-web'
if (Test-Path $WebTarget) { Remove-Item -Recurse -Force $WebTarget }
Copy-Item $WebDist $WebTarget -Recurse -Force

if (-not $SkipFFmpeg -and $Architecture -eq 'x64') {
    Write-Host "==> Resolving jellyfin-ffmpeg URL (latest-$FFmpegVersion)"
    $listUrl = "https://repo.jellyfin.org/?path=/ffmpeg/windows/latest-$FFmpegVersion/win64"
    $listing = (Invoke-WebRequest -Uri $listUrl -UseBasicParsing).Content
    $match   = [regex]::Match($listing, "/files/ffmpeg/windows/latest-$FFmpegVersion/win64/(jellyfin-ffmpeg_[^']+_portable_win64-clang-gpl\.zip)")
    if (-not $match.Success) { throw "Could not locate jellyfin-ffmpeg zip on repo.jellyfin.org" }
    $ffmpegUrl = "https://repo.jellyfin.org$($match.Value)"
    $ffmpegZip = Join-Path $env:TEMP $match.Groups[1].Value
    Write-Host "==> Downloading $ffmpegUrl"
    Invoke-WebRequest -Uri $ffmpegUrl -OutFile $ffmpegZip -UseBasicParsing
    $ffmpegExtract = Join-Path $env:TEMP "jellyfin-ffmpeg-extract"
    if (Test-Path $ffmpegExtract) { Remove-Item -Recurse -Force $ffmpegExtract }
    Expand-Archive -Path $ffmpegZip -DestinationPath $ffmpegExtract -Force
    $contentRoot = $ffmpegExtract
    $children = Get-ChildItem $ffmpegExtract
    if ($children.Count -eq 1 -and $children[0].PSIsContainer) {
        $contentRoot = $children[0].FullName
    }
    Write-Host "==> Copying ffmpeg binaries into staging"
    Get-ChildItem $contentRoot -File | Copy-Item -Destination $StageDir -Force
    Remove-Item -Recurse -Force $ffmpegExtract
    Remove-Item -Force $ffmpegZip
} elseif (-not $SkipFFmpeg) {
    Write-Warning "FFmpeg fetch only supported for x64; skipping for $Architecture"
}

$serviceScriptSrc = Join-Path $PSScriptRoot 'service-control.ps1'
if (Test-Path $serviceScriptSrc) {
    Copy-Item $serviceScriptSrc (Join-Path $StageDir 'service-control.ps1') -Force
} else {
    Write-Warning "service-control.ps1 not found at $serviceScriptSrc"
}

if (-not $NoZip) {
    $zipPath = Join-Path $OutputRoot ("jellyfin_{0}-proxy_{1}.zip" -f $VersionSuffix, $Architecture)
    Write-Host "==> Compress to $zipPath"
    if (Test-Path $zipPath) { Remove-Item -Force $zipPath }
    Compress-Archive -Path $StageDir -DestinationPath $zipPath -CompressionLevel Optimal
    Write-Host "==> Done: $zipPath"
} else {
    Write-Host "==> Done (no zip): $StageDir"
}
