#Requires -RunAsAdministrator
# Removes MagicFnDriver and stops trusting its signing certificate. Windows then puts the plain HID driver on the keyboard,
# without Apple's KeyMagic2, so MagicFnSetup is started afterwards to bring the Fn keys back.
param([switch]$KeepAppleDriverAsIs)   # for callers that put another driver on the keyboard themselves
$ErrorActionPreference = 'Stop'

$published = (pnputil /enum-drivers) -join "`n" -split '(?m)^\s*$' |
    Where-Object { $_ -match 'magicfndriver\.inf|magickbd\.inf' } |
    ForEach-Object { [regex]::Match($_, 'oem\d+\.inf').Value }
foreach ($inf in $published) {
    pnputil /delete-driver $inf /uninstall /force
    if ($LASTEXITCODE -notin 0, 3010) { throw "pnputil failed with exit code $LASTEXITCODE" }   # 3010 = done, reboot needed
}

Get-ChildItem Cert:\LocalMachine\Root, Cert:\LocalMachine\TrustedPublisher |
    Where-Object Subject -eq 'CN=MagicFn driver signing' | Remove-Item

$setup = Join-Path (Split-Path $PSScriptRoot) 'MagicFnSetup.exe'
if ($published -and -not $KeepAppleDriverAsIs -and (Test-Path $setup)) { Start-Process $setup apple }
