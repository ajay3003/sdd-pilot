# Source Analysis archive uploads

Source Analysis accepts one `.zip` archive. No Target Environment is needed: a source snapshot belongs to the workspace, and a Target Environment is runtime context only (see `project-import.md`, *Source snapshots and Target Environments*).

## Limits and archive safety

- Compressed archive: at most 50 MB.
- Total expanded entry size: at most 100 MB.
- Entries: at most 20,000, including directory entries.
- Individual analyzed file: at most 2 MB is read for analysis. Larger files are skipped with a limitation; they do not make an otherwise safe archive invalid.
- ZIP entries are read in memory. The upload and entries are not extracted to disk, so there is no extraction directory to clean up.
- Absolute paths, drive or UNC paths, `..` traversal, symbolic links, and duplicate paths after separator, dot-segment, and case normalization are rejected. A harmless `.` segment such as the common `./` ZIP root wrapper is normalized away.

## Repository layout and technology

A safe archive may have one wrapper directory, multiple top-level service folders, nested source folders, infrastructure, documentation, or no recognized source code. Validation does not require `.sln`, `.csproj`, C# files, Azure files, or a particular product structure. Unsupported technologies and document-only archives pass archive validation; later analysis reports the technology coverage or analysis limitations it can establish.

An invalid ZIP, a safety-limit violation, extraction/read failure, source-analysis failure, and snapshot-save failure are separate outcomes. Upload errors show a stable code, stage, safe reason, and relevant relative entry path or limit. Server paths, source contents, and stack traces are not returned to the user.
