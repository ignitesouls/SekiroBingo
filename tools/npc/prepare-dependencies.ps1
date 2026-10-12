[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$dependencyDirectory = Join-Path $PSScriptRoot '.deps'
New-Item -ItemType Directory -Path $dependencyDirectory -Force | Out-Null
$sources = @(
    @{ Name = 'soulsformats'; Repository = 'soulsmods/SoulsFormats'; Revision = 'ee1dd61958f60bdc51ce3da548e9a90a8ab39905' },
    @{ Name = 'esdlang'; Repository = 'thefifthmatt/ESDLang'; Revision = '9fa7fa90849a662b36839f077e321a02f9e992f4' }
)
foreach ($source in $sources) {
    $destination = Join-Path $dependencyDirectory $source.Name
    $revisionFile = Join-Path $destination '.revision'
    if (Test-Path -LiteralPath $destination) {
        if (-not (Test-Path -LiteralPath $revisionFile) -or
            [IO.File]::ReadAllText($revisionFile).Trim() -ne $source.Revision) {
            throw ('Cached dependency revision does not match: ' + $destination + '. Refresh this development cache before continuing.')
        }
        continue
    }
    $archive = Join-Path $dependencyDirectory ($source.Name + '.zip')
    Invoke-WebRequest ('https://codeload.github.com/' + $source.Repository + '/zip/' + $source.Revision) -OutFile $archive
    $extract = Join-Path $dependencyDirectory ([guid]::NewGuid().ToString('N'))
    Expand-Archive -LiteralPath $archive -DestinationPath $extract
    $checkout = @(Get-ChildItem -LiteralPath $extract -Directory)
    if ($checkout.Count -ne 1) { throw 'Dependency archive must contain one root directory.' }
    $allowedRoot = [IO.Path]::GetFullPath($dependencyDirectory).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    foreach ($movePath in @($checkout[0].FullName, $destination)) {
        if (-not [IO.Path]::GetFullPath($movePath).StartsWith($allowedRoot, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Dependency move must remain inside the development dependency directory.'
        }
    }
    Move-Item -LiteralPath $checkout[0].FullName -Destination $destination
    [IO.File]::WriteAllText($revisionFile, $source.Revision)
}
# The staged ESDLang revision contains an unused AC6 namespace import that
# is absent in its pinned SoulsFormats revision. Remove only that import.
$infixor = Join-Path $dependencyDirectory 'esdlang/ESDLang/EzSemble/EzInfixor.cs'
$infixorText = [IO.File]::ReadAllText($infixor)
$infixorText = $infixorText.Replace('using static SoulsFormats.MSBAC6.Event;', '')
[IO.File]::WriteAllText($infixor, $infixorText)
$definition = Join-Path $dependencyDirectory 'ActionButtonParam.xml'
if (-not (Test-Path -LiteralPath $definition)) {
    Invoke-WebRequest 'https://raw.githubusercontent.com/thefifthmatt/SoulsRandomizers/c1353d04f3d7cd0724e5f03d2743bf7d93d238b9/dists/Defs/ActionButtonParam.xml' -OutFile $definition
}
if ((Get-FileHash -LiteralPath $definition -Algorithm SHA256).Hash -ne '90EA2EEA85DD7B49F6C28470685ED1041E3EA1D7FC19BAD4A23C40FB61676B98') {
    throw 'Cached ActionButtonParam definition hash does not match its pinned source. Refresh this development cache before continuing.'
}
Write-Host 'Pinned NPC development dependencies are ready.'
