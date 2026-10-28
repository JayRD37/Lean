[CmdletBinding()]
param(
    [Parameter()]
    [string] $DestinationDirectory = (Join-Path (Split-Path $PSScriptRoot -Parent) "plugins/alpaca")
)

$ErrorActionPreference = "Stop"

# Pin the official plugin source and its LEAN package dependencies so paper deployments
# can be rebuilt from the same source and package versions.
$pluginRepository = "https://github.com/QuantConnect/Lean.Brokerages.Alpaca.git"
$pluginCommit = "b92d6ecd391d5a79e91b060668d1ea4fafec9b89"
$leanPackageVersion = "2.5.18208"
$repositoryRoot = Split-Path $PSScriptRoot -Parent
$tempRoot = Join-Path ([IO.Path]::GetTempPath()) "lean-alpaca-plugin-$pluginCommit"
$sourceDirectory = Join-Path (Split-Path $repositoryRoot -Parent) "Lean.Brokerages.Alpaca-$pluginCommit"
$publishDirectory = Join-Path $tempRoot ("publish-" + [Guid]::NewGuid().ToString("N"))
$projectPath = Join-Path $sourceDirectory "QuantConnect.AlpacaBrokerage/QuantConnect.AlpacaBrokerage.csproj"

$sdks = & dotnet --list-sdks
if ($LASTEXITCODE -ne 0 -or -not ($sdks | Where-Object { $_ -match '^10\.' })) {
    throw "The LEAN Alpaca plugin requires the .NET 10 SDK. Install the SDK pinned by global.json and rerun this script."
}

if (-not (Test-Path $sourceDirectory)) {
    New-Item -ItemType Directory -Path $tempRoot -Force | Out-Null
    & git clone --no-checkout $pluginRepository $sourceDirectory
    if ($LASTEXITCODE -ne 0) { throw "Unable to clone the official LEAN Alpaca brokerage plugin." }
    & git -C $sourceDirectory fetch --depth 1 origin $pluginCommit
    if ($LASTEXITCODE -ne 0) { throw "Unable to fetch pinned Alpaca plugin commit $pluginCommit." }
}

$checkedOutCommit = (& git -C $sourceDirectory rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $checkedOutCommit -ne $pluginCommit) {
    & git -C $sourceDirectory fetch --depth 1 origin $pluginCommit
    if ($LASTEXITCODE -ne 0) { throw "Unable to fetch pinned Alpaca plugin commit $pluginCommit." }
    & git -C $sourceDirectory checkout --detach $pluginCommit
    if ($LASTEXITCODE -ne 0) { throw "Unable to check out pinned Alpaca plugin commit $pluginCommit." }
}

$projectText = [IO.File]::ReadAllText($projectPath)
$pinnedProjectText = $projectText.Replace('Version="2.5.*"', "Version=`"$leanPackageVersion`"")
if ($pinnedProjectText -eq $projectText -and -not $projectText.Contains("Version=`"$leanPackageVersion`"")) {
    throw "The official plugin project no longer has the expected floating LEAN package references; review and update the installer pin."
}
if ($pinnedProjectText -ne $projectText) {
    [IO.File]::WriteAllText($projectPath, $pinnedProjectText, [Text.UTF8Encoding]::new($false))
}

New-Item -ItemType Directory -Path $publishDirectory -Force | Out-Null
& dotnet publish $projectPath --configuration Release --framework net10.0 --no-self-contained --output $publishDirectory
if ($LASTEXITCODE -ne 0) { throw "Building the pinned LEAN Alpaca brokerage plugin failed." }

$requiredFiles = @("QuantConnect.Brokerages.Alpaca.dll", "Alpaca.Markets.dll")
foreach ($requiredFile in $requiredFiles) {
    if (-not (Test-Path (Join-Path $publishDirectory $requiredFile))) {
        throw "The Alpaca plugin build did not produce required file '$requiredFile'."
    }
}

$destination = [IO.Path]::GetFullPath($DestinationDirectory)
$manifestPath = Join-Path $destination ".lean-alpaca-plugin-manifest.json"
if (Test-Path $destination) {
    $existingFiles = @(Get-ChildItem -LiteralPath $destination -File -Force)
    if ($existingFiles.Count -gt 0) {
        if (-not (Test-Path $manifestPath)) {
            throw "Destination '$destination' contains files not managed by this installer; choose an empty dedicated plugin directory."
        }
        $oldManifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
        $managedSet = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($oldName in $oldManifest.files) { [void]$managedSet.Add([string]$oldName) }
        foreach ($existingFile in $existingFiles) {
            if (-not $managedSet.Contains($existingFile.Name)) {
                throw "Destination '$destination' contains unmanaged file '$($existingFile.Name)'; choose an empty dedicated plugin directory."
            }
        }
        foreach ($oldName in $oldManifest.files) {
            if ([IO.Path]::GetFileName([string]$oldName) -ne [string]$oldName) {
                throw "The existing plugin manifest contains an unsafe path; refusing to replace files."
            }
            $oldPath = Join-Path $destination ([string]$oldName)
            if (Test-Path $oldPath -PathType Leaf) { Remove-Item -LiteralPath $oldPath -Force }
        }
    }
}
else {
    New-Item -ItemType Directory -Path $destination -Force | Out-Null
}

$publishedFiles = @(Get-ChildItem -LiteralPath $publishDirectory -File | Sort-Object Name)
foreach ($file in $publishedFiles) {
    Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $destination $file.Name) -Force
}
Copy-Item -LiteralPath (Join-Path $sourceDirectory "README.md") -Destination (Join-Path $destination "UPSTREAM-README.md") -Force
$managedNames = @($publishedFiles.Name) + "UPSTREAM-README.md" + ".lean-alpaca-plugin-manifest.json"
$manifest = [ordered]@{
    repository = $pluginRepository
    commit = $pluginCommit
    leanPackageVersion = $leanPackageVersion
    files = $managedNames
}
$manifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $manifestPath -Encoding utf8
Write-Host "Installed official LEAN Alpaca plugin $pluginCommit to $destination"
