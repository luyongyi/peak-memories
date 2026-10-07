# Release package verifier

Build from the repository root:

```powershell
dotnet build tools/ReleaseVerifier/ReleaseVerifier.csproj -c Release --nologo -warnaserror
dotnet artifacts/bin/ReleaseVerifier/release/ReleaseVerifier.dll self-test
dotnet artifacts/bin/ReleaseVerifier/release/ReleaseVerifier.dll inspect artifacts/bin/PeakReplayLab/release/PeakReplayLab.dll
dotnet artifacts/bin/ReleaseVerifier/release/ReleaseVerifier.dll verify package.zip . v0.8.0 <full-40-character-commit>
```

`inspect` emits one JSON object. It reads managed PE metadata using
`PEReader`/`MetadataReader`; it never loads or executes the inspected assembly.
Missing optional version attributes are reported as empty strings.

`verify` requires the four flat ZIP files `PeakReplayLab.dll`,
`README-INSTALL.md`, `SHA256SUMS.txt`, and `build-manifest.json`. Checksums must
cover the DLL, README and manifest exactly, with lines `SHA256  filename`.
Verification checks source versions and schema rules, the actual DLL's metadata,
hash, size and embedded resource names, and the manifest's contract/reference
metadata. It does not extract files or execute game APIs. Success exits 0;
invalid input exits nonzero with a concise error on stderr.

Archive limits are 160 MiB compressed overall, 128 MiB decoded DLL, 256 KiB
README, 16 KiB checksum list and 2 MiB manifest. Entries must be nonempty and
unique; directories, unexpected files and traversal names are rejected. Reference
filenames must be unique basenames. Manifest version 1 rejects unknown or duplicate
JSON properties.

`self-test` uses a small, separate .NET metadata fixture with synthetic text
resources. It contains no native game assemblies, images or recordings. This
fixture is copied beside the verifier under `Fixtures/`; neither the verifier nor
the fixture belongs in the Mod installation package. Tests verify acceptance of a
valid package and rejection of malformed tags/commits, source/version drift,
extra/duplicate/traversal/oversized ZIP entries, checksum/metadata errors, missing
cover/schema/contract/reference data and machine paths.

The manifest records claims about compile references and completed contracts;
the packaging workflow must obtain those values from its actual build/test run.
This CLI validates their structure and consistency, not Unity visual or DSP
behavior.
