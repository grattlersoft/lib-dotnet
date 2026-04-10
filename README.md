# shared-dotnet

> Shared Libraries für grattlersoft-Projekte.

[![Build](https://github.com/grattlersoft/shared-dotnet/actions/workflows/ci.yml/badge.svg)](https://github.com/grattlersoft/shared-dotnet/actions/workflows/ci.yml)
[![License: OHNE](https://img.shields.io/badge/License-OHNE-yellow.svg)](LICENSE)

## Inhalt

### Grattlersoft.VsExtension.Core

Gemeinsame Basis für Visual Studio Extensions:

- **SoftfairPackage** — Abstrakte Package-Basisklasse mit Boot-Log, try/catch, ProvideBindingPath
- **Log** — Dateibasiertes Logging nach `%TEMP%\{ExtensionName}.log`
- **ObservableOptions** — Change-Detection für Extension-Settings

Wird verwendet von:
- [vs-ext-virtual-project](https://github.com/grattlersoft/vs-ext-virtual-project) — Virtuelle Projektbäume im Solution Explorer

## Einbindung

Als Submodule in Consumer-Repos:

```bash
git submodule add git@github.com:grattlersoft/shared-dotnet.git submodules/shared-dotnet
```

```xml
<ProjectReference Include="$(WerkzeugkastenRoot)src\Grattlersoft.VsExtension.Core\Grattlersoft.VsExtension.Core.csproj" />
```

## Build

```bash
dotnet build src/Werkzeugkasten.slnx
```

## Lizenz

[OHNE-Lizenz](LICENSE) — Mach was du willst. Wir waren's nicht.
