$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (!(Test-Path -LiteralPath $compiler)) { $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
if (!(Test-Path -LiteralPath $compiler)) { throw '.NET Framework C# compiler not found.' }
Push-Location $PSScriptRoot
try {
    $references = @('/r:System.dll', '/r:System.Core.dll', '/r:System.Drawing.dll', '/r:System.Windows.Forms.dll', '/r:System.Web.Extensions.dll')
    & $compiler /nologo /warn:4 /warnaserror+ /optimize+ /target:winexe /platform:anycpu /win32manifest:src\app.manifest /out:LumaBar.exe @references src\Core.cs src\Native.cs src\Wallpaper.cs src\Storage.cs src\App.cs
    if ($LASTEXITCODE -ne 0) { throw 'Application compilation failed.' }
    New-Item -ItemType Directory -Path tests/bin -Force | Out-Null
    & $compiler /nologo /warn:4 /warnaserror+ /optimize+ /target:exe /main:LumaBar.Tests /platform:anycpu /win32manifest:src\app.manifest /out:tests\bin\LumaBar.Tests.exe @references src\Core.cs src\Native.cs src\Wallpaper.cs src\Storage.cs src\App.cs tests\Tests.cs
    if ($LASTEXITCODE -ne 0) { throw 'Test compilation failed.' }
    & .\tests\bin\LumaBar.Tests.exe
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
    Write-Output 'Build and tests succeeded: LumaBar.exe'
} finally { Pop-Location }
