param([Parameter(Mandatory=$true)][string]$PackageRoot, [switch]$PassThru)
$ErrorActionPreference = 'Stop'
$PackageRoot = (Resolve-Path -LiteralPath $PackageRoot).Path
$names = @('LumaBar.exe', 'LumaBar.exe.config', 'README.md', 'LICENSE', 'LICENSE-LumaBar-MIT', 'THIRD_PARTY_NOTICES.md', 'native/ExplorerTAP.dll')
$hashes = [ordered]@{}
foreach ($name in $names) {
    $hashes[$name] = (Get-FileHash -LiteralPath (Join-Path $PackageRoot $name) -Algorithm SHA256).Hash
}
$version = (Get-Item -LiteralPath (Join-Path $PackageRoot 'LumaBar.exe')).VersionInfo.FileVersion
$fileVersion = $null
if (![version]::TryParse($version, [ref]$fileVersion) -or $fileVersion.Build -lt 0) {
    throw 'LumaBar.exe must have a valid file version with at least three components.'
}
$manifest = [ordered]@{ Version = $version; Files = $hashes } | ConvertTo-Json -Depth 4
[IO.File]::WriteAllText((Join-Path $PackageRoot 'update.json'), $manifest, [Text.UTF8Encoding]::new($false))
if ($PassThru) { $fileVersion }
