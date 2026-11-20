param([string]$Version = '1.0.0', [switch]$SkipTests)
$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw 'Expected a version such as 1.0.0.' }
$repo = Split-Path -Parent $PSScriptRoot

function Invoke-Dotnet {
    & dotnet @args
    if ($LASTEXITCODE) { throw "dotnet failed ($LASTEXITCODE): $args" }
}

Push-Location $repo
try {
    & (Join-Path $PSScriptRoot 'fetch-models.ps1')
    if (!$SkipTests) {
        Invoke-Dotnet test src/PhotoPrintStudio.slnx -c Release '-m:1' '-p:RestoreLockedMode=true' --verbosity minimal
    }
    $project = 'src/PhotoPrintStudio.App/PhotoPrintStudio.App.csproj'
    $dist = Join-Path $repo 'dist'
    $name = "PhotoPrintStudio-$Version-win-x64"
    $zip = Join-Path $dist "$name.zip"
    $work = Join-Path $repo ('artifacts\release-' + [Guid]::NewGuid().ToString('N'))
    $package = Join-Path $work 'PhotoPrintStudio_Portable'
    $publishArgs = @(
        'publish', $project, '-c', 'Release', '-m:1', '-r', 'win-x64', '--self-contained', 'true',
        "-p:Version=$Version", '-p:PpsWindowsRelease=true', '-p:RestoreLockedMode=true', '-p:ContinuousIntegrationBuild=true',
        '-p:PublishSingleFile=true', '-p:EnableCompressionInSingleFile=true',
        '-p:DebugType=None', '-p:DebugSymbols=false', '-o', $package
    )
    Invoke-Dotnet @publishArgs
    # Native NuGet packages may publish linker inputs and debug symbols alongside runtime DLLs.
    Get-ChildItem -LiteralPath $package -File |
        Where-Object Extension -in '.lib', '.pdb' |
        Remove-Item

    $modelTarget = Join-Path $package 'portable_data\models'
    New-Item -ItemType Directory -Path $modelTarget -Force | Out-Null
    $manifestPath = Join-Path $repo 'assets\models\models.manifest.json'
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    Copy-Item -LiteralPath $manifestPath -Destination $modelTarget
    foreach ($model in $manifest.models) {
        Copy-Item -LiteralPath (Join-Path $repo "assets\models\$($model.fileName)") -Destination $modelTarget
    }

    Copy-Item -LiteralPath 'LICENSE','THIRD-PARTY-NOTICES.md','docs/使用手册.md','assets/portable/启动诊断.cmd' -Destination $package
    $licenseDir = Join-Path $package 'assets\licenses'
    New-Item -ItemType Directory -Path (Split-Path -Parent $licenseDir) -Force | Out-Null
    Copy-Item -LiteralPath 'assets/licenses' -Destination $licenseDir -Recurse
    $appAssets = Get-Content -LiteralPath 'src/PhotoPrintStudio.App/obj/project.assets.json' -Raw | ConvertFrom-Json
    foreach ($library in $appAssets.libraries.PSObject.Properties) {
        if ($library.Value.type -ne 'package' -or $library.Name -match '^(Microsoft\.NET\.ILLink\.Tasks|SkiaSharp\.NativeAssets\.macOS)/') { continue }
        $notices = @($library.Value.files | Where-Object { $_ -match '(?i)^(license|third[-_ ]?party[-_ ]?notices|notice)(\.[a-z]+)?$' })
        if (!$notices.Count) { continue }
        foreach ($packageFolder in $appAssets.packageFolders.PSObject.Properties.Name) {
            $source = Join-Path $packageFolder $library.Value.path
            if (!(Test-Path -LiteralPath $source)) { continue }
            $dest = New-Item -ItemType Directory -Path (Join-Path $licenseDir $library.Name.Replace('/','-')) -Force
            foreach ($notice in $notices) { Copy-Item -LiteralPath (Join-Path $source $notice) -Destination $dest.FullName }
            break
        }
    }
    @"
光影印坊 PhotoPrintStudio
完整解压后运行 PhotoPrintStudio.exe。应用自带 .NET，无需另装 .NET。
若 Windows 缺少 Visual C++ x64 运行库，请从 Microsoft 下载。
https://learn.microsoft.com/cpp/windows/latest-supported-vc-redist
详细操作见使用手册。处理数据保存在本目录下的 portable_data 中。
"@ | Set-Content -LiteralPath (Join-Path $package '使用说明.txt') -Encoding utf8

    New-Item -ItemType Directory -Path $dist -Force | Out-Null
    $stagedZip = Join-Path $work "$name.zip"
    Compress-Archive -LiteralPath $package -DestinationPath $stagedZip -CompressionLevel Optimal
    [IO.File]::Move($stagedZip, $zip, $true)
    $hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $name.zip" | Set-Content -LiteralPath (Join-Path $dist "$name.sha256") -Encoding ascii
    Write-Output $zip
}
finally {
    Pop-Location
}
