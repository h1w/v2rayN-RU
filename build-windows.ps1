$ErrorActionPreference = 'Stop'

try {
    $output = Join-Path $PSScriptRoot 'publish\win-x64'
    $cores = Join-Path $PSScriptRoot 'bin'
    foreach ($relative in @('xray\xray.exe', 'sing_box\sing-box.exe')) {
        if (-not (Test-Path -LiteralPath (Join-Path $cores $relative) -PathType Leaf)) {
            throw "Missing core: bin\$relative. Place the core in the repository bin directory first."
        }
    }

    $commonArgs = @(
        '-c', 'Release', '-r', 'win-x64',
        '--self-contained', 'true',
        '-p:PublishSingleFile=true', '-o', $output
    )

    $desktop = Join-Path $PSScriptRoot 'v2rayN\v2rayN.Desktop\v2rayN.Desktop.csproj'
    & dotnet publish $desktop @commonArgs
    if ($LASTEXITCODE -ne 0) { throw "Desktop publish failed (exit $LASTEXITCODE)." }

    $tool = Join-Path $PSScriptRoot 'v2rayN\AmazTool\AmazTool.csproj'
    & dotnet publish $tool @commonArgs '-p:PublishTrimmed=true'
    if ($LASTEXITCODE -ne 0) { throw "AmazTool publish failed (exit $LASTEXITCODE)." }

    $destination = Join-Path $output 'bin'
    New-Item -ItemType Directory -Path $destination -Force | Out-Null
    Get-ChildItem -LiteralPath $cores -Force | Copy-Item -Destination $destination -Recurse -Force

    Write-Host ""
    Write-Host "Build ready: $output"
    Write-Host "Run: $(Join-Path $output 'v2rayN-RU.exe')"
    Write-Host 'Existing settings are not copied or deleted. Run the application as administrator for TUN.'
}
catch {
    Write-Error $_ -ErrorAction Continue
    exit 1
}
