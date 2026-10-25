param(
    [ValidatePattern('^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?$')]
    [string]$Version = '1.1.0',
    [string]$RestoreSource
)

# Lightweight publishing only. No tests, application launch, settings edits or server operations.
$ErrorActionPreference = 'Stop'
$taskRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot)).TrimEnd('\')
$taskDist = Join-Path $taskRoot 'dist'
$taskName = "AppEgress-$Version-win-x64"
$taskSourceName = "AppEgress-$Version-source.zip"
$taskZip = Join-Path $taskDist ($taskName + '.zip')
$taskSourceZip = Join-Path $taskDist $taskSourceName
foreach ($taskPath in @($taskZip,$taskSourceZip)) {
    if (Test-Path -LiteralPath $taskPath) { throw "Output already exists; choose a new version or remove it explicitly: $taskPath" }
}
$taskWork = Join-Path $taskRoot ('work\package-' + [guid]::NewGuid().ToString('N'))
$taskStage = Join-Path $taskWork $taskName
$taskArtifacts = Join-Path $taskWork 'artifacts'
New-Item -ItemType Directory -Path $taskDist,$taskStage -Force | Out-Null

$taskNative = Join-Path $taskRoot 'native\x64'
$taskExpected = @{
    'WinDivert.dll' = 'C1E060EE19444A259B2162F8AF0F3FE8C4428A1C6F694DCE20DE194AC8D7D9A2'
    'WinDivert64.sys' = '8DA085332782708D8767BCACE5327A6EC7283C17CFB85E40B03CD2323A90DDC2'
}
foreach ($taskFile in $taskExpected.Keys) {
    if ((Get-FileHash -LiteralPath (Join-Path $taskNative $taskFile) -Algorithm SHA256).Hash -ne $taskExpected[$taskFile]) {
        throw "Native component hash mismatch: $taskFile. Run scripts/get-windivert.ps1 first."
    }
}
if ((Get-AuthenticodeSignature -LiteralPath (Join-Path $taskNative 'WinDivert64.sys')).Status -ne 'Valid') {
    throw 'WinDivert64.sys does not have a valid signature.'
}

$taskFileVersion = (($Version -split '[-+]')[0]) + '.0'
$taskArguments = @('publish',(Join-Path $taskRoot 'src\AppEgress\AppEgress.csproj'),'-c','Release','-r','win-x64',
    '--self-contained','false','--artifacts-path',$taskArtifacts,'-p:PublishSingleFile=true',
    "-p:Version=$Version","-p:FileVersion=$taskFileVersion",'-o',$taskStage,'--nologo')
if ($RestoreSource) { $taskArguments += "-p:RestoreSources=$RestoreSource" }
& dotnet @taskArguments
if ($LASTEXITCODE -ne 0) { throw 'Publishing failed.' }

foreach ($taskDocument in @('LICENSE','THIRD_PARTY_NOTICES.md','UPSTREAM.md')) {
    Copy-Item -LiteralPath (Join-Path $taskRoot $taskDocument) -Destination (Join-Path $taskStage $taskDocument)
}
New-Item -ItemType Directory -Path (Join-Path $taskStage 'licenses') | Out-Null
Copy-Item -LiteralPath (Join-Path $taskRoot 'licenses\WinDivert-LICENSE.txt') -Destination (Join-Path $taskStage 'licenses\WinDivert-LICENSE.txt')
@(
    'AppEgress - Windows application network egress analysis and routing management.'
    'Extract all files. Exit the old instance through its tray menu, then run AppEgress.exe as administrator.'
    '.NET 8 Desktop Runtime x64 is required and is not bundled in this lightweight package.'
    'Settings -> Language -> 简体中文 / English / Русский. Resume if paused.'
    'The app reads legacy TrafikRuler settings if no AppEgress settings file exists; future saves use %APPDATA%\AppEgress.'
    'Connection/route evidence is not proof of actual VPN forwarding, shared-proxy attribution or proxy billing.'
    'Corresponding source is supplied separately as ' + $taskSourceName
    'Based on TrafikRuler; preserve LICENSE, UPSTREAM.md and THIRD_PARTY_NOTICES.md.'
) | Set-Content -LiteralPath (Join-Path $taskStage 'README.txt') -Encoding utf8

# Include local source changes as well as tracked files; skip deleted old paths after a rename.
Push-Location -LiteralPath $taskRoot
try {
    $taskCandidates = @(git -c core.quotepath=false ls-files --cached --others --exclude-standard | Sort-Object -Unique)
    if ($LASTEXITCODE -ne 0) { throw 'A Git checkout and Git are required for corresponding-source packaging.' }
    $taskCommit = (git rev-parse HEAD).Trim()
} finally { Pop-Location }
$taskSources = @($taskCandidates | Where-Object { Test-Path -LiteralPath (Join-Path $taskRoot $_) -PathType Leaf })
Add-Type -AssemblyName System.IO.Compression.FileSystem
$taskArchive = [IO.Compression.ZipFile]::Open($taskSourceZip,[IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($taskRelative in $taskSources) {
        if ($taskRelative -match '^(?:work|dist|native|out|publish|\.git)/' -or $taskRelative -match '(?:^|/)(?:bin|obj|TestResults)/') {
            throw "Non-source path in archive list: $taskRelative"
        }
        $taskFull = [IO.Path]::GetFullPath((Join-Path $taskRoot $taskRelative))
        if (-not $taskFull.StartsWith($taskRoot+'\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Source path escaped the repository.' }
        if ((Get-Item -LiteralPath $taskFull).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Source links are not packaged automatically.' }
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($taskArchive,$taskFull,
            'AppEgress-source/' + $taskRelative.Replace('\','/'),[IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
} finally { $taskArchive.Dispose() }
$taskSourceHash = (Get-FileHash -LiteralPath $taskSourceZip -Algorithm SHA256).Hash.ToLowerInvariant()
[ordered]@{
    Product='AppEgress';Version=$Version;BuiltAt=(Get-Date).ToString('o');Repository='https://github.com/ccut2014/app-egress'
    SourceBaseCommit=$taskCommit;SourceIncludesWorkingTreeChanges=$true;SourceArchive=$taskSourceName;SourceSha256=$taskSourceHash
    Target='win-x64';SelfContained=$false;RequiredRuntime='.NET 8 Desktop Runtime x64'
    TestsRunByThisScript=$false;PackagedApplicationLaunched=$false
} | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $taskStage 'BUILD-INFO.json') -Encoding utf8
$taskFiles = @(Get-ChildItem -LiteralPath $taskStage -File -Recurse)
$taskSums = foreach ($taskFile in $taskFiles) {
    '{0}  {1}' -f (Get-FileHash -LiteralPath $taskFile.FullName -Algorithm SHA256).Hash.ToLowerInvariant(),
        $taskFile.FullName.Substring($taskStage.Length+1).Replace('\','/')
}
$taskSums | Set-Content -LiteralPath (Join-Path $taskStage 'SHA256SUMS.txt') -Encoding utf8
[IO.Compression.ZipFile]::CreateFromDirectory($taskStage,$taskZip,[IO.Compression.CompressionLevel]::Optimal,$true)

# Read and hash every archived file. This checks packaging integrity, not application behavior.
$taskArchive = [IO.Compression.ZipFile]::OpenRead($taskZip)
try {
    foreach ($taskEntry in $taskArchive.Entries) {
        if (-not $taskEntry.Name) { continue }
        if (-not $taskEntry.FullName.StartsWith($taskName+'/',[StringComparison]::Ordinal)) { throw 'Unexpected archive path.' }
        $taskInput = $taskEntry.Open()
        $taskHasher = [Security.Cryptography.SHA256]::Create()
        try { $taskHash = [BitConverter]::ToString($taskHasher.ComputeHash($taskInput)).Replace('-','').ToLowerInvariant() }
        finally { $taskHasher.Dispose(); $taskInput.Dispose() }
        $taskRelative = $taskEntry.FullName.Substring($taskName.Length+1)
        if ($taskHash -ne (Get-FileHash -LiteralPath (Join-Path $taskStage $taskRelative) -Algorithm SHA256).Hash.ToLowerInvariant()) {
            throw "Archive verification failed: $taskRelative"
        }
    }
} finally { $taskArchive.Dispose() }
@($taskZip,$taskSourceZip) | ForEach-Object {
    '{0}  {1}' -f (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash.ToLowerInvariant(),[IO.Path]::GetFileName($_)
} | Set-Content -LiteralPath (Join-Path $taskDist 'SHA256SUMS.txt') -Encoding ascii
Get-Item -LiteralPath $taskZip,$taskSourceZip | Select-Object Name,Length
