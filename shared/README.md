# Shared assets

Language-neutral data used by both the macOS (Swift) and the Windows (.NET) versions of Cleanup.
**This folder is the source of truth.** The `cleanup-windows` repo keeps a copy (see §3 of
`docs/superpowers/specs/2026-10-12-windows-version-design.md`); change files here, then sync.

| File | Swift test that runs it |
|---|---|
| `test-vectors/version-compare.json` | `Tests/CleanupCoreTests/SharedVectorsTests.swift` |
| `test-vectors/path-containment.json` | `Tests/CleanupCoreTests/SharedVectorsTests.swift` |
| `test-vectors/search-filter.json` | `Tests/CleanupUITests/SharedListVectorsTests.swift` |
| `test-vectors/selection.json` | `Tests/CleanupUITests/SharedListVectorsTests.swift` (labels: .NET only) |
| `test-vectors/keep-rule.json` | `Tests/CleanupCoreTests/SharedBatchVectorsTests.swift` |
| `test-vectors/privileged-batch.json` | `Tests/CleanupCoreTests/SharedBatchVectorsTests.swift` |
| `test-vectors/deeplinks.json` | `Tests/CleanupCoreTests/SharedLinkDiskVectorsTests.swift` |
| `test-vectors/disk-space.json` | `Tests/CleanupCoreTests/SharedLinkDiskVectorsTests.swift` |
| `test-vectors/health.json` | `Tests/CleanupCoreTests/SharedHealthVectorsTests.swift` |
| `test-vectors/autoclean.json` | `Tests/CleanupCoreTests/SharedHealthVectorsTests.swift` |
| `rules/windows.json` | `Tests/CleanupCoreTests/SharedLinkDiskVectorsTests.swift` (schema + unique ids only; the rules are Windows-only) |
| `design-tokens.json` | `Tests/CleanupUITests/DesignTokensTests.swift` |

Safety-policy paths themselves are platform-specific (`~/Library/...` vs `%LOCALAPPDATA%\...`), so only the
component-aware containment rule is shared.
