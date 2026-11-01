param([string]$UserSettingsPath, [string]$OutputDirectory = (Join-Path $PSScriptRoot 'dist'))
$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    $OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
    New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
    if (!(Test-Path -LiteralPath 'LumaBar.exe')) { throw 'Run build.ps1 first.' }
    $distribution = Join-Path $OutputDirectory 'LumaBar-0.6.2-preview-x64'
    New-Item -ItemType Directory -Path $distribution -Force | Out-Null
    foreach ($name in @('LumaBar.exe', 'LumaBar.exe.config', 'settings.json', 'README.md', 'LICENSE', 'LICENSE-LumaBar-MIT', 'THIRD_PARTY_NOTICES.md', 'VALIDATION.md')) {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination $distribution -Force
    }
    if ($UserSettingsPath) {
        Copy-Item -LiteralPath $UserSettingsPath -Destination (Join-Path $distribution 'settings.json') -Force
    }
    New-Item -ItemType Directory -Path (Join-Path $distribution 'native') -Force | Out-Null
    Copy-Item -LiteralPath 'native/ExplorerTAP.dll', 'native/README.md' -Destination (Join-Path $distribution 'native') -Force
    Copy-Item -LiteralPath 'docs' -Destination $distribution -Recurse -Force
    Compress-Archive -Path $distribution -DestinationPath (Join-Path $OutputDirectory 'LumaBar-0.6.2-preview-x64.zip') -Force

    # Include exact upstream source beside the binary, without nested Git metadata.
    $source = Join-Path $OutputDirectory 'LumaBar-0.6.2-source'
    New-Item -ItemType Directory -Path $source -Force | Out-Null
    foreach ($name in @('src', 'build.ps1', 'package.ps1', 'LumaBar.csproj', 'LumaBar.exe.config', 'settings.json', 'README.md', 'LICENSE', 'LICENSE-LumaBar-MIT', 'THIRD_PARTY_NOTICES.md', 'VALIDATION.md', 'docs')) {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination $source -Recurse -Force
    }
    New-Item -ItemType Directory -Path (Join-Path $source 'tests') -Force | Out-Null
    Copy-Item -Path 'tests/*.cs' -Destination (Join-Path $source 'tests') -Force
    New-Item -ItemType Directory -Path (Join-Path $source 'native') -Force | Out-Null
    Copy-Item -LiteralPath 'native/README.md', 'native/fetch.ps1', 'native/ExplorerTAP.dll' -Destination (Join-Path $source 'native') -Force
    $upstreamArchive = Join-Path $OutputDirectory 'TranslucentTB-2026.2-source.zip'
    & git -C vendor/TranslucentTB archive --format=zip --output=$upstreamArchive d4636e439865df0a1a1419db408e055740ce5c74
    if ($LASTEXITCODE -ne 0) { throw 'Could not package corresponding native source.' }
    Expand-Archive -LiteralPath $upstreamArchive -DestinationPath (Join-Path $source 'vendor/TranslucentTB') -Force
    Compress-Archive -Path $source -DestinationPath (Join-Path $OutputDirectory 'LumaBar-0.6.2-source.zip') -Force
    Write-Output "Ready: $distribution"
} finally { Pop-Location }
