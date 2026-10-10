#Requires -Version 7.0

<#
.SYNOPSIS
    Applies `dotnet format <subcommand>` fixers to the C# files prek hands this hook and stages what changed.

.DESCRIPTION
    `dotnet format` takes a project or solution in its positional slot, never files, so the staged files go in
    through `--include`. That option is variadic and therefore comes last. Fixers run at info severity, the same
    level the CI `--verify-no-changes` check uses. Modified files are re-staged so prek does not fail the commit
    on "files were modified by this hook"; the dotnet-build hook runs last and gates the result.

    When a staged file's project has no restore output, the wrapper runs `dotnet restore` first.

.EXAMPLE
    pwsh tools/hooks/dotnet-format-wrapper.ps1 style src/Foo/Bar.cs
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [ValidateSet('style', 'analyzers')]
    [string]$Subcommand,

    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$Files
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not $Files) {
    exit 0
}

# True when the project owning any staged file has no obj/project.assets.json (a fresh clone, or obj/ cleaned).
function Test-RestoreNeeded {
    param([string[]]$Paths)
    foreach ($file in $Paths) {
        # GetFullPath's second argument: without it .NET resolves against the process's folder, not $PWD.
        $dir = Split-Path -Parent ([IO.Path]::GetFullPath($file, $PWD.Path))
        while ($dir -and -not (Get-ChildItem -LiteralPath $dir -Filter *.csproj -File)) {
            $dir = Split-Path -Parent $dir
        }
        if ($dir -and -not (Test-Path -LiteralPath (Join-Path $dir 'obj/project.assets.json'))) {
            return $true
        }
    }
    return $false
}

# `--no-restore` keeps the hook fast, but on a workspace that was never restored dotnet format loads no package
# references, exits 0 and silently skips every fix that needs a package type. Restore first in that case.
if (Test-RestoreNeeded -Paths $Files) {
    dotnet restore
    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }
}

# IDE0059 (unused value) and IDE0060 (unused parameter) are never auto-fixed: the fixer deletes the dead store,
# and a dead store is usually a bug (a computed value that was meant to be used). CI keeps flagging them.
# IDE0130 (namespace matches folder) is excluded because its fixer crashes dotnet format ("Changing document
# properties is not supported"); the warnings-as-errors build reports it instead.
dotnet format $Subcommand --severity info --no-restore --exclude-diagnostics IDE0059 IDE0060 IDE0130 --include @Files
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

git add -- @Files
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}
