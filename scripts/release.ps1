<#
.SYNOPSIS
    Builds, packages and optionally publishes a DirSizer GitHub release.

.DESCRIPTION
    Without -Publish, publishes the four NativeAOT win-x64 executables and creates:

      dist\DirSizer-v<version>-win-x64.zip

    The ZIP contains dirsizer.exe, dirsizer-fsctl.exe, dirsizer-bulk.exe,
    dirsizer-inspect.exe, README.md, README-jp.md and LICENSE. With -Publish, the script also
    creates and pushes the v<version> tag and creates the GitHub release with
    the ZIP attached. Use -NotesFile to provide the release description.

.PARAMETER Publish
    Push the tag and create the GitHub release. Requires -NotesFile.

.PARAMETER NotesFile
    Markdown file used as the GitHub release body.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\release.ps1

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\release.ps1 -Publish -NotesFile release-notes.md
#>

[CmdletBinding()]
param(
    [switch]$Publish,
    [string]$NotesFile
)

$ErrorActionPreference = 'Stop'
$Repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
# The release contains four tools. Each is published as its own NativeAOT executable.
$Tools = @(
    @{ Project = 'src\DirSizer.Fs\DirSizer.Fs.csproj';           Exe = 'dirsizer.exe' },
    @{ Project = 'src\DirSizer.Fsctl\DirSizer.Fsctl.csproj';     Exe = 'dirsizer-fsctl.exe' },
    @{ Project = 'src\DirSizer.Bulk\DirSizer.Bulk.csproj';       Exe = 'dirsizer-bulk.exe' },
    @{ Project = 'src\DirSizer.Inspect\DirSizer.Inspect.csproj'; Exe = 'dirsizer-inspect.exe' }
)
$VersionFile = Join-Path $Repo 'Directory.Build.props'
$Dist = Join-Path $Repo 'dist'
$Runtime = 'win-x64'
$FetchRemote = 'origin'
$PushRemote = 'origin'

function Invoke-Native([string]$What, [scriptblock]$Command) {
    & $Command
    if ($LASTEXITCODE -ne 0) { throw "$What failed (exit code $LASTEXITCODE)" }
}

# The NativeAOT link step runs vswhere.exe to find the C++ build tools. It lives in the Visual Studio Installer folder, which
# is not always on PATH (a plain terminal, an agent shell); without it the link fails with a misleading "not recognized" error.
if (-not (Get-Command vswhere.exe -ErrorAction SilentlyContinue)) {
    $installer = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer'
    if (Test-Path (Join-Path $installer 'vswhere.exe')) { $env:PATH = "$env:PATH;$installer" }
    else { Write-Warning 'vswhere.exe was not found; the NativeAOT link step will fail unless the Visual Studio C++ build tools are installed.' }
}

foreach ($tool in $Tools) { if (-not (Test-Path (Join-Path $Repo $tool.Project))) { throw "project not found: $($tool.Project)" } }
if (-not (Test-Path $VersionFile)) { throw "version file not found: $VersionFile" }
[xml]$projectXml = Get-Content $VersionFile -Raw
$versionNode = $projectXml.Project.PropertyGroup.Version | Select-Object -First 1
if (-not $versionNode) { throw 'no <Version>...</Version> in Directory.Build.props' }
$Version = $versionNode.ToString().Trim()
if ($Version -notmatch '^\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?$') {
    throw "invalid project version: $Version"
}
$Tag = "v$Version"
$PackageName = "DirSizer-$Tag-$Runtime"
$Stage = Join-Path $Dist $PackageName
$Zip = Join-Path $Dist "$PackageName.zip"
$PublishRoot = Join-Path $Dist 'publish'

if ($Publish) {
    if (-not $NotesFile) { throw '-Publish needs -NotesFile <release notes.md>' }
    if (-not (Test-Path $NotesFile)) { throw "notes file not found: $NotesFile" }
    $NotesFile = (Resolve-Path $NotesFile).Path

    $dirty = & git -C $Repo status --porcelain
    if ($dirty) { throw "working tree is not clean:`n$($dirty -join "`n")" }
    $branch = (& git -C $Repo rev-parse --abbrev-ref HEAD).Trim()
    if ($branch -ne 'master') { throw "publish from master (currently on '$branch')" }
    if (-not (Get-Command gh -ErrorAction SilentlyContinue)) { throw 'gh is not on PATH -- install GitHub CLI and run gh auth login.' }
    & gh repo view panzoux/dirsizer --json nameWithOwner | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'GitHub repository panzoux/dirsizer was not found. Create it or update the repository name in scripts\release.ps1.' }

    & git -C $Repo rev-parse -q --verify "refs/tags/$Tag" | Out-Null
    if ($LASTEXITCODE -eq 0) { throw "tag $Tag already exists locally -- bump <Version> in Directory.Build.props" }
    $remoteTag = & git -C $Repo ls-remote --tags $FetchRemote "refs/tags/$Tag"
    if ($remoteTag) { throw "tag $Tag already exists on $FetchRemote" }
}

Write-Host "DirSizer $Tag -- runtime: $Runtime" -ForegroundColor Cyan
Write-Host "`n-- dotnet publish --" -ForegroundColor Cyan
if (Test-Path $PublishRoot) { Remove-Item $PublishRoot -Recurse -Force }
foreach ($tool in $Tools) {
    $project = Join-Path $Repo $tool.Project
    $out = Join-Path $PublishRoot ([IO.Path]::GetFileNameWithoutExtension($tool.Exe))
    Invoke-Native "dotnet publish $($tool.Project)" { dotnet publish $project -c Release -r $Runtime --self-contained true -o $out }
    $tool.Published = Join-Path $out $tool.Exe
    if (-not (Test-Path $tool.Published)) { throw "published executable not found: $($tool.Published)" }
}

if (Test-Path $Stage) { Remove-Item $Stage -Recurse -Force }
if (Test-Path $Zip) { Remove-Item $Zip -Force }
New-Item -ItemType Directory -Force $Stage | Out-Null
foreach ($tool in $Tools) { Copy-Item $tool.Published (Join-Path $Stage $tool.Exe) }
Copy-Item (Join-Path $Repo 'README.md') (Join-Path $Stage 'README.md')
Copy-Item (Join-Path $Repo 'README-jp.md') (Join-Path $Stage 'README-jp.md')
Copy-Item (Join-Path $Repo 'LICENSE') (Join-Path $Stage 'LICENSE')

Compress-Archive -Path (Join-Path $Stage '*') -DestinationPath $Zip -CompressionLevel Optimal
$hash = (Get-FileHash $Zip -Algorithm SHA256).Hash.ToLowerInvariant()
Write-Host ("  {0}  {1:N0} bytes  sha256:{2}" -f $Zip, (Get-Item $Zip).Length, $hash) -ForegroundColor Green

if (-not $Publish) {
    Write-Host "`nPackaged release. Not published (no -Publish)." -ForegroundColor Cyan
    exit 0
}

Write-Host "`n-- publish $Tag --" -ForegroundColor Cyan
Invoke-Native 'git tag' { git -C $Repo tag -a $Tag -m "DirSizer $Tag" }
Invoke-Native 'git push master' { git -C $Repo push $PushRemote master }
Invoke-Native "git push $Tag" { git -C $Repo push $PushRemote $Tag }
Invoke-Native 'gh release create' {
    gh release create $Tag $Zip --repo panzoux/dirsizer --title "DirSizer $Tag" --notes-file $NotesFile --verify-tag
}
Write-Host "`nPublished $Tag." -ForegroundColor Green
