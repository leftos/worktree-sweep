#Requires -Version 7
<#
.SYNOPSIS
Installs worktree-sweep for the current user: publishes it into the user's own folder and puts that folder on the user PATH.

.DESCRIPTION
Checks that the .NET 10 Desktop Runtime is installed and that no worktree-sweep is running out of the destination folder,
publishes src\WorktreeSweep\WorktreeSweep.csproj there, framework-dependent, and adds the destination to the user PATH
when it is not on it already. Nothing outside the user's own profile is changed: an older Rust build under ~\.cargo\bin is
reported and never removed.

-WhatIf is a dry run: it prints the publish and the PATH change it would make and changes nothing.

.EXAMPLE
pwsh tools/install.ps1
#>

[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingWriteHost', '',
    Justification = 'The install''s progress lines are its whole output, for the console; it returns nothing to the pipeline.')]
[CmdletBinding(SupportsShouldProcess)]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$missingRuntime = 'The .NET 10 Desktop Runtime is not installed. Install it with: winget install Microsoft.DotNet.DesktopRuntime.10'

# 1. The runtime the published tool needs, which lists itself as Microsoft.WindowsDesktop.App 10.x; a missing dotnet lands here too.
$desktop = $null
try {
    $desktop = & dotnet --list-runtimes 2>$null | Where-Object { $_ -match '^Microsoft\.WindowsDesktop\.App 10\.' }
}
catch {
    $desktop = $null
}
if (-not $desktop) { throw $missingRuntime }

# 2. The destination, and a copy running out of it that holds the folder open while the publish overwrites it.
$destination = Join-Path $env:LOCALAPPDATA 'worktree-sweep'
$under = $destination.TrimEnd('\') + '\'
foreach ($process in (Get-Process -Name worktree-sweep -ErrorAction SilentlyContinue)) {
    $path = $null
    try { $path = $process.Path } catch { $path = $null }
    if ($path -and $path.StartsWith($under, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "worktree-sweep is running from $destination (PID $($process.Id)). Close it and run the install again."
    }
}

# 3. The publish itself, framework-dependent, into the destination folder.
$project = Join-Path (Split-Path -Parent $PSScriptRoot) 'src\WorktreeSweep\WorktreeSweep.csproj'
if ($PSCmdlet.ShouldProcess($destination, 'Publish worktree-sweep')) {
    & dotnet publish $project -c Release -o $destination
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }
}

# 4. The user PATH, once: an entry equal to the destination, case-insensitively and with a trailing backslash ignored, is left alone.
if ($PSCmdlet.ShouldProcess($destination, 'Add to the user PATH')) {
    $entries = @([Environment]::GetEnvironmentVariable('Path', 'User') -split ';' | Where-Object { $_ })
    $wanted = $destination.TrimEnd('\')
    if ($entries | Where-Object { $_.TrimEnd('\') -ieq $wanted }) {
        Write-Host "$destination is already on your user PATH."
    }
    else {
        [Environment]::SetEnvironmentVariable('Path', (@($entries) + $destination) -join ';', 'User')
        Write-Host "Added $destination to your user PATH. Open a new terminal to use worktree-sweep."
    }
}

# 5. An older Rust build on the PATH would win over this one; it is reported, never removed.
$rust = Join-Path $HOME '.cargo\bin\worktree-sweep.exe'
if (Test-Path -LiteralPath $rust) {
    Write-Warning "An older Rust build is installed at $rust and may shadow this one on PATH. Remove it with: cargo uninstall worktree-sweep"
}

# 6. What the run did, unless -WhatIf asked for nothing to happen.
if (-not $WhatIfPreference) { Write-Host "Installed worktree-sweep to $destination." }
