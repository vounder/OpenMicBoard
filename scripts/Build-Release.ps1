[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$')]
    [string] $Version,

    [ValidateSet('x64', 'arm64')]
    [string[]] $Architectures = @('x64', 'arm64'),

    [switch] $SkipInstaller
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$artifactsRoot = Join-Path $projectRoot 'artifacts'
$releaseRoot = Join-Path $artifactsRoot 'release'
$publishRoot = Join-Path $artifactsRoot 'publish'
$appProject = Join-Path $projectRoot 'src\OpenMicBoard.App\OpenMicBoard.App.csproj'
$numericVersion = ($Version -split '-')[0]
$fileVersion = "$numericVersion.0"

New-Item -ItemType Directory -Force -Path $releaseRoot | Out-Null
New-Item -ItemType Directory -Force -Path $publishRoot | Out-Null

dotnet test (Join-Path $projectRoot 'OpenMicBoard.sln') `
    --configuration Release `
    -p:Platform=x64 `
    --verbosity minimal
if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }

foreach ($architecture in $Architectures) {
    $runtime = "win-$architecture"
    $output = Join-Path $publishRoot $architecture
    if (Test-Path -LiteralPath $output) {
        Remove-Item -LiteralPath $output -Recurse -Force
    }

    dotnet publish $appProject `
        --configuration Release `
        --runtime $runtime `
        -p:Platform=$architecture `
        -p:Version=$Version `
        -p:FileVersion=$fileVersion `
        -p:AssemblyVersion=$fileVersion `
        --self-contained true `
        --output $output
    if ($LASTEXITCODE -ne 0) { throw "Publish failed for $architecture." }

    $debugSymbols = Get-ChildItem -LiteralPath $output -Filter '*.pdb' -File -Recurse
    if ($debugSymbols) {
        throw "Release publish contains debug symbols: $($debugSymbols.FullName -join ', ')"
    }

    $portableStage = Join-Path $artifactsRoot "portable-$architecture"
    if (Test-Path -LiteralPath $portableStage) {
        Remove-Item -LiteralPath $portableStage -Recurse -Force
    }
    Copy-Item -LiteralPath $output -Destination $portableStage -Recurse
    New-Item -ItemType File -Path (Join-Path $portableStage 'OpenMicBoard.portable') -Force | Out-Null
    $portableArchive = Join-Path $releaseRoot "OpenMicBoard-$Version-windows-$architecture-portable.zip"
    if (Test-Path -LiteralPath $portableArchive) {
        Remove-Item -LiteralPath $portableArchive -Force
    }
    Compress-Archive -Path (Join-Path $portableStage '*') -DestinationPath $portableArchive -CompressionLevel Optimal

    if (-not $SkipInstaller) {
        $compilerCandidates = @(
            (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 7\ISCC.exe'),
            (Join-Path $env:ProgramFiles 'Inno Setup 7\ISCC.exe'),
            (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 7\ISCC.exe'),
            (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
            (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe'),
            (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe')
        )
        $compiler = $compilerCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
        if (-not $compiler) {
            throw 'Inno Setup 6.3 or later was not found. Install it or pass -SkipInstaller.'
        }
        & $compiler `
            "/DAppVersion=$Version" `
            "/DFileVersion=$fileVersion" `
            "/DArchitecture=$architecture" `
            "/DSourceDir=$output" `
            "/DOutputDir=$releaseRoot" `
            (Join-Path $projectRoot 'installer\OpenMicBoard.iss')
        if ($LASTEXITCODE -ne 0) { throw "Installer build failed for $architecture." }
    }
}

$sbomPath = Join-Path $releaseRoot "OpenMicBoard-$Version.spdx.json"
& (Join-Path $PSScriptRoot 'Generate-Sbom.ps1') -Version $Version -OutputPath $sbomPath

$checksumPath = Join-Path $releaseRoot 'SHA256SUMS.txt'
$checksumLines = Get-ChildItem -LiteralPath $releaseRoot -File |
    Where-Object { $_.Name -ne 'SHA256SUMS.txt' } |
    Sort-Object Name |
    ForEach-Object {
        $hash = Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256
        "$($hash.Hash.ToLowerInvariant())  $($_.Name)"
    }
[System.IO.File]::WriteAllLines($checksumPath, $checksumLines)

Write-Host "Release artifacts: $releaseRoot"
