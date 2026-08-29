param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

$projectRoot = $PSScriptRoot
$projectFile = Join-Path $projectRoot "NorthIslandChestPlugin.csproj"
$outputDirectory = Join-Path $projectRoot "bin\$Configuration"
$stagingDirectory = Join-Path $outputDirectory "PackageStaging"
$packagePath = Join-Path $outputDirectory "latest.zip"

dotnet restore $projectFile --locked-mode
dotnet build $projectFile -c $Configuration --no-restore

if (Test-Path -LiteralPath $stagingDirectory) {
    Remove-Item -LiteralPath $stagingDirectory -Recurse -Force
}
New-Item -ItemType Directory -Path $stagingDirectory | Out-Null

$packageFiles = @(
    "NorthIslandChestPlugin.dll",
    "NorthIslandChestPlugin.json",
    "OmenTools.dll",
    "GuerrillaNtp.dll",
    "TinyPinyin.dll"
)

foreach ($fileName in $packageFiles) {
    $source = Join-Path $outputDirectory $fileName
    if (-not (Test-Path -LiteralPath $source)) {
        throw "Release dependency is missing: $source"
    }
    Copy-Item -LiteralPath $source -Destination $stagingDirectory
}

$omenToolsLicense = Join-Path $projectRoot "dependencies\OmenTools\LICENSE"
if (-not (Test-Path -LiteralPath $omenToolsLicense)) {
    throw "OmenTools license is missing: $omenToolsLicense"
}
Copy-Item -LiteralPath $omenToolsLicense -Destination (Join-Path $stagingDirectory "OmenTools.LICENSE")

if (Test-Path -LiteralPath $packagePath) {
    Remove-Item -LiteralPath $packagePath -Force
}
Compress-Archive -Path (Join-Path $stagingDirectory "*") -DestinationPath $packagePath -CompressionLevel Optimal

$assemblyVersion = [Reflection.AssemblyName]::GetAssemblyName(
    (Join-Path $stagingDirectory "NorthIslandChestPlugin.dll")
).Version
$manifest = Get-Content (Join-Path $stagingDirectory "NorthIslandChestPlugin.json") -Raw -Encoding UTF8 | ConvertFrom-Json
if ([string]$assemblyVersion -ne [string]$manifest.AssemblyVersion) {
    throw "Assembly version $assemblyVersion does not match manifest version $($manifest.AssemblyVersion)."
}

Write-Host "Created $packagePath"
Write-Host "Assembly version: $assemblyVersion"
Write-Host "SHA-256: $((Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash)"
