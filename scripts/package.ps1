param(
    [ValidatePattern('^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?$')]
    [string]$Version = '1.1.0',
    [ValidateSet('Light','Full','Both')]
    [string]$Variant = 'Light',
    [string]$RestoreSource
)

# Builds archives only. No tests, application launch, settings edits or server operations.
$ErrorActionPreference = 'Stop'
if ($Variant -eq 'Both') {
    $taskBothArguments = @{ Version=$Version; Variant='Light' }
    if ($RestoreSource) { $taskBothArguments.RestoreSource = $RestoreSource }
    & $PSCommandPath @taskBothArguments
    $taskBothArguments.Variant = 'Full'
    & $PSCommandPath @taskBothArguments
    return
}
$taskSelfContained = $Variant -eq 'Full'
$taskRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot)).TrimEnd('\')
$taskDist = Join-Path $taskRoot 'dist'
$taskName = "AppEgress-$Version-win-x64-$($Variant.ToLowerInvariant())"
$taskSourceName = "AppEgress-$Version-source.zip"
$taskZip = Join-Path $taskDist ($taskName + '.zip')
$taskSourceZip = Join-Path $taskDist $taskSourceName
foreach ($taskPath in @($taskZip)) {
    if (Test-Path -LiteralPath $taskPath) { throw "Output already exists; choose a new version or remove it explicitly: $taskPath" }
}
Push-Location -LiteralPath $taskRoot
try {
    $taskChanges = @(git status --porcelain=v1)
    if ($LASTEXITCODE -ne 0) { throw 'A Git checkout and Git are required.' }
    if ($taskChanges.Count) { throw 'Commit source changes before packaging so the source archive matches the build.' }
    $taskCommit = (git rev-parse HEAD).Trim()
} finally { Pop-Location }
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
    '--self-contained',$taskSelfContained.ToString().ToLowerInvariant(),'--artifacts-path',$taskArtifacts,'-p:PublishSingleFile=true',
    '-p:DebugType=None','-p:DebugSymbols=false','-p:Deterministic=true',"-p:PathMap=$taskRoot=/_/",
    "-p:Version=$Version","-p:FileVersion=$taskFileVersion",'-o',$taskStage,'--nologo')
if ($RestoreSource) { $taskArguments += "-p:RestoreSources=$RestoreSource" }
if ($taskSelfContained) { $taskArguments += '-p:IncludeNativeLibrariesForSelfExtract=true' }
& dotnet @taskArguments
if ($LASTEXITCODE -ne 0) { throw 'Publishing failed.' }

foreach ($taskDocument in @('LICENSE','THIRD_PARTY_NOTICES.md','UPSTREAM.md')) {
    Copy-Item -LiteralPath (Join-Path $taskRoot $taskDocument) -Destination (Join-Path $taskStage $taskDocument)
}
New-Item -ItemType Directory -Path (Join-Path $taskStage 'licenses') | Out-Null
Copy-Item -LiteralPath (Join-Path $taskRoot 'licenses\WinDivert-LICENSE.txt') -Destination (Join-Path $taskStage 'licenses\WinDivert-LICENSE.txt')
$taskDotnetRoot = Split-Path -Parent (Get-Command dotnet).Source
Copy-Item -LiteralPath (Join-Path $taskDotnetRoot 'LICENSE.txt') -Destination (Join-Path $taskStage 'licenses\dotnet-LICENSE.txt')
if ($taskSelfContained) {
    Copy-Item -LiteralPath (Join-Path $taskDotnetRoot 'ThirdPartyNotices.txt') -Destination (Join-Path $taskStage 'licenses\dotnet-ThirdPartyNotices.txt')
    $taskAssets = Get-Content -LiteralPath (Join-Path $taskArtifacts 'obj\AppEgress\project.assets.json') -Raw | ConvertFrom-Json
    foreach ($taskRuntime in @('Microsoft.NETCore.App.Runtime.win-x64','Microsoft.WindowsDesktop.App.Runtime.win-x64')) {
        $taskDependency = @($taskAssets.project.frameworks.PSObject.Properties.Value.downloadDependencies | Where-Object { $_.name -eq $taskRuntime } | Select-Object -First 1)
        if ($taskDependency.Count -ne 1) { throw "Cannot identify runtime license package: $taskRuntime" }
        $taskRuntimeVersion = [regex]::Match($taskDependency[0].version,'\d+\.\d+\.\d+(?:-[A-Za-z0-9.-]+)?').Value
        if (-not $taskRuntimeVersion) { throw "Cannot identify runtime version: $taskRuntime" }
        $taskPackagePath = $null
        foreach ($taskFolder in $taskAssets.packageFolders.PSObject.Properties.Name) {
            $taskCandidate = Join-Path $taskFolder ($taskRuntime.ToLowerInvariant()+'/'+$taskRuntimeVersion)
            if (Test-Path -LiteralPath $taskCandidate -PathType Container) { $taskPackagePath = $taskCandidate; break }
        }
        if (-not $taskPackagePath) { throw "Missing runtime package: $taskRuntime" }
        $taskLegalFiles = @(Get-ChildItem -LiteralPath $taskPackagePath -File | Where-Object { $_.Name -match '^(LICENSE(?:\.TXT)?|THIRD-PARTY-NOTICES\.TXT)$' })
        if (-not @($taskLegalFiles | Where-Object { $_.Name -match '^LICENSE' }).Count) { throw "Missing runtime license: $taskRuntime" }
        foreach ($taskLegalFile in $taskLegalFiles) {
            Copy-Item -LiteralPath $taskLegalFile.FullName -Destination (Join-Path $taskStage ('licenses\'+$taskRuntime+'-'+$taskLegalFile.Name))
        }
    }
}
$taskRuntimeNote = if ($taskSelfContained) { 'The .NET 8 Desktop Runtime is included; no separate .NET installation is required.' }
    else { '.NET 8 Desktop Runtime x64 is required and is not bundled in this lightweight package.' }
@(
    'AppEgress - Windows application network egress analysis and routing management.'
    'Extract all files. Exit the old instance through its tray menu, then run AppEgress.exe as administrator.'
    $taskRuntimeNote
    'Settings -> Language -> 简体中文 / English / Русский. Resume if paused.'
    'The app reads legacy TrafikRuler settings if no AppEgress settings file exists; future saves use %APPDATA%\AppEgress.'
    'Connection/route evidence is not proof of actual VPN forwarding, shared-proxy attribution or proxy billing.'
    "Corresponding source is supplied separately as $taskSourceName"
    'Based on TrafikRuler; preserve LICENSE, UPSTREAM.md and THIRD_PARTY_NOTICES.md.'
) | Set-Content -LiteralPath (Join-Path $taskStage 'README.txt') -Encoding utf8

# Archive only the exact committed source; never include untracked local files.
Add-Type -AssemblyName System.IO.Compression.FileSystem
$taskSourceStage = Join-Path $taskWork $taskSourceName
git -C $taskRoot archive --format=zip --prefix=AppEgress-source/ --output=$taskSourceStage HEAD
if ($LASTEXITCODE -ne 0) { throw 'Cannot archive the committed source.' }
if (Test-Path -LiteralPath $taskSourceZip) {
    if ((Get-FileHash -LiteralPath $taskSourceZip).Hash -ne (Get-FileHash -LiteralPath $taskSourceStage).Hash) { throw 'Existing source archive belongs to a different commit.' }
} else { Copy-Item -LiteralPath $taskSourceStage -Destination $taskSourceZip }
$taskSourceHash = (Get-FileHash -LiteralPath $taskSourceZip -Algorithm SHA256).Hash.ToLowerInvariant()
[ordered]@{
    Product='AppEgress';Version=$Version;Variant=$Variant;BuiltAtUtc=[DateTimeOffset]::UtcNow.ToString('o');Repository='https://github.com/ccut2014/app-egress'
    SourceCommit=$taskCommit;SourceIncludesWorkingTreeChanges=$false;SourceArchive=$taskSourceName;SourceSha256=$taskSourceHash
    Target='win-x64';SelfContained=$taskSelfContained;RequiredRuntime=$(if ($taskSelfContained) { 'Included' } else { '.NET 8 Desktop Runtime x64' })
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
@('light','full' | ForEach-Object { Join-Path $taskDist "AppEgress-$Version-win-x64-$_.zip" }) + @($taskSourceZip) | Where-Object { Test-Path -LiteralPath $_ } | ForEach-Object {
    '{0}  {1}' -f (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash.ToLowerInvariant(),[IO.Path]::GetFileName($_)
} | Set-Content -LiteralPath (Join-Path $taskDist 'SHA256SUMS.txt') -Encoding ascii
Get-Item -LiteralPath $taskZip,$taskSourceZip | Select-Object Name,Length
