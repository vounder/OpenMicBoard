[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $Version,

    [Parameter(Mandatory = $true)]
    [string] $OutputPath
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$raw = & dotnet list (Join-Path $root 'OpenMicBoard.sln') package --include-transitive --format json
if ($LASTEXITCODE -ne 0) {
    throw 'Could not resolve the dependency graph for the SBOM.'
}

$graph = ($raw -join [Environment]::NewLine) | ConvertFrom-Json
$resolved = @{}
foreach ($project in $graph.projects) {
    foreach ($framework in $project.frameworks) {
        foreach ($package in @($framework.topLevelPackages) + @($framework.transitivePackages)) {
            if ($null -ne $package -and $package.id) {
                $resolved[$package.id] = $package.resolvedVersion
            }
        }
    }
}

$appSpdxId = 'SPDXRef-Package-OpenMicBoard'
$packages = @(
    [ordered]@{
        name = 'OpenMicBoard'
        SPDXID = $appSpdxId
        versionInfo = $Version
        downloadLocation = 'https://github.com/vounder/OpenMicBoard'
        filesAnalyzed = $false
        licenseConcluded = 'GPL-3.0-only'
        licenseDeclared = 'GPL-3.0-only'
        copyrightText = 'NOASSERTION'
    }
)
$relationships = @(
    [ordered]@{
        spdxElementId = 'SPDXRef-DOCUMENT'
        relationshipType = 'DESCRIBES'
        relatedSpdxElement = $appSpdxId
    }
)

foreach ($name in ($resolved.Keys | Sort-Object)) {
    $safeName = $name -replace '[^A-Za-z0-9.-]', '-'
    $id = "SPDXRef-Package-$safeName"
    $packages += [ordered]@{
        name = $name
        SPDXID = $id
        versionInfo = $resolved[$name]
        downloadLocation = "https://www.nuget.org/packages/$name/$($resolved[$name])"
        filesAnalyzed = $false
        licenseConcluded = 'NOASSERTION'
        licenseDeclared = 'NOASSERTION'
        copyrightText = 'NOASSERTION'
    }
    $relationships += [ordered]@{
        spdxElementId = $appSpdxId
        relationshipType = 'DEPENDS_ON'
        relatedSpdxElement = $id
    }
}

$document = [ordered]@{
    spdxVersion = 'SPDX-2.3'
    dataLicense = 'CC0-1.0'
    SPDXID = 'SPDXRef-DOCUMENT'
    name = "OpenMicBoard-$Version"
    documentNamespace = "https://github.com/vounder/OpenMicBoard/releases/download/v$Version/OpenMicBoard-$Version.spdx.json"
    creationInfo = [ordered]@{
        created = [DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ')
        creators = @('Tool: OpenMicBoard Build-Release.ps1')
    }
    packages = $packages
    relationships = $relationships
}

$parent = Split-Path -Parent $OutputPath
New-Item -ItemType Directory -Force -Path $parent | Out-Null
[System.IO.File]::WriteAllText(
    $OutputPath,
    ($document | ConvertTo-Json -Depth 8),
    [System.Text.UTF8Encoding]::new($false))

Write-Host "Generated SPDX SBOM: $OutputPath"
