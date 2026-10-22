# bbport: downloads the Intel XeSS SDK (Super Resolution, Vulkan) into gpu/third_party/xess-sdk.
# Intel's headers and libxess.dll may not be redistributed in this repository (Intel Simplified
# Software License): this script fetches the official release instead. build.bat then compiles
# the XeSS upscaler (upscaler=xess) and copies libxess.dll and its license next to bbport.exe.
#
#   powershell -ExecutionPolicy Bypass -File tools\fetch_xess_sdk.ps1 [-Zip <local XeSS_SDK zip>]
param([string]$Zip = "")

$ErrorActionPreference = "Stop"
$Version = "3.0.2"
$Url = "https://github.com/intel/xess/releases/download/v$Version/XeSS_SDK_$Version.zip"
$Sha256 = "88b8a373f30e33f3558a77a93e634f11b8132fc3047ea1a8edeead32b8471990"

$Root = Split-Path -Parent $PSScriptRoot
$Dest = Join-Path $Root "gpu\third_party\xess-sdk"
$Temp = Join-Path ([IO.Path]::GetTempPath()) ("bbport-xess-" + [Guid]::NewGuid())
New-Item -ItemType Directory -Force $Temp | Out-Null
try {
    if (-not $Zip) {
        $Zip = Join-Path $Temp "xess.zip"
        Write-Host "Downloading XeSS SDK $Version..."
        Invoke-WebRequest -UseBasicParsing -Uri $Url -OutFile $Zip
    }
    $hash = (Get-FileHash -Algorithm SHA256 $Zip).Hash.ToLower()
    if ($hash -ne $Sha256) {
        throw "XeSS SDK checksum mismatch: $hash (expected $Sha256)"
    }
    $Unpacked = Join-Path $Temp "sdk"
    Expand-Archive -Path $Zip -DestinationPath $Unpacked -Force
    if (Test-Path $Dest) { Remove-Item -Recurse -Force $Dest }
    New-Item -ItemType Directory -Force (Join-Path $Dest "inc\xess"), (Join-Path $Dest "bin") | Out-Null
    Copy-Item (Join-Path $Unpacked "inc\xess\*.h") (Join-Path $Dest "inc\xess")
    Copy-Item (Join-Path $Unpacked "bin\libxess.dll") (Join-Path $Dest "bin")
    Copy-Item (Join-Path $Unpacked "LICENSE.txt") (Join-Path $Dest "LICENSE.txt")
    Copy-Item (Join-Path $Unpacked "third-party-programs.txt") (Join-Path $Dest "third-party-programs.txt")
    Set-Content -Encoding ascii (Join-Path $Dest "VERSION") $Version
    Write-Host "XeSS SDK $Version installed in $Dest"
} finally {
    Remove-Item -Recurse -Force $Temp -ErrorAction SilentlyContinue
}
