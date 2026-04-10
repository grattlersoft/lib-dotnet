# Shared Libs - dotnet

> Shared Libraries und gemeinsame Basisklassen für grattlersoft-Projekte.

[![Build](https://github.com/grattlersoft/lib-dotnet/actions/workflows/ci.yml/badge.svg)](https://github.com/grattlersoft/lib-dotnet/actions/workflows/ci.yml)
[![License: OHNE](https://img.shields.io/badge/License-OHNE-yellow.svg)](LICENSE)

Wiederverwendbare Libraries, die in mehreren grattlersoft-Repos als Submodule eingebunden werden. Consumer referenzieren die Projekte direkt per `ProjectReference` aus `submodules/lib-dotnet/`.

## Inhalt

### Grattlersoft.VsExtension.Core

Gemeinsame Basis für Visual Studio Extensions:

- **ExtensionPackage** — Abstrakte Package-Basisklasse mit Boot-Log, try/catch, ProvideBindingPath, Dependency-Check und MEF-Health-Check
- **Log** — Dateibasiertes Logging nach `%TEMP%\{ExtensionName}.log` + Output-Window-Pane
- **ObservableOptions** — Change-Detection für Extension-Settings

Wird verwendet von:
- [vsx-virtual-project](https://github.com/grattlersoft/vsx-virtual-project) — Virtuelle Projektbäume im Solution Explorer

## Einbindung

Als Submodule:

```bash
git submodule add git@github.com:grattlersoft/lib-dotnet.git submodules/lib-dotnet
```

In `.csproj`:

```xml
<ProjectReference Include="..\..\submodules\lib-dotnet\src\Grattlersoft.VsExtension.Core\Grattlersoft.VsExtension.Core.csproj" />
```

## Build

```bash
dotnet build src/SharedDotnet.slnx
```

## Lizenz

[OHNE-Lizenz](LICENSE) — Mach was du willst. Wir waren's nicht.
