param([Parameter(Mandatory=$true)][string]$PackageRoot)
$ErrorActionPreference = 'Stop'
$PackageRoot = (Resolve-Path -LiteralPath $PackageRoot).Path
$names = @('LumaBar.exe', 'LumaBar.exe.config', 'README.md', 'LICENSE', 'LICENSE-LumaBar-MIT', 'THIRD_PARTY_NOTICES.md', 'native/ExplorerTAP.dll')
$hashes = [ordered]@{}
foreach ($name in $names) {
    $hashes[$name] = (Get-FileHash -LiteralPath (Join-Path $PackageRoot $name) -Algorithm SHA256).Hash
}
$version = (Get-Item -LiteralPath (Join-Path $PackageRoot 'LumaBar.exe')).VersionInfo.FileVersion
if (!$version) { throw 'Executable has no file version.' }
$manifest = [ordered]@{ Version = $version; Files = $hashes } | ConvertTo-Json -Depth 4
[IO.File]::WriteAllText((Join-Path $PackageRoot 'update.json'), $manifest, [Text.UTF8Encoding]::new($false))
