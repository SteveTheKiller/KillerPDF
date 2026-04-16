# Changelog

All notable changes to KillerPDF are documented here.

Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [1.1.0] - 2026-04-16

### Changed
- Retargeted from .NET 8 to .NET Framework 4.8 so end users no longer need to install a separate .NET runtime.
- Forced 64-bit build via `PlatformTarget=x64`.
- Added PolySharp polyfills for modern C# language features on net48.
- Replaced `Math.Clamp` calls with `Math.Min`/`Math.Max` equivalents.

### Added
- Post-publish MSBuild target that automatically bundles a GPL3-compliant source zip alongside the published EXE.
- CHANGELOG.md.

## [1.0.1]

_Historical entries to be backfilled._

[Unreleased]: https://github.com/SteveTheKiller/KillerPDF/compare/v1.1.0...HEAD
[1.1.0]: https://github.com/SteveTheKiller/KillerPDF/releases/tag/v1.1.0
[1.0.1]: https://github.com/SteveTheKiller/KillerPDF/releases/tag/v1.0.1
