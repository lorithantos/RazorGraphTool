<#
.SYNOPSIS
    Regenerate lightroom-classic.json from official Lightroom Classic SDK folders.

.DESCRIPTION
    The catalogue records which Lr* modules exist in which SDK version, so an
    import can be classified as a known external module, dated to a minimum SDK
    version, or flagged as absent from every version we know.

    It is generated rather than hand-maintained because it is derived data: the
    module surface is the set of Lr*-prefixed pages under
    "<SDK>/API Reference/modules". A hand-edited copy would drift silently, which
    is the failure the catalogue exists to avoid.

    Adding a newly downloaded SDK is: unzip it beside the others, re-run this,
    commit the JSON.

    THE SOURCE, for the next update:
        https://developer.adobe.com/console/73617/servicesandapis
    Sign in with an Adobe ID, choose Lightroom Classic, View Downloads. Adobe does
    not publish the API Reference on the web at all -- it exists only inside that
    zip -- and there is no unauthenticated download URL, so this script cannot and
    must not fetch it. A human downloads it; this script only reads what they got.

    TRUST BOUNDARY. -SdkRoot is caller-supplied and its contents are untrusted:
    every directory named "Lightroom SDK *" is read, and file names inside become
    entries in a catalogue the analyzer later treats as authoritative. That is a
    privilege escalation if left unchecked -- an arbitrary file name would be
    promoted to "known Adobe SDK module". It is not hypothetical: the first
    version of this script admitted six documentation pages ("LrView child layout
    properties" and kin) as modules, none of which can be an import target.
    So names are validated against a full-anchored Lua identifier pattern, and
    anything rejected is REPORTED rather than quietly dropped. Output is confined
    to this script's own directory for the same reason.

    PROVENANCE. The folder name is caller-controlled, so it cannot be what decides
    which version's data is catalogued -- anyone can name a directory "Lightroom
    SDK 99.0". Each folder must carry the Readme.txt that ships inside the
    official SDK, and the version it declares must match the folder name; a
    missing or disagreeing Readme is a hard failure, not a warning. The declared
    line is recorded in the catalogue so a reader can see what each version's data
    was derived from rather than trusting that it was official.

    Use ONLY SDKs obtained from Adobe's Developer Console. Third-party mirrors and
    legacy download hosts are out of bounds however reachable they are: the
    catalogue's entire value is being authoritative about what Adobe ships, and
    content of unverifiable origin destroys exactly that.

.PARAMETER SdkRoot
    Folder containing "Lightroom SDK <version>" directories.

.PARAMETER OutputFileName
    File name to write, within this script's directory. A name only -- paths are
    rejected, so this cannot be aimed at an arbitrary location.

.PARAMETER NewestKnownRelease
    The newest SDK Adobe has shipped, catalogued here or not. Recorded so the
    catalogue states how far behind it is instead of implying completeness.

.EXAMPLE
    ./Update-LightroomCatalog.ps1 -SdkRoot 'C:\Users\lori_\Documents\LR-Lua\LR-Lua'
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Container }, ErrorMessage = 'SdkRoot must be an existing directory.')]
    [string] $SdkRoot,

    # A bare file name. Rejecting separators and traversal keeps an arbitrary
    # caller from turning a catalogue refresh into an arbitrary file write.
    [ValidatePattern('^[A-Za-z0-9._-]+\.json$')]
    [string] $OutputFileName = 'lightroom-classic.json',

    [ValidatePattern('^\d+\.\d+$')]
    [string] $NewestKnownRelease = '15.3'
)

# Pinned, not Latest: "Latest" is a moving target, so a script that passes today
# can change behaviour under a future engine without being edited.
Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'

# A module name must be something `import` can actually take. Full-anchored:
# an unanchored prefix test is what admitted the six LrView doc pages.
$ModuleNamePattern = '^Lr[A-Za-z0-9]+$'

$outputPath = Join-Path $PSScriptRoot $OutputFileName

$sdkDirs = @(Get-ChildItem -LiteralPath $SdkRoot -Directory -Filter 'Lightroom SDK *')
if ($sdkDirs.Count -eq 0) { throw "No 'Lightroom SDK <version>' folders under $SdkRoot" }

# Sorted as VERSIONS, not strings: '14.0' sorts before '3.0' lexically, which
# would make firstCataloguedIn name the wrong release for every module.
$versions = [ordered] @{}
$rejected = [ordered] @{}
$provenance = [ordered] @{}
$observed = [ordered] @{}

foreach ($dir in ($sdkDirs | Sort-Object { [version] ($_.Name -replace '^Lightroom SDK\s+', '') })) {
    $version = $dir.Name -replace '^Lightroom SDK\s+', ''

    # The package must attest to its own version. Without this the folder name --
    # which any caller controls -- would decide what gets catalogued as official
    # Adobe API surface.
    $readmePath = Join-Path $dir.FullName 'Readme.txt'
    if (-not (Test-Path -LiteralPath $readmePath -PathType Leaf)) {
        throw "SDK folder '$($dir.Name)' has no Readme.txt. Only official SDK downloads carry one, and without it the folder's version claim is unverifiable."
    }

    # Older SDK readmes are Windows-1252, newer ones UTF-8. Reading a 1252 file as
    # UTF-8 yields replacement characters, which would be committed into the
    # provenance record as mojibake -- so fall back when that is what we got.
    $readmeHead = @(Get-Content -LiteralPath $readmePath -TotalCount 20)
    if ($readmeHead -match "�") {
        $readmeHead = @(Get-Content -LiteralPath $readmePath -TotalCount 20 -Encoding latin1)
    }

    $attestation = ($readmeHead | Where-Object { $_ -match 'Software Development Kit' } | Select-Object -First 1)
    if (-not $attestation) {
        throw "SDK folder '$($dir.Name)' has a Readme.txt with no 'Software Development Kit' line; it does not look like an official SDK package."
    }

    $attestation = $attestation.Trim()
    if ($attestation -notmatch '(?<declared>\d+\.\d+)\s+Software Development Kit') {
        throw "Could not read a version out of '$($dir.Name)' Readme.txt line: $attestation"
    }
    if ($Matches.declared -ne $version) {
        throw "Provenance mismatch: folder '$($dir.Name)' declares version $($Matches.declared) in its Readme.txt. Rename the folder to match the package, or use the package the folder claims."
    }

    # Newer packages also stamp the exact build they were cut from, which pins the
    # data far more precisely than a version number. Older ones do not; absence is
    # normal and not a provenance failure.
    $entry = [ordered] @{ declares = $attestation }
    $buildLine = ($readmeHead | Where-Object { $_ -match 'Build:\s*"(?<build>[^"]+)"' } | Select-Object -First 1)
    if ($buildLine -and $buildLine -match 'Build:\s*"(?<build>[^"]+)"') { $entry['build'] = $Matches.build }

    $provenance[$version] = $entry

    # Modules Adobe SHIPS AND USES but never documented. The API Reference is not
    # the whole surface: LrController and LrTableUtils are imported by Adobe's own
    # sample plug-ins and appear on no reference page -- LrController is absent
    # from both SDK guides too, so its only evidence of existing is that Adobe's
    # code imports it.
    #
    # Read from the samples rather than from the PDFs on purpose. The guides are
    # prose whose extracted text runs words together across layout boundaries
    # ("LrDialogsAllows", "LrSdkVersionRequirednumberThe"), and feeding those into
    # a catalogue of real modules would be the same promotion-of-noise this script
    # already guards against. The samples are Lua, where `import "X"` is exact.
    $sampleDir = Join-Path $dir.FullName 'Sample Plugins'
    if (Test-Path -LiteralPath $sampleDir -PathType Container) {
        foreach ($lua in Get-ChildItem -LiteralPath $sampleDir -Recurse -Filter '*.lua') {
            $text = Get-Content -LiteralPath $lua.FullName -Raw
            foreach ($m in [regex]::Matches($text, 'import\s*\(?\s*["''](?<name>Lr[A-Za-z0-9]+)["'']')) {
                $name = $m.Groups['name'].Value
                if (-not $observed.Contains($name)) { $observed[$name] = [ordered] @{} }
                if (-not $observed[$name].Contains($version)) {
                    $observed[$name][$version] = (Resolve-Path -LiteralPath $lua.FullName -Relative -RelativeBasePath $dir.FullName)
                }
            }
        }
    }

    $moduleDir = Join-Path $dir.FullName 'API Reference\modules'
    if (-not (Test-Path -LiteralPath $moduleDir -PathType Container)) {
        Write-Warning "SDK $version has no 'API Reference\modules' folder; skipped."
        continue
    }

    $pages = @(Get-ChildItem -LiteralPath $moduleDir -Filter '*.html' |
        ForEach-Object { [IO.Path]::GetFileNameWithoutExtension($_.Name) })

    $versions[$version] = @($pages | Where-Object { $_ -cmatch $ModuleNamePattern } | Sort-Object -Unique)

    # Everything the filter refused, kept so the run can show its own losses.
    # A silent drop here reads as "that SDK had fewer modules".
    $skipped = @($pages | Where-Object { $_ -cnotmatch $ModuleNamePattern } | Sort-Object -Unique)
    if ($skipped.Count -gt 0) { $rejected[$version] = $skipped }

    Write-Verbose "SDK $version : $($versions[$version].Count) modules, $($skipped.Count) non-module pages"
}

if ($versions.Count -eq 0) { throw "No SDK folder under $SdkRoot contained an 'API Reference\modules' directory." }

$ordered = @($versions.Keys)
$newest = $ordered[-1]

$modules = [ordered] @{}
foreach ($name in (($versions.Values | ForEach-Object { $_ }) | Sort-Object -Unique)) {
    # @() forces an array: with a single matching version, $present[-1] would
    # index into the STRING, so '3.0' would yield the character '0'.
    $present = @($ordered | Where-Object { $versions[$_] -contains $name })

    $entry = [ordered] @{ firstCataloguedIn = $present[0] }
    if ($present[-1] -ne $newest) { $entry['absentAfter'] = $present[-1] }
    $modules[$name] = $entry
}

# Observed in official sample code but on no reference page. Kept separate from
# modules: these are real -- Adobe's own plug-ins import them -- but nothing
# documents their surface, so a caller should know they are working blind.
$undocumented = [ordered] @{}
foreach ($name in ($observed.Keys | Sort-Object)) {
    if ($modules.Contains($name)) { continue }
    $undocumented[$name] = [ordered] @{
        seenIn   = @($observed[$name].Keys)
        evidence = @($observed[$name].GetEnumerator() | ForEach-Object { "$($_.Key): $($_.Value)" })
    }
}

$doc = [ordered] @{
    '.comment'              = @(
        'Known module surface of the Adobe Lightroom Classic SDK, per catalogued version.',
        'CLASSIC ONLY. The SDK readme titles itself "Adobe Lightroom Classic Software Development Kit". Cloud Lightroom is a different product with a REST API and no Lua plug-in model; nothing here applies to it.',
        'Generated by Update-LightroomCatalog.ps1 from file names under "<SDK>/API Reference/modules", accepted only if they match ^Lr[A-Za-z0-9]+$. Do not hand-edit: re-run the script.',
        'firstCataloguedIn is the earliest version IN THIS CATALOGUE containing the module, NOT the release that introduced it. A module marked with the oldest catalogued version may be far older.',
        'absentAfter marks a module present in an earlier catalogued version and gone from the newest -- a removal or rename, and a real compatibility signal.',
        'INCOMPLETE BY CONSTRUCTION: only catalogedVersions were read, and Adobe has shipped beyond them. A module missing here may simply be newer, so an unrecognised import must be reported as unknown-to-this-catalogue, never as nonexistent.',
        'UNDOCUMENTED MODULES: undocumentedModules holds namespaces imported by Adobe''s own sample plug-ins that appear on no API Reference page. They are real and usable, but no published documentation describes them, so anything built on them is built on observation. Read from the sample sources, not from the SDK guide PDFs, whose extracted text runs words together and would poison this list with fragments.',
        'PROVENANCE: sourced only from SDK packages downloaded from Adobe''s Developer Console (https://developer.adobe.com/console/73617/servicesandapis), which requires an Adobe ID. Each entry in provenance is the version line the package states about ITSELF, checked against the folder it was read from. Third-party mirrors and legacy download hosts are not acceptable sources: a catalogue claiming to be authoritative about Adobe''s API surface is worth nothing if its contents came from somewhere unverifiable.'
    )
    name                    = 'lightroom-classic'
    product                 = 'Adobe Lightroom Classic SDK'
    variant                 = 'classic'
    catalogedVersions       = $ordered
    newestCataloguedVersion = $newest
    newestKnownRelease      = $NewestKnownRelease
    provenance              = $provenance
    modulePrefix            = 'Lr'
    modules                 = $modules
    undocumentedModules     = $undocumented
}

$doc | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $outputPath -Encoding utf8

[pscustomobject] @{
    output            = $outputPath
    catalogedVersions = $ordered
    modules           = $modules.Count
    removed           = @($modules.GetEnumerator() | Where-Object { $_.Value.Contains('absentAfter') } | ForEach-Object { $_.Key })
    # Reported, not hidden: these are pages the filter refused, and a reviewer
    # needs to see them to know the filter is refusing the right things.
    rejectedPages     = $rejected
} | ConvertTo-Json -Depth 4
