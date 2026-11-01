$ErrorActionPreference = 'Stop'
$expectedDll = 'C48F1A06168ACD7839269DDE0D15CCF5D09C702512B94DD6BA9A05DF1B153DC2'
$destination = Join-Path $PSScriptRoot 'ExplorerTAP.dll'
if ((Test-Path -LiteralPath $destination) -and (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash -eq $expectedDll) {
    Write-Output 'ExplorerTAP.dll 2026.2 is already verified.'
    return
}
$download = Join-Path ([IO.Path]::GetTempPath()) ('lumabar-native-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $download | Out-Null
# Keep the downloaded archive in this unique temp folder for provenance; no installer is run.
$archive = Join-Path $download 'TranslucentTB-portable-x64.zip'
Invoke-WebRequest 'https://github.com/TranslucentTB/TranslucentTB/releases/download/2026.2/TranslucentTB-portable-x64.zip' -OutFile $archive
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne '0DBE8E0255C20E131CDE536DCD0AE490D45989A7D360E26D0150DFF1922AC420') {
    throw 'The official archive checksum did not match.'
}
Expand-Archive -LiteralPath $archive -DestinationPath (Join-Path $download 'files')
$dll = Join-Path $download 'files/ExplorerTAP.dll'
if ((Get-FileHash -LiteralPath $dll -Algorithm SHA256).Hash -ne $expectedDll) { throw 'ExplorerTAP.dll checksum did not match.' }
Copy-Item -LiteralPath $dll -Destination $destination -Force
Write-Output 'Verified TranslucentTB 2026.2 ExplorerTAP.dll is ready.'
