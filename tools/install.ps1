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

# The user PATH lives in HKCU\Environment as a raw value whose entries may hold %VAR% references. The environment API
# expands those on read and writes the expanded strings back, which would replace an owner's %USERPROFILE%\.dotnet\tools
# with a fixed path for good, so the value is read and written through the registry instead.
Add-Type -Namespace WorktreeSweep -Name NativeMethods -MemberDefinition @'
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr SendMessageTimeout(
        IntPtr hWnd, uint msg, IntPtr wParam, string lParam, uint flags, uint timeout, out IntPtr result);
'@

# Adds $Entry to the raw user environment variable $Name exactly once, keeping the %VAR% references of the entries it does
# not touch, and tells every window to re-read the environment; a value that is already there is reported and left alone.
function Add-UserPathEntry {
    param([string]$Name, [string]$Entry)

    $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Environment', $true)
    try {
        # A value that is not there yet reads as empty and is written as ExpandString, the kind a %VAR% entry needs.
        $kind = [Microsoft.Win32.RegistryValueKind]::ExpandString
        if ($key.GetValueNames() -contains $Name) { $kind = $key.GetValueKind($Name) }
        $raw = [string]$key.GetValue($Name, '', [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
        $entries = @($raw -split ';' | Where-Object { $_ })
        # An entry already on the PATH may name $Entry as %LOCALAPPDATA%\worktree-sweep, so it is expanded for the comparison.
        $wanted = $Entry.TrimEnd('\')
        if ($entries | Where-Object { [Environment]::ExpandEnvironmentVariables($_).TrimEnd('\') -ieq $wanted }) {
            Write-Host "$Entry is already on your user PATH."
            return
        }
        $write = $kind
        if ($kind -eq [Microsoft.Win32.RegistryValueKind]::String) { $write = [Microsoft.Win32.RegistryValueKind]::ExpandString }
        $key.SetValue($Name, (@($entries) + $Entry) -join ';', $write)
    }
    finally {
        $key.Dispose()
    }
    Write-Host "Added $Entry to your user PATH. Open a new terminal to use worktree-sweep."
    # A registry write tells no running program that the environment changed, so a terminal started from Explorer would not
    # see the entry: HWND_BROADCAST, WM_SETTINGCHANGE and SMTO_ABORTIFHUNG tell every top-level window to re-read it.
    $ignored = [IntPtr]::Zero
    [void][WorktreeSweep.NativeMethods]::SendMessageTimeout([IntPtr]0xffff, 0x1a, [IntPtr]::Zero, 'Environment', 0x2, 5000, [ref]$ignored)
}

# 4. The user PATH, once; the function leaves the value alone when the destination is on it already.
if ($PSCmdlet.ShouldProcess($destination, 'Add to the user PATH')) {
    Add-UserPathEntry -Name 'Path' -Entry $destination
}

# 5. An older Rust build on the PATH would win over this one; it is reported, never removed.
$rust = Join-Path $HOME '.cargo\bin\worktree-sweep.exe'
if (Test-Path -LiteralPath $rust) {
    Write-Warning "An older Rust build is installed at $rust and may shadow this one on PATH. Remove it with: cargo uninstall worktree-sweep"
}

# 6. What the run did, unless -WhatIf asked for nothing to happen.
if (-not $WhatIfPreference) { Write-Host "Installed worktree-sweep to $destination." }
