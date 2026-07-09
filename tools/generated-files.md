# Generated file validation

Use [`tools/validate-generated-files.ps1`](./validate-generated-files.ps1) in CI or review prep to catch Razor `.cshtml` changes that do not have matching generated C# outputs.

The validator scans a root path for `.cshtml` files that either:

- are declared with `Generator=RazorGenerator` in a project file, or
- already have a generated sibling/output file

Supported generated output names:

- `View.generated.cs`
- `View.cs`
- `obj\CodeGen\relative\path\View.cshtml.cs`

Examples:

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\validate-generated-files.ps1
powershell -ExecutionPolicy Bypass -File .\tools\validate-generated-files.ps1 -RootPath .\samples\PrecompiledMvcLibrary
powershell -ExecutionPolicy Bypass -File .\tools\validate-generated-files.ps1 -StrictTimestamps
```

By default the script only checks that expected generated files exist. Add `-StrictTimestamps` when you also want the script to fail if a `.cshtml` file is newer than its discovered generated outputs.

The default Cake `Verify` target runs the validator tests and the non-strict generated-file validation. Use strict timestamp validation locally when reviewing regenerated files.
