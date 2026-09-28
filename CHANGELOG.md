# Changelog

## [Unreleased]

### Added

- Scan a folder for linked git worktrees and orphan worktree folders, and list them with `--list` or as a JSON report with `--json`.
- Show each worktree's merge state (merged, no commits, cherry-picked, squashed, unmerged), uncommitted files, unpushed commits, last activity and size.
- Pick worktrees to remove, with a second confirmation for any that would lose work or have no commits yet.
- Removed folders go to the Recycle Bin; a folder too big for it asks for a permanent delete instead.
- Junction and symbolic link orphans are removed as links, leaving their targets untouched.
- Prune removed worktrees' registrations and offer to delete their branches when merged.
- When folders are locked, one `sudo` prompt lists the processes holding them and lets you stop them or close their handles, then retries.
- `remove <PATH> --json` lets an agent remove its own worktree without prompts; a locked one is reported with its holders, and the next sweep lists it first, already picked.
