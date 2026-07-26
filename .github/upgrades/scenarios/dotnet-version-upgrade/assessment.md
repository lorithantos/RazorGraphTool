# Projects and dependencies analysis

This document provides a comprehensive overview of the projects and their dependencies in the context of upgrading to .NETCoreApp,Version=v10.0.

## Table of Contents

- [Executive Summary](#executive-Summary)
  - [Highlevel Metrics](#highlevel-metrics)
  - [Projects Compatibility](#projects-compatibility)
  - [Package Compatibility](#package-compatibility)
  - [API Compatibility](#api-compatibility)
  - [Binding Redirect Configuration](#binding-redirect-configuration)
- [Aggregate NuGet packages details](#aggregate-nuget-packages-details)
- [Top API Migration Challenges](#top-api-migration-challenges)
  - [Technologies and Features](#technologies-and-features)
  - [Most Frequent API Issues](#most-frequent-api-issues)
- [Projects Relationship Graph](#projects-relationship-graph)
- [Project Details](#project-details)

  - [src\RazorGraph.Cli\RazorGraph.Cli.csproj](#srcrazorgraphclirazorgraphclicsproj)
  - [src\RazorGraph.Core\RazorGraph.Core.csproj](#srcrazorgraphcorerazorgraphcorecsproj)
  - [src\RazorGraph.Extractor\RazorGraph.Extractor.csproj](#srcrazorgraphextractorrazorgraphextractorcsproj)
  - [src\RazorGraph.Tests\RazorGraph.Tests.csproj](#srcrazorgraphtestsrazorgraphtestscsproj)


## Executive Summary

### Highlevel Metrics

| Metric | Count | Status |
| :--- | :---: | :--- |
| Total Projects | 4 | 0 require upgrade |
| Total NuGet Packages | 9 | All compatible |
| Total Code Files | 11 |  |
| Total Code Files with Incidents | 0 |  |
| Total Lines of Code | 1471 |  |
| Total Number of Issues | 0 |  |
| Estimated LOC to modify | 0+ | at least 0.0% of codebase |

### Projects Compatibility

| Project | Target Framework | Difficulty | Package Issues | API Issues | Binding Issues | Est. LOC Impact | Description |
| :--- | :---: | :---: | :---: | :---: | :---: | :---: | :--- |
| [src\RazorGraph.Cli\RazorGraph.Cli.csproj](#srcrazorgraphclirazorgraphclicsproj) | net10.0 | ✅ None | 0 | 0 | 0 |  | DotNetCoreApp, Sdk Style = True |
| [src\RazorGraph.Core\RazorGraph.Core.csproj](#srcrazorgraphcorerazorgraphcorecsproj) | net10.0 | ✅ None | 0 | 0 | 0 |  | ClassLibrary, Sdk Style = True |
| [src\RazorGraph.Extractor\RazorGraph.Extractor.csproj](#srcrazorgraphextractorrazorgraphextractorcsproj) | net10.0 | ✅ None | 0 | 0 | 0 |  | ClassLibrary, Sdk Style = True |
| [src\RazorGraph.Tests\RazorGraph.Tests.csproj](#srcrazorgraphtestsrazorgraphtestscsproj) | net10.0 | ✅ None | 0 | 0 | 0 |  | DotNetCoreApp, Sdk Style = True |

### Package Compatibility

| Status | Count | Percentage |
| :--- | :---: | :---: |
| ✅ Compatible | 9 | 100.0% |
| ⚠️ Incompatible | 0 | 0.0% |
| 🔄 Upgrade Recommended | 0 | 0.0% |
| ***Total NuGet Packages*** | ***9*** | ***100%*** |

### API Compatibility

| Category | Count | Impact |
| :--- | :---: | :--- |
| 🔴 Binary Incompatible | 0 | High - Require code changes |
| 🟡 Source Incompatible | 0 | Medium - Needs re-compilation and potential conflicting API error fixing |
| 🔵 Behavioral change | 0 | Low - Behavioral changes that may require testing at runtime |
| ✅ Compatible | 0 |  |
| ***Total APIs Analyzed*** | ***0*** |  |

## Aggregate NuGet packages details

| Package | Current Version | Suggested Version | Projects | Description |
| :--- | :---: | :---: | :--- | :--- |
| Microsoft.AspNetCore.Razor.Language | 6.0.36 |  | [RazorGraph.Extractor.csproj](#srcrazorgraphextractorrazorgraphextractorcsproj) | ✅Compatible |
| Microsoft.Build.Locator | 1.11.2 |  | [RazorGraph.Extractor.csproj](#srcrazorgraphextractorrazorgraphextractorcsproj) | ✅Compatible |
| Microsoft.CodeAnalysis.CSharp | 5.6.0 |  | [RazorGraph.Extractor.csproj](#srcrazorgraphextractorrazorgraphextractorcsproj) | ✅Compatible |
| Microsoft.CodeAnalysis.CSharp.Workspaces | 5.6.0 |  | [RazorGraph.Extractor.csproj](#srcrazorgraphextractorrazorgraphextractorcsproj) | ✅Compatible |
| Microsoft.CodeAnalysis.Workspaces.MSBuild | 5.6.0 |  | [RazorGraph.Extractor.csproj](#srcrazorgraphextractorrazorgraphextractorcsproj) | ✅Compatible |
| Microsoft.NET.Test.Sdk | 18.8.1 |  | [RazorGraph.Tests.csproj](#srcrazorgraphtestsrazorgraphtestscsproj) | ✅Compatible |
| System.CommandLine | 2.0.10 |  | [RazorGraph.Cli.csproj](#srcrazorgraphclirazorgraphclicsproj) | ✅Compatible |
| xunit | 2.9.3 |  | [RazorGraph.Tests.csproj](#srcrazorgraphtestsrazorgraphtestscsproj) | ✅Compatible |
| xunit.runner.visualstudio | 3.1.5 |  | [RazorGraph.Tests.csproj](#srcrazorgraphtestsrazorgraphtestscsproj) | ✅Compatible |

## Top API Migration Challenges

### Technologies and Features

| Technology | Issues | Percentage | Migration Path |
| :--- | :---: | :---: | :--- |

### Most Frequent API Issues

| API | Count | Percentage | Category |
| :--- | :---: | :---: | :--- |

## Projects Relationship Graph

Legend:
📦 SDK-style project
⚙️ Classic project

```mermaid
flowchart LR
    P1["<b>📦&nbsp;RazorGraph.Core.csproj</b><br/><small>net10.0</small>"]
    P2["<b>📦&nbsp;RazorGraph.Extractor.csproj</b><br/><small>net10.0</small>"]
    P3["<b>📦&nbsp;RazorGraph.Cli.csproj</b><br/><small>net10.0</small>"]
    P4["<b>📦&nbsp;RazorGraph.Tests.csproj</b><br/><small>net10.0</small>"]
    P2 --> P1
    P3 --> P1
    P3 --> P2
    P4 --> P1
    P4 --> P2
    click P1 "#srcrazorgraphcorerazorgraphcorecsproj"
    click P2 "#srcrazorgraphextractorrazorgraphextractorcsproj"
    click P3 "#srcrazorgraphclirazorgraphclicsproj"
    click P4 "#srcrazorgraphtestsrazorgraphtestscsproj"

```

## Project Details

<a id="srcrazorgraphclirazorgraphclicsproj"></a>
### src\RazorGraph.Cli\RazorGraph.Cli.csproj

#### Project Info

- **Current Target Framework:** net10.0✅
- **SDK-style**: True
- **Project Kind:** DotNetCoreApp
- **Dependencies**: 2
- **Dependants**: 0
- **Number of Files**: 1
- **Lines of Code**: 142
- **Estimated LOC to modify**: 0+ (at least 0.0% of the project)

#### Dependency Graph

Legend:
📦 SDK-style project
⚙️ Classic project

```mermaid
flowchart TB
    subgraph current["RazorGraph.Cli.csproj"]
        MAIN["<b>📦&nbsp;RazorGraph.Cli.csproj</b><br/><small>net10.0</small>"]
        click MAIN "#srcrazorgraphclirazorgraphclicsproj"
    end
    subgraph downstream["Dependencies (2"]
        P1["<b>📦&nbsp;RazorGraph.Core.csproj</b><br/><small>net10.0</small>"]
        P2["<b>📦&nbsp;RazorGraph.Extractor.csproj</b><br/><small>net10.0</small>"]
        click P1 "#srcrazorgraphcorerazorgraphcorecsproj"
        click P2 "#srcrazorgraphextractorrazorgraphextractorcsproj"
    end
    MAIN --> P1
    MAIN --> P2

```

### API Compatibility

| Category | Count | Impact |
| :--- | :---: | :--- |
| 🔴 Binary Incompatible | 0 | High - Require code changes |
| 🟡 Source Incompatible | 0 | Medium - Needs re-compilation and potential conflicting API error fixing |
| 🔵 Behavioral change | 0 | Low - Behavioral changes that may require testing at runtime |
| ✅ Compatible | 0 |  |
| ***Total APIs Analyzed*** | ***0*** |  |

<a id="srcrazorgraphcorerazorgraphcorecsproj"></a>
### src\RazorGraph.Core\RazorGraph.Core.csproj

#### Project Info

- **Current Target Framework:** net10.0✅
- **SDK-style**: True
- **Project Kind:** ClassLibrary
- **Dependencies**: 0
- **Dependants**: 3
- **Number of Files**: 7
- **Lines of Code**: 535
- **Estimated LOC to modify**: 0+ (at least 0.0% of the project)

#### Dependency Graph

Legend:
📦 SDK-style project
⚙️ Classic project

```mermaid
flowchart TB
    subgraph upstream["Dependants (3)"]
        P2["<b>📦&nbsp;RazorGraph.Extractor.csproj</b><br/><small>net10.0</small>"]
        P3["<b>📦&nbsp;RazorGraph.Cli.csproj</b><br/><small>net10.0</small>"]
        P4["<b>📦&nbsp;RazorGraph.Tests.csproj</b><br/><small>net10.0</small>"]
        click P2 "#srcrazorgraphextractorrazorgraphextractorcsproj"
        click P3 "#srcrazorgraphclirazorgraphclicsproj"
        click P4 "#srcrazorgraphtestsrazorgraphtestscsproj"
    end
    subgraph current["RazorGraph.Core.csproj"]
        MAIN["<b>📦&nbsp;RazorGraph.Core.csproj</b><br/><small>net10.0</small>"]
        click MAIN "#srcrazorgraphcorerazorgraphcorecsproj"
    end
    P2 --> MAIN
    P3 --> MAIN
    P4 --> MAIN

```

### API Compatibility

| Category | Count | Impact |
| :--- | :---: | :--- |
| 🔴 Binary Incompatible | 0 | High - Require code changes |
| 🟡 Source Incompatible | 0 | Medium - Needs re-compilation and potential conflicting API error fixing |
| 🔵 Behavioral change | 0 | Low - Behavioral changes that may require testing at runtime |
| ✅ Compatible | 0 |  |
| ***Total APIs Analyzed*** | ***0*** |  |

<a id="srcrazorgraphextractorrazorgraphextractorcsproj"></a>
### src\RazorGraph.Extractor\RazorGraph.Extractor.csproj

#### Project Info

- **Current Target Framework:** net10.0✅
- **SDK-style**: True
- **Project Kind:** ClassLibrary
- **Dependencies**: 1
- **Dependants**: 2
- **Number of Files**: 26
- **Lines of Code**: 794
- **Estimated LOC to modify**: 0+ (at least 0.0% of the project)

#### Dependency Graph

Legend:
📦 SDK-style project
⚙️ Classic project

```mermaid
flowchart TB
    subgraph upstream["Dependants (2)"]
        P3["<b>📦&nbsp;RazorGraph.Cli.csproj</b><br/><small>net10.0</small>"]
        P4["<b>📦&nbsp;RazorGraph.Tests.csproj</b><br/><small>net10.0</small>"]
        click P3 "#srcrazorgraphclirazorgraphclicsproj"
        click P4 "#srcrazorgraphtestsrazorgraphtestscsproj"
    end
    subgraph current["RazorGraph.Extractor.csproj"]
        MAIN["<b>📦&nbsp;RazorGraph.Extractor.csproj</b><br/><small>net10.0</small>"]
        click MAIN "#srcrazorgraphextractorrazorgraphextractorcsproj"
    end
    subgraph downstream["Dependencies (1"]
        P1["<b>📦&nbsp;RazorGraph.Core.csproj</b><br/><small>net10.0</small>"]
        click P1 "#srcrazorgraphcorerazorgraphcorecsproj"
    end
    P3 --> MAIN
    P4 --> MAIN
    MAIN --> P1

```

### API Compatibility

| Category | Count | Impact |
| :--- | :---: | :--- |
| 🔴 Binary Incompatible | 0 | High - Require code changes |
| 🟡 Source Incompatible | 0 | Medium - Needs re-compilation and potential conflicting API error fixing |
| 🔵 Behavioral change | 0 | Low - Behavioral changes that may require testing at runtime |
| ✅ Compatible | 0 |  |
| ***Total APIs Analyzed*** | ***0*** |  |

<a id="srcrazorgraphtestsrazorgraphtestscsproj"></a>
### src\RazorGraph.Tests\RazorGraph.Tests.csproj

#### Project Info

- **Current Target Framework:** net10.0✅
- **SDK-style**: True
- **Project Kind:** DotNetCoreApp
- **Dependencies**: 2
- **Dependants**: 0
- **Number of Files**: 2
- **Lines of Code**: 0
- **Estimated LOC to modify**: 0+ (at least 0.0% of the project)

#### Dependency Graph

Legend:
📦 SDK-style project
⚙️ Classic project

```mermaid
flowchart TB
    subgraph current["RazorGraph.Tests.csproj"]
        MAIN["<b>📦&nbsp;RazorGraph.Tests.csproj</b><br/><small>net10.0</small>"]
        click MAIN "#srcrazorgraphtestsrazorgraphtestscsproj"
    end
    subgraph downstream["Dependencies (2"]
        P1["<b>📦&nbsp;RazorGraph.Core.csproj</b><br/><small>net10.0</small>"]
        P2["<b>📦&nbsp;RazorGraph.Extractor.csproj</b><br/><small>net10.0</small>"]
        click P1 "#srcrazorgraphcorerazorgraphcorecsproj"
        click P2 "#srcrazorgraphextractorrazorgraphextractorcsproj"
    end
    MAIN --> P1
    MAIN --> P2

```

### API Compatibility

| Category | Count | Impact |
| :--- | :---: | :--- |
| 🔴 Binary Incompatible | 0 | High - Require code changes |
| 🟡 Source Incompatible | 0 | Medium - Needs re-compilation and potential conflicting API error fixing |
| 🔵 Behavioral change | 0 | Low - Behavioral changes that may require testing at runtime |
| ✅ Compatible | 0 |  |
| ***Total APIs Analyzed*** | ***0*** |  |

