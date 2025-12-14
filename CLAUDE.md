# Claude Code Instructions for PenguinMonitor

## CRITICAL RULES

1. **READ THIS FILE FIRST** before doing anything else.
2. **TELL THE USER** that you've read CLAUDE.md at the start of each conversation.
3. **NEVER BUILD UNLESS THE USER EXPLICITLY ASKS YOU TO BUILD.**
4. **If the user allows you to touch git, don't mention Claude. Keep your commit messages terse and specific to new features**

Do not run `dotnet build`, `dotnet run`, or any build commands automatically after making changes.
Wait for the user to say "build" or explicitly request a build.

---

## Project Overview

**PenguinMonitor** is a .NET MAUI Android app used by conservation teams to monitor penguin breeding at nest box colonies in New Zealand.

**What it does:**
- Field researchers visit nest boxes and record: adults present, eggs, chicks, breeding status, gate status, notes
- Scans microchipped penguins via Bluetooth connection to Gallagher HR5 EID reader
- Syncs data to/from a central server
- Shows colony overview with filters (by breeding status, egg count, etc.)

**Architecture:**
- 3 tabs: Settings, Box Data, Overview
- MVVM pattern: Pages (XAML) + ViewModels
- `DataManager` singleton holds all state
- `DataStorageService` handles file I/O and server communication
- `Backend` class (in SmtpAuthenticator namespace) handles encrypted server requests
- `BluetoothManager` handles HR5 EID reader connection

**Key files:**
- `AppShell.xaml` - Tab navigation
- `Pages/SettingsPage.xaml` - Config (box set, Bluetooth, GPS)
- `Pages/BoxDataPage.xaml` - Main data entry screen
- `Pages/OverviewPage.xaml` - Colony summary grid
- `Services/DataManager.cs` - Central state management
- `Services/DataStorageService.cs` - Persistence and sync
