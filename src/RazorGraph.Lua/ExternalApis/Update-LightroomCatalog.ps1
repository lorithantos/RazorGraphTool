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
    [string] $NewestKnownRelease = '15.3',

    # Pre-extracted guide text, if any. Optional because extraction is a separate
    # concern with its own tooling, quality and licensing questions -- without it
    # the catalogue still ships sample-derived manifest keys and simply says the
    # guide contributed nothing.
    [string] $GuideTextDir
)

# Pinned, not Latest: "Latest" is a moving target, so a script that passes today
# can change behaviour under a future engine without being edited.
Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'

# A module name must be something `import` can actually take. Full-anchored:
# an unanchored prefix test is what admitted the six LrView doc pages.
$ModuleNamePattern = '^Lr[A-Za-z0-9]+$'

<#
.SYNOPSIS
    The values a parameter accepts, when the docs enumerate them.
.DESCRIPTION
    Only where an ENUMERATION LEAD-IN says so. That restriction is the whole
    function, and it is not caution for its own sake -- it was measured.

    The SDK carries 112 <ul><li><b>name</b>: lists, and the MAJORITY do not
    enumerate legal argument values at all. The most common lead-ins are
    "A table with these fields" (26) and "in which each element contains" (10):
    those describe the SHAPE OF A RETURN VALUE. Harvesting them as accepted
    argument values would make a checker reject correct code -- a false Error on
    working code, which is worse than no check at all.

    So the lead-in is an allowlist, and every phrase in it was read out of the
    reference rather than guessed:

      "The following keys are recognized"        photo:setRawMetadata     (65 keys)
      "These valid keys return these values"     photo:getRawMetadata     (55)
                                                 photo:getFormattedMetadata (98)
      "Valid values are"                         LrCatalog
      "one of the following strings"             LrLogger

    The phrasing differs per accessor, which is why a single phrase does not do:
    searching only for "valid values" finds LrCatalog and misses all 218
    metadata keys, the vocabulary plug-in code touches constantly.

    A parameter with no recognised lead-in yields nothing, and absence must be
    read as "the docs do not enumerate this", never as "anything is accepted".
#>
function Read-EnumeratedValues {
    [CmdletBinding()]
    param([Parameter(Mandatory)] [string] $ParamBody)

    # Anchored on the lead-in and only reading the list that FOLLOWS it, so a
    # parameter that both enumerates values and later describes a return shape
    # contributes only the former.
    $leadIn = 'The following keys are recognized|These valid keys return these values|Valid values are|one of the following strings'

    # Returns an ordered dictionary on every path, name -> {} or { since }. NOT
    # comma-wrapped: the wrap exists to stop an ARRAY unrolling on return, and a
    # dictionary does not unroll -- wrapping one nests it in an array instead,
    # which is the opposite of the intent (house rule 1).
    $start = [regex]::Match($ParamBody, $leadIn)
    if (-not $start.Success) { return [ordered] @{} }

    # ONLY the list the lead-in introduces, and only if it introduces one at all.
    #
    # Anchoring on the lead-in and then reading every list after it is wrong, and
    # measurably so: LrCatalog's findPhotos says "Valid values are:" and follows
    # it with quoted values in PROSE ("rating", "pick", "labelColor"), while a
    # later list in the same parameter describes the criterion table's own fields.
    # Taking the tail harvested operation/value/value2/value_units -- field names
    # presented as accepted argument values, which is the false Error this
    # function exists to prevent.
    $afterLeadIn = $start.Index + $start.Length
    $open = $ParamBody.IndexOf('<ul>', $afterLeadIn, [StringComparison]::Ordinal)
    if ($open -lt 0) { return [ordered] @{} }

    # The list must FOLLOW the lead-in, not merely appear somewhere after it.
    # Anything but whitespace and a closing tag or two in between means the
    # lead-in was introducing prose and this list belongs to something else.
    $between = $ParamBody.Substring($afterLeadIn, $open - $afterLeadIn) -replace '<[^>]+>', ''
    if ($between.Trim().Length -gt 8) { return [ordered] @{} }

    # The vocabulary is spread over SEVERAL lists, not one. setRawMetadata holds
    # five: a base list, then continuations introduced by "The following items are
    # first supported in version 3.0 / 4.0 / 6.0 / 13.2". Stopping at the first
    # </ul> loses two thirds of the keys -- including pickStatus, which the
    # existing plug-in sets -- so a checker built on it would reject working code.
    #
    # Each continuation's version is kept rather than flattened away: a key added
    # in 13.2 is not valid for a plug-in declaring an older LrSdkMinimumVersion,
    # and that is the same question minimumSdkVersion already answers elsewhere.
    $values = [ordered] @{}
    $cursor = $open
    $since = $null

    while ($cursor -ge 0) {
        $close = $ParamBody.IndexOf('</ul>', $cursor, [StringComparison]::Ordinal)
        $listBody = if ($close -gt $cursor) { $ParamBody.Substring($cursor, $close - $cursor) } else { $ParamBody.Substring($cursor) }

        # <li><b>name</b>: -- the colon is load-bearing. Prose lists in these pages
        # contain <li> items too, and the bolded-name-then-colon shape is what
        # marks a defined term rather than a sentence.
        foreach ($m in [regex]::Matches($listBody, '<li>\s*<b>(?<v>[A-Za-z][A-Za-z0-9_]*)</b>\s*:')) {
            $name = $m.Groups['v'].Value
            if ($values.Contains($name)) { continue }

            $values[$name] = if ($since) { [ordered] @{ since = $since } } else { [ordered] @{} }
        }

        if ($close -lt 0) { break }

        # Continue only into a list the docs introduce as a continuation of this
        # same vocabulary. Every gap between the metadata accessors' lists takes
        # one of two forms, and both were read out of the reference rather than
        # imagined -- across setRawMetadata (5 lists), getRawMetadata (12) and
        # getFormattedMetadata (5), every single gap begins "The following":
        #
        #   "The following items are first supported in version 13.2 ..."  (versioned)
        #   "The following are the video items supported by this API."     (a category)
        #
        # Requiring only the version form silently dropped the video keys AND
        # everything after them -- five valid keys of getFormattedMetadata, which
        # would have become five false Errors.
        $nextOpen = $ParamBody.IndexOf('<ul>', $close, [StringComparison]::Ordinal)
        if ($nextOpen -lt 0) { break }

        $gap = ($ParamBody.Substring($close, $nextOpen - $close) -replace '<[^>]+>', ' ' -replace '\s+', ' ').Trim()
        if ($gap -notmatch '^The following') { break }

        # Per gap, not carried forward: a category continuation with no version of
        # its own must not inherit the version of the list before it.
        $since = if ($gap -match 'first supported in version (?<v>[\d.]+)') { $Matches.v } else { $null }
        $cursor = $nextOpen
    }

    return $values
}

<#
.SYNOPSIS
    Per-function API detail from the LuaDoc pages of one SDK.
.DESCRIPTION
    The API Reference is LuaDoc output, and its structure is completely regular:
    each function is a <dt><a name="Module.fn"></a><strong>...</strong>(args)</dt>
    followed by a <dd> holding the description, a param_list, and -- for 96% of
    them -- "First supported in version X.Y of the Lightroom SDK".

    THAT VERSION LINE IS THE PRIZE. firstCataloguedIn can only say which of the
    packages we happen to hold first carried a module, which is not when Adobe
    shipped it: the catalogue infers 8.0 for LrDevelopController where Adobe
    states 6.0. Reading the annotation gives the real introduction version for
    every documented function, from a single download, including the releases
    nobody has locally.

    NOT parsed as XML despite the XHTML Strict doctype: the pages are not
    well-formed. Description prose contains unclosed <li> elements, so a
    conforming parser rejects the document. The structural markers are regular
    and the malformed prose sits inside descriptions we deliberately do not
    keep, so the markers are read directly and the prose is never parsed.

    Descriptions are omitted on purpose. They are the bulk of the file and they
    are Adobe's copyrighted documentation; names, versions and types are facts
    about the API surface, which is what an analyzer needs.
#>
function Read-LuaDocModules {
    [CmdletBinding()]
    param([Parameter(Mandatory)] [string] $ModulesDir)

    $result = [ordered] @{}
    if (-not (Test-Path -LiteralPath $ModulesDir -PathType Container)) { return $result }

    foreach ($page in (Get-ChildItem -LiteralPath $ModulesDir -Filter '*.html' | Sort-Object Name)) {
        $module = [IO.Path]::GetFileNameWithoutExtension($page.Name)
        if ($module -cnotmatch $ModuleNamePattern) { continue }

        $text = Get-Content -LiteralPath $page.FullName -Raw
        $functions = [ordered] @{}

        # Split on the function anchor: everything up to the next anchor belongs
        # to this function, which keeps a version line with the function it
        # annotates rather than the nearest one in the file.
        $chunks = [regex]::Split($text, '(?=<dt><a name=")') | Where-Object { $_ -match '^<dt><a name="' }
        foreach ($chunk in $chunks) {
            if ($chunk -notmatch '<dt><a name="(?<qualified>[^"]+)"') { continue }
            $qualified = $Matches.qualified

            # Anchors are Module.fn or Module.obj:method; keep what follows the module.
            $name = if ($qualified.StartsWith("$module.")) { $qualified.Substring($module.Length + 1) } else { $qualified }
            if (-not $name) { continue }

            $entry = [ordered] @{}
            if ($chunk -match 'First supported in version (?<v>[\d.]+)') { $entry['since'] = $Matches.v }

            # <dt>1. name</dt><dd>(type) ...  -- the leading parenthesised token is
            # the declared type, and "optional" rides in the same parentheses.
            $params = @()
            $paramValues = [ordered] @{}
            $matches2 = [regex]::Matches($chunk, '<dt>\d+\.\s*(?<pname>[^<]+?)\s*</dt>\s*<dd>\s*\((?<ptype>[^)]*)\)')
            for ($k = 0; $k -lt $matches2.Count; $k++) {
                $p = $matches2[$k]
                $ptype = ($p.Groups['ptype'].Value -replace '<[^>]+>', '' -replace '\s+', ' ').Trim()
                $pname = $p.Groups['pname'].Value.Trim()
                $params += "${pname}:${ptype}"

                # This parameter's own <dd>, i.e. up to the next numbered <dt>. A
                # value list must be read inside the parameter it documents, not
                # from anywhere in the function's chunk.
                $from = $p.Index
                $to = if ($k + 1 -lt $matches2.Count) { $matches2[$k + 1].Index } else { $chunk.Length }
                $body = $chunk.Substring($from, $to - $from)

                $values = Read-EnumeratedValues -ParamBody $body
                if ($values.Count -gt 0) { $paramValues[$pname] = $values }
            }
            if ($params.Count -gt 0) { $entry['params'] = $params }
            if ($paramValues.Count -gt 0) { $entry['paramValues'] = $paramValues }

            $functions[$name] = $entry
        }

        if ($functions.Count -eq 0) { continue }

        # The module's own floor is the earliest version any of its functions
        # carries -- a module is available once something in it is.
        $since = @($functions.Values | ForEach-Object { if ($_.Contains('since')) { $_.since } }) |
            Sort-Object { [version] $_ } | Select-Object -First 1

        $result[$module] = [ordered] @{ functions = $functions }
        if ($since) { $result[$module]['firstSupportedIn'] = $since }
    }

    return $result
}

<#
.SYNOPSIS
    Info.lua manifest keys a plug-in may declare, from the samples and, when
    available, from the SDK guide's manifest table.
.DESCRIPTION
    Two sources, deliberately kept apart by confidence.

    SAMPLES are exact: Info.lua is Lua, so a key is either there or not. They
    show only what Adobe's examples happen to use.

    THE GUIDE is the specification, and carries what the samples cannot -- the
    type of each key and whether it is required. It is read from pre-extracted
    text rather than from the PDF: extraction is a separate concern with its own
    quality and licensing questions, so this script parses whatever text it is
    given and records which guide produced it.

    The extracted text needs two repairs before it parses, both deterministic
    rather than heuristic:

    1. Words hyphenated across a line break arrive split, so a real key can be
       truncated -- LrAlsoUseBuiltInTransla-tions and LrLimitNumberOfTempRend-
       itions both lost their tails. Rejoining recovers them exactly.
    2. The manifest table's cells arrive concatenated in a fixed order:
       <key><Required|Optional><type><description>. That is a layout, not noise,
       so a non-greedy match up to Required/Optional recovers the key AND the two
       columns beside it.
#>
function Read-ManifestKeys {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string] $SampleDir,
        [string] $GuideTextDir
    )

    $keys = [ordered] @{}

    if (Test-Path -LiteralPath $SampleDir -PathType Container) {
        foreach ($info in Get-ChildItem -LiteralPath $SampleDir -Recurse -Filter 'Info.lua' -ErrorAction SilentlyContinue) {
            foreach ($m in [regex]::Matches((Get-Content -LiteralPath $info.FullName -Raw), '(?m)^\s*(?<k>Lr[A-Za-z0-9]+)\s*=')) {
                $name = $m.Groups['k'].Value
                if (-not $keys.Contains($name)) { $keys[$name] = [ordered] @{ source = 'samples'; usedBySamples = 0 } }
                $keys[$name].usedBySamples++
            }
        }
    }

    if ($GuideTextDir -and (Test-Path -LiteralPath $GuideTextDir -PathType Container)) {
        foreach ($txt in Get-ChildItem -LiteralPath $GuideTextDir -Filter '*.txt' -ErrorAction SilentlyContinue) {
            $text = Get-Content -LiteralPath $txt.FullName -Raw
            $text = $text -replace '-\s*\r?\n\s*', '' -replace '(?<=[a-z])-(?=[a-z]{2,})', ''

            foreach ($m in [regex]::Matches($text, '\b(?<key>Lr[A-Za-z0-9]+?)(?<req>Optional|Required)\s*(?<type>Boolean|string|number|table|function)')) {
                # One row can document two keys together -- LrLibraryMenuItems and
                # LrHelpMenuItems share a cell -- so split where a second Lr name begins.
                foreach ($name in ($m.Groups['key'].Value -csplit '(?=Lr[A-Z])' | Where-Object { $_ -cmatch '^Lr[A-Za-z0-9]+$' })) {
                    if (-not $keys.Contains($name)) { $keys[$name] = [ordered] @{ source = 'guide' } }
                    $keys[$name].required = ($m.Groups['req'].Value -eq 'Required')
                    $keys[$name].type = $m.Groups['type'].Value
                    if ($keys[$name].source -eq 'samples') { $keys[$name].source = 'samples+guide' }
                }
            }
        }
    }

    $ordered = [ordered] @{}
    foreach ($name in ($keys.Keys | Sort-Object)) { $ordered[$name] = $keys[$name] }
    return $ordered
}

<#
.SYNOPSIS
    Identity of the SDK guides shipped in one Manual folder.
.DESCRIPTION
    Name, size and SHA-256 -- deliberately NOT parsed content.

    Provenance wants identity, not prose. A hash pins the exact file anyone else
    can re-verify, which is stronger evidence than a title line lifted out of
    page one, and it costs no dependency: no PDF library, no cloud extraction
    service, no credentials, and nothing leaves the machine. Adobe's own PDF
    Services API would extract these more faithfully than a local parser, but it
    is a network round-trip with credentials to obtain a fact a hash already
    settles.

    The guides' body text is not a data source in any case. Extraction runs words
    together across layout boundaries -- "LrDialogsAllows",
    "LrSdkVersionRequirednumberThe" -- so anything built from it would be
    fragments wearing the shape of real names.
#>
function Read-GuideProvenance {
    [CmdletBinding()]
    param([Parameter(Mandatory)] [string] $ManualDir)

    if (-not (Test-Path -LiteralPath $ManualDir -PathType Container)) { return , @() }

    $results = @()
    foreach ($pdf in Get-ChildItem -LiteralPath $ManualDir -Filter '*.pdf' -ErrorAction SilentlyContinue) {
        $results += [ordered] @{
            file   = $pdf.Name
            bytes  = $pdf.Length
            sha256 = (Get-FileHash -LiteralPath $pdf.FullName -Algorithm SHA256).Hash
        }
    }
    return , $results
}

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

    # The guides are part of the same official package and state what they are
    # and who published them, so they corroborate the readme rather than
    # duplicating it. Only their identity is taken -- their prose is not a usable
    # source for module names, as the extracted text runs words together.
    # Assigned directly: the function already returns an array via the comma
    # operator, so wrapping in @() here would nest it and collapse two guides
    # into one element.
    $guides = Read-GuideProvenance -ManualDir (Join-Path $dir.FullName 'Manual')
    if ($guides.Count -gt 0) { $entry['guides'] = $guides }

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

# From the newest catalogued package: the manifest vocabulary is a property of the
# current SDK, not something to union across versions.
$newestDir = ($sdkDirs | Where-Object { ($_.Name -replace '^Lightroom SDK\s+', '') -eq $newest } | Select-Object -First 1)
$manifestKeys = Read-ManifestKeys -SampleDir (Join-Path $newestDir.FullName 'Sample Plugins') -GuideTextDir $GuideTextDir

# Guide-derived keys survive a run that cannot regenerate them.
#
# -GuideTextDir is optional and the guide text lives outside this repository, so
# a refresh run without it USED to silently delete every guide-sourced key --
# type, required, and all. Seventeen of them, replaced by nothing, in a file
# whose whole purpose is to be authoritative. It is a one-word omission on the
# command line and the output looks perfectly well-formed afterwards.
#
# Regenerating what you can and deleting what you cannot is the worst of the
# three options. Carrying the earlier answer forward keeps the catalogue whole,
# and the run says how many it carried rather than passing them off as fresh.
$carriedForward = @()
if (-not $GuideTextDir -and (Test-Path -LiteralPath $outputPath -PathType Leaf)) {
    $previous = Get-Content -LiteralPath $outputPath -Raw | ConvertFrom-Json

    if ($previous.PSObject.Properties.Name -contains 'manifestKeys') {
        foreach ($prior in $previous.manifestKeys.PSObject.Properties) {
            if ($prior.Value.source -notlike '*guide*') { continue }

            $carriedForward += $prior.Name

            # Samples are re-read every run and stay authoritative for what they
            # cover; only the guide-derived fields are restored on top.
            if (-not $manifestKeys.Contains($prior.Name)) { $manifestKeys[$prior.Name] = [ordered] @{} }
            foreach ($field in 'source', 'required', 'type', 'usedBySamples') {
                if ($prior.Value.PSObject.Properties.Name -notcontains $field) { continue }
                if ($field -eq 'usedBySamples' -and $manifestKeys[$prior.Name].Contains('usedBySamples')) { continue }

                $manifestKeys[$prior.Name][$field] = $prior.Value.$field
            }
        }

        $reordered = [ordered] @{}
        foreach ($name in ($manifestKeys.Keys | Sort-Object)) { $reordered[$name] = $manifestKeys[$name] }
        $manifestKeys = $reordered
    }
}

# Adobe's own introduction versions, from the newest package's LuaDoc. Merged
# ALONGSIDE firstCataloguedIn rather than over it: the two answer different
# questions, and silently replacing one with the other would hide which is which.
$luaDoc = Read-LuaDocModules -ModulesDir (Join-Path $newestDir.FullName 'API Reference\modules')
foreach ($name in $luaDoc.Keys) {
    if (-not $modules.Contains($name)) { continue }
    if ($luaDoc[$name].Contains('firstSupportedIn')) { $modules[$name]['firstSupportedIn'] = $luaDoc[$name].firstSupportedIn }
    $modules[$name]['functions'] = $luaDoc[$name].functions
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
        'seenIn on an undocumented module is the earliest CATALOGUED version whose samples import it, not the release that introduced it. With gaps between catalogued versions the two can be far apart: LrController and LrTableUtils have no trace at all in 3.0 or 8.0 and appear in 15.3, so all that can be said is that they arrived somewhere in between -- possibly documented at the time and undocumented since.',
        'PARAMETER VALUES: paramValues on a function lists the values a parameter accepts, read ONLY where the reference introduces them with an enumeration lead-in ("The following keys are recognized", "These valid keys return these values", "Valid values are", "one of the following strings"). Absence means the docs do not enumerate that parameter -- NEVER that anything is accepted, and never that a value is wrong. Deliberately narrow: the reference holds 112 bulleted name lists and most describe the shape of a RETURN value ("A table with these fields", 26 of them), so harvesting them all would reject correct code.',
        'CARRIED FORWARD: guide-derived manifest keys cannot be regenerated without -GuideTextDir, whose input lives outside this repository. A run without it PRESERVES the previous answer for those keys rather than deleting them, and reports how many it carried. So a guide-sourced entry may predate the newest run; re-run with -GuideTextDir to refresh it.',
        'MANIFEST KEYS: manifestKeys lists what an Info.lua may declare. source=samples means observed in Adobe''s example plug-ins and therefore exact but only as complete as their usage; source=guide means read from the SDK guide''s manifest table, which also supplies type and whether the key is required. Guide-derived entries depend on pre-extracted text being supplied, so their absence means nobody supplied it, not that the guide lists nothing.',
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
    manifestKeys            = $manifestKeys
}

# Depth 10, not 6: paramValues added two levels (param -> value -> { since }), and
# ConvertTo-Json TRUNCATES past its depth with only a warning, so the catalogue
# would ship silently mangled rather than failing. Deepest path today is
# doc > modules > module > functions > function > paramValues > param > value > since = 9.
$doc | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $outputPath -Encoding utf8

[pscustomobject] @{
    output            = $outputPath
    catalogedVersions = $ordered
    modules           = $modules.Count
    removed           = @($modules.GetEnumerator() | Where-Object { $_.Value.Contains('absentAfter') } | ForEach-Object { $_.Key })
    # Reported, not hidden: these are pages the filter refused, and a reviewer
    # needs to see them to know the filter is refusing the right things.
    rejectedPages     = $rejected
    # Guide-derived keys this run preserved instead of regenerating, because no
    # -GuideTextDir was supplied. Empty on a run that had the guide text.
    carriedForward    = $carriedForward
} | ConvertTo-Json -Depth 4