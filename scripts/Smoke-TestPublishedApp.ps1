[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PublishDirectory,

    [ValidateRange(1, 30)]
    [int]$Seconds = 5
)

$ErrorActionPreference = "Stop"
$executable = Join-Path (Resolve-Path -LiteralPath $PublishDirectory) "OpenMicBoard.exe"
if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
    throw "Published executable not found: $executable"
}

$process = Start-Process -FilePath $executable -PassThru
try {
    Start-Sleep -Seconds $Seconds
    if ($process.HasExited) {
        throw "OpenMicBoard exited during launch smoke test with code $($process.ExitCode)."
    }
}
finally {
    if (-not $process.HasExited) {
        Stop-Process -Id $process.Id -Force
        $process.WaitForExit()
    }
}

Write-Host "Launch smoke test passed: $executable"
