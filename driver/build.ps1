# Builds MagicFnDriver and assembles a signed driver package in .\package.
# Needs Visual Studio with the C++ and Windows Driver Kit components, plus the matching Windows SDK and WDK.
# The package is signed with a self-signed certificate "MagicFn driver signing" from the current user's store;
# MagicFnSetup makes the target machine trust it.
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$msbuild = & $vswhere -latest -requires Component.Microsoft.Windows.DriverKit -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
if (-not $msbuild) { throw 'Visual Studio with the Windows Driver Kit component was not found.' }

$kit = (Get-ItemProperty 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows Kits\Installed Roots').KitsRoot10
$ver = Get-ChildItem "$kit\bin" -Directory | Where-Object { Test-Path "$($_.FullName)\x86\Inf2Cat.exe" } | Sort-Object Name | Select-Object -Last 1 -ExpandProperty Name
if (-not $ver) { throw 'Windows Driver Kit tools (Inf2Cat) were not found.' }
$bin = "$kit\bin\$ver"

function Run($exe) { & $exe @args; if ($LASTEXITCODE -ne 0) { throw "$(Split-Path $exe -Leaf) failed with exit code $LASTEXITCODE" } }

Run $msbuild MagicFnDriver.vcxproj /nologo /v:m /p:Configuration=Release /p:Platform=x64

Remove-Item package -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory package | Out-Null
Copy-Item build\MagicFnDriver.dll package\
Copy-Item MagicFnDriver.inx package\MagicFnDriver.inf
Run "$bin\x64\stampinf.exe" -f package\MagicFnDriver.inf -a amd64 -u 2.15.0 -d * -v *
Run "$bin\x86\Inf2Cat.exe" /driver:package /os:10_X64 /uselocaltime

$cert = Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert | Where-Object Subject -eq 'CN=MagicFn driver signing' | Select-Object -First 1
if (-not $cert) {
    $cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject 'CN=MagicFn driver signing' -CertStoreLocation Cert:\CurrentUser\My -NotAfter (Get-Date).AddYears(10)
}
Export-Certificate -Cert $cert -FilePath package\MagicFn.cer | Out-Null
Run "$bin\x64\signtool.exe" sign /fd SHA256 /sha1 $cert.Thumbprint package\MagicFnDriver.dll package\MagicFnDriver.cat

Get-ChildItem package | Select-Object Name, Length
