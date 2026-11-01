$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (!(Test-Path -LiteralPath $compiler)) { $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
if (!(Test-Path -LiteralPath $compiler)) { throw '.NET Framework C# compiler not found.' }
Push-Location $PSScriptRoot
try {
    $materialDll = Join-Path $PSScriptRoot 'native\ExplorerTAP.dll'
    if (!(Test-Path -LiteralPath $materialDll)) { throw 'Missing native/ExplorerTAP.dll. Run native/fetch.ps1.' }
    $expectedHash = 'C48F1A06168ACD7839269DDE0D15CCF5D09C702512B94DD6BA9A05DF1B153DC2'
    if ((Get-FileHash -LiteralPath $materialDll -Algorithm SHA256).Hash -ne $expectedHash) { throw 'Unexpected ExplorerTAP.dll checksum.' }
    $references = @('/r:System.dll', '/r:System.Core.dll', '/r:System.Drawing.dll', '/r:System.Windows.Forms.dll', '/r:System.Web.Extensions.dll')
    & $compiler /nologo /warn:4 /warnaserror+ /optimize+ /target:winexe /platform:x64 /win32manifest:src\app.manifest /out:LumaBar.exe @references src\Core.cs src\Native.cs src\Wallpaper.cs src\Storage.cs src\App.cs src\Materials.cs src\AppearanceEditor.cs src\Readability.cs src\TaskbarBlend.cs
    if ($LASTEXITCODE -ne 0) { throw 'Application compilation failed.' }
    New-Item -ItemType Directory -Path tests/bin -Force | Out-Null
    New-Item -ItemType Directory -Path tests/bin/native -Force | Out-Null
    $testMaterialDll = Join-Path $PSScriptRoot 'tests/bin/native/ExplorerTAP.dll'
    if (!(Test-Path -LiteralPath $testMaterialDll) -or (Get-FileHash -LiteralPath $testMaterialDll -Algorithm SHA256).Hash -ne $expectedHash) {
        Copy-Item -LiteralPath $materialDll -Destination $testMaterialDll -Force
    }
    & $compiler /nologo /warn:4 /warnaserror+ /optimize+ /target:exe /main:LumaBar.Tests /platform:x64 /win32manifest:src\app.manifest /out:tests\bin\LumaBar.Tests.exe @references src\Core.cs src\Native.cs src\Wallpaper.cs src\Storage.cs src\App.cs src\Materials.cs src\AppearanceEditor.cs src\Readability.cs src\TaskbarBlend.cs tests\Tests.cs tests\StabilityTests.cs
    if ($LASTEXITCODE -ne 0) { throw 'Test compilation failed.' }
    & .\tests\bin\LumaBar.Tests.exe
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
    Write-Output 'Build and tests succeeded: LumaBar.exe'
} finally { Pop-Location }
