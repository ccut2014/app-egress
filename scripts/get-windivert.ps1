# Downloads WinDivert 2.2.2-A from the official GitHub release and puts the x64 files
# into native\x64, where the build picks them up.
#
# Microsoft Defender classifies WinDivert as HackTool:Win64/WinDivert (PUA) and may delete
# the download. See SECURITY.md and THIRD_PARTY_NOTICES.md.
#
# The script fails unless both files match the pinned SHA256 of the official 2.2.2-A release
# and the driver carries a valid Authenticode signature (upstream ships WinDivert.dll unsigned),
# so CI never packages tampered binaries.

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$dest = Join-Path $root 'native\x64'
$tmp = Join-Path $root 'native\download'
$url = 'https://github.com/basil00/WinDivert/releases/download/v2.2.2/WinDivert-2.2.2-A.zip'
$expected = @{
    'WinDivert.dll'   = 'C1E060EE19444A259B2162F8AF0F3FE8C4428A1C6F694DCE20DE194AC8D7D9A2'
    'WinDivert64.sys' = '8DA085332782708D8767BCACE5327A6EC7283C17CFB85E40B03CD2323A90DDC2'
}

New-Item -ItemType Directory -Force $dest, $tmp | Out-Null
$zip = Join-Path $tmp 'WinDivert-2.2.2-A.zip'

Write-Host "Downloading $url"
[Net.ServicePointManager]::SecurityProtocol = 'Tls12'
Invoke-WebRequest -Uri $url -OutFile $zip -UseBasicParsing

Write-Host "Archive SHA256: $((Get-FileHash $zip -Algorithm SHA256).Hash)"

Expand-Archive $zip -DestinationPath $tmp -Force
$x64 = Get-ChildItem -Recurse -Directory $tmp | Where-Object Name -eq 'x64' | Select-Object -First 1
if (-not $x64) { throw 'x64 folder not found in the archive' }

foreach ($file in $expected.Keys) {
    $src = Join-Path $x64.FullName $file
    $hash = (Get-FileHash $src -Algorithm SHA256).Hash
    if ($hash -ne $expected[$file]) {
        throw "$file SHA256 is $hash, expected $($expected[$file]); refusing to use it"
    }
    Write-Host "$file SHA256 OK"
    Copy-Item $src $dest -Force
}

$sig = Get-AuthenticodeSignature (Join-Path $dest 'WinDivert64.sys')
if ($sig.Status -ne 'Valid') {
    throw "WinDivert64.sys signature is $($sig.Status), refusing to use it"
}
Write-Host "WinDivert64.sys signature: Valid ($($sig.SignerCertificate.Subject))"

$resolvedRoot = [IO.Path]::GetFullPath($root).TrimEnd('\')
$resolvedTmp = [IO.Path]::GetFullPath($tmp)
if (-not $resolvedTmp.Equals((Join-Path $resolvedRoot 'native\download'), [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Unexpected cleanup directory; refusing recursive removal'
}
Remove-Item -LiteralPath $resolvedTmp -Recurse -Force
Write-Host "Done: $dest"
Get-ChildItem $dest | Format-Table Name, Length
