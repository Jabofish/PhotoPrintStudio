$ErrorActionPreference = 'Stop'
$modelDir = Join-Path $PSScriptRoot '..\assets\models'
$manifest = Get-Content -LiteralPath (Join-Path $modelDir 'models.manifest.json') -Raw | ConvertFrom-Json
foreach ($model in $manifest.models) {
    $target = Join-Path $modelDir $model.fileName
    if ((Test-Path -LiteralPath $target -PathType Leaf) -and
        (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -eq $model.sha256) { continue }
    $pending = "$target.download"
    try {
        Invoke-WebRequest -Uri $model.downloadUrl -OutFile $pending -MaximumRetryCount 2 -RetryIntervalSec 2
        if ((Get-FileHash -LiteralPath $pending -Algorithm SHA256).Hash -ne $model.sha256) {
            throw "SHA-256 mismatch: $($model.fileName)"
        }
        [IO.File]::Move($pending, $target, $true)
    }
    finally {
        if (Test-Path -LiteralPath $pending) { Remove-Item -LiteralPath $pending }
    }
}
