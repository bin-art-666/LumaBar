param([string]$UserSettingsPath, [string]$OutputDirectory = (Join-Path $PSScriptRoot 'dist'))
$ErrorActionPreference = 'Stop'
$upstreamCommit = 'd4636e439865df0a1a1419db408e055740ce5c74'
$upstreamName = "TranslucentTB-$upstreamCommit"
$upstreamHash = 'C66C067E591589AD2EEB1DBFFB8845C4DA5EF060DEBB3FF9FBE5EFD3F8298FD1'
$staging = $null
$keepStaging = $false

function Write-PackageZip($folder, $zip) {
    $archive = [IO.Compression.ZipFile]::Open($zip, [IO.Compression.ZipArchiveMode]::Create)
    try {
        $rootName = Split-Path -Leaf $folder
        foreach ($file in Get-ChildItem -LiteralPath $folder -File -Recurse -Force) {
            $relative = $file.FullName.Substring($folder.Length + 1).Replace('\', '/')
            [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.FullName, "$rootName/$relative", [IO.Compression.CompressionLevel]::Optimal)
        }
    } finally { $archive.Dispose() }
}

Push-Location $PSScriptRoot
try {
    if (!(Test-Path -LiteralPath 'LumaBar.exe' -PathType Leaf)) { throw 'Run build.ps1 first.' }
    $OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
    New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
    $staging = Join-Path $OutputDirectory ('.lumabar-package-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $staging | Out-Null
    Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem

    # Use the pinned submodule commit when present, or a verified upstream archive.
    $vendor = Join-Path $PSScriptRoot 'vendor/TranslucentTB'
    $upstreamZip = Join-Path $staging "$upstreamName.zip"
    $cache = Join-Path $PSScriptRoot "native/upstream-cache/$upstreamName.zip"
    $hasCommit = $false
    if ((Test-Path -LiteralPath (Join-Path $vendor '.git')) -and (Get-Command git -ErrorAction SilentlyContinue)) {
        & git -C $vendor cat-file -e "$upstreamCommit^{commit}" 2>$null
        $hasCommit = $LASTEXITCODE -eq 0
    }
    if ($hasCommit) {
        & git -C $vendor archive --format=zip "--prefix=$upstreamName/" "--output=$upstreamZip" $upstreamCommit
        if ($LASTEXITCODE -ne 0) { throw 'Could not archive the pinned TranslucentTB commit.' }
    } else {
        if (Test-Path -LiteralPath $cache -PathType Leaf) {
            Copy-Item -LiteralPath $cache -Destination $upstreamZip
        } else {
            try {
                Invoke-WebRequest -UseBasicParsing -Uri "https://codeload.github.com/TranslucentTB/TranslucentTB/zip/$upstreamCommit" -OutFile $upstreamZip -TimeoutSec 60
            } catch {
                throw "Could not fetch the pinned TranslucentTB source. Initialize vendor/TranslucentTB or supply the verified archive at '$cache'. $($_.Exception.Message)"
            }
        }
        if ((Get-FileHash -LiteralPath $upstreamZip -Algorithm SHA256).Hash -ne $upstreamHash) {
            throw 'Unexpected SHA256 for the pinned TranslucentTB source archive.'
        }
    }
    $distribution = Join-Path $staging 'runtime'
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
    $fileVersion = & (Join-Path $PSScriptRoot 'tools/WriteUpdateManifest.ps1') -PackageRoot $distribution -PassThru
    $version = $fileVersion.ToString($(if ($fileVersion.Revision -gt 0) { 4 } else { 3 }))
    $distributionName = "LumaBar-$version-preview-x64"
    Rename-Item -LiteralPath $distribution -NewName $distributionName
    $distribution = Join-Path $staging $distributionName

    # Include exact upstream source beside the binary, without nested Git metadata.
    $source = Join-Path $staging "LumaBar-$version-source"
    New-Item -ItemType Directory -Path $source -Force | Out-Null
    foreach ($name in @('src', 'assets', 'tools', 'build.ps1', 'package.ps1', 'LumaBar.csproj', 'LumaBar.exe.config', 'settings.json', 'README.md', 'LICENSE', 'LICENSE-LumaBar-MIT', 'THIRD_PARTY_NOTICES.md', 'VALIDATION.md', 'docs')) {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination $source -Recurse -Force
    }
    New-Item -ItemType Directory -Path (Join-Path $source 'tests') -Force | Out-Null
    Copy-Item -Path 'tests/*.cs' -Destination (Join-Path $source 'tests') -Force
    New-Item -ItemType Directory -Path (Join-Path $source 'native') -Force | Out-Null
    Copy-Item -LiteralPath 'native/README.md', 'native/fetch.ps1', 'native/ExplorerTAP.dll' -Destination (Join-Path $source 'native') -Force
    $archives = foreach ($folder in @($distribution, $source)) {
        $zip = "$folder.zip"
        Write-PackageZip $folder $zip
        $name = [IO.Path]::GetFileName($zip)
        @{ Zip = $zip; Target = (Join-Path $OutputDirectory $name); Backup = (Join-Path $staging "$name.bak"); Published = $false }
    }

    # Stream the upstream tree into the source ZIP to avoid legacy Windows path limits.
    $inputZip = [IO.Compression.ZipFile]::OpenRead($upstreamZip)
    try {
        $sourceZip = [IO.Compression.ZipFile]::Open("$source.zip", [IO.Compression.ZipArchiveMode]::Update)
        try {
            $prefix = "$upstreamName/"
            $vendorPrefix = "$(Split-Path -Leaf $source)/vendor/TranslucentTB/"
            foreach ($entry in $inputZip.Entries) {
                if (!$entry.FullName.StartsWith($prefix, [StringComparison]::Ordinal)) { throw 'Unexpected upstream archive layout.' }
                $relative = $entry.FullName.Substring($prefix.Length)
                $copy = $sourceZip.CreateEntry($vendorPrefix + $relative, [IO.Compression.CompressionLevel]::Optimal)
                $copy.LastWriteTime = $entry.LastWriteTime
                if ($entry.Name) {
                    $inputStream = $entry.Open()
                    try {
                        $outputStream = $copy.Open()
                        try { $inputStream.CopyTo($outputStream) } finally { $outputStream.Dispose() }
                    } finally { $inputStream.Dispose() }
                }
            }
        } finally { $sourceZip.Dispose() }
    } finally { $inputZip.Dispose() }

    $lock = [IO.FileStream]::new((Join-Path $OutputDirectory '.lumabar-package.lock'), [IO.FileMode]::OpenOrCreate,
        [IO.FileAccess]::ReadWrite, [IO.FileShare]::None, 4096, [IO.FileOptions]::DeleteOnClose)
    try {
        foreach ($archive in $archives) {
            if ([IO.File]::Exists($archive.Target)) { [IO.File]::Replace($archive.Zip, $archive.Target, $archive.Backup) }
            else { [IO.File]::Move($archive.Zip, $archive.Target) }
            $archive.Published = $true
        }
    } catch {
        $keepStaging = $true
        try {
            foreach ($archive in $archives) {
                if ([IO.File]::Exists($archive.Backup)) {
                    if ([IO.File]::Exists($archive.Target)) { [IO.File]::Replace($archive.Backup, $archive.Target, [NullString]::Value) }
                    else { [IO.File]::Move($archive.Backup, $archive.Target) }
                }
                elseif ($archive.Published) { [IO.File]::Delete($archive.Target) }
            }
            $keepStaging = $false
        } catch { throw "Could not restore the previous release archives. Backups are in '$staging'. $($_.Exception.Message)" }
        throw
    } finally { $lock.Dispose() }
    foreach ($archive in $archives) { Write-Output "Ready: $($archive.Target)" }
} finally {
    try {
        if ($staging -and !$keepStaging -and (Test-Path -LiteralPath $staging -PathType Container)) {
            $resolved = Get-Item -LiteralPath $staging -Force
            if ($resolved.Parent.FullName.TrimEnd('\') -eq (Get-Item -LiteralPath $OutputDirectory).FullName.TrimEnd('\')) {
                Remove-Item -LiteralPath $resolved.FullName -Recurse -Force
            }
        }
    } finally { Pop-Location }
}
