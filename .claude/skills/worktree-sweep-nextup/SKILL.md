---
name: worktree-sweep-nextup
description: Profile for the user-level `nextup` skill in the worktree-sweep repo — loaded by `nextup` at its step 0 for this project's plan convention, agents, gates, docs map and landing path. Not a loop of its own; invoke `/nextup`.
---

# worktree-sweep profile for `nextup`

The generic loop is the user-level `nextup` skill; this file supplies only what is worktree-sweep-specific.

siblings: none
linear: worktree-sweep

## Plan and tracker

- The plan lives in Linear: every task is a Linear issue in team WTS, per `~/.claude/docs/plan-operations.md`; `docs/plans/MAIN.md` is its generated snapshot, never edited by hand. Projects are worked in Linear's project order; an item no project fits goes to `Backlog`. The approved design is `docs/design.md`; every brief reads its section for the module it touches.
- A written brief waiting in `docs/plans/` and linked from its issue is dispatched as written. Re-check its "Measured facts" against the code on `main` first (brief 2 may have changed a signature it names), and fix the brief, not the implementer's result.
- Pre-loop hooks: none.
- An item **land**s after its commit. A dispatched brief file moves to `docs/plans/archive/` in the landing commit.
- A steer or a finding the item does not fix gets an **add**, in the project whose files it shares, else in `Backlog`.
- Tracker: **triage** as plan-operations says (GitHub issues reach the team through Linear's sync; an untriaged one is top-level with no project), each placed in the project that shares its files, else in `Backlog`.
- Pull requests: `gh pr list --repo leftos/worktree-sweep --state open --json number,title,headRefName`. An open PR from an item's own `<slug>` branch is that item still landing, and one from a `feat/<name>` branch is a feature PR, planned by its project's tracking issue: finish the landing (checks green, then the merge). Any other PR is triaged as plan-operations says.
- Hotspots: `src/main.rs` (CLI dispatch), `src/remove.rs` (removal orchestration that the unlock flow plugs into), `src/lib.rs` (module list).

## Owner delegation

- The owner delegates the technical design and implementation of the C# rewrite to the orchestrator, through to the cutover (WTS-22). The orchestrator settles decision rounds from the Rust behaviour, the docs and the conventions, and records each with **settle** as "Settled (orchestrator, owner-delegated)" rather than opening an `AskUserQuestion` round. This covers JSON shapes (C13's agent report), the window's layout (C15), and follow-ups such as WTS-25 to WTS-29.

  Each session refills slots until the user-level `nextup`'s context line (40%), then lands what is in flight and writes the checkpoint handoff. The owner's only step between sessions is `/clear` then `/nextup`, until the project is done.
- The owner is asked only for what needs a person: anything that runs elevated or raises a UAC prompt (the C12 manual check), a removing run against a real folder, a change to the tool's purpose or scope, or a release.
- The code stays plain, readable C#: the owner reads it to understand the tool. Prefer the obvious construct over a clever one.

## Rulings every brief carries

- **Read-only against the real D:\.** A run against D:\ uses `--list` or `--json` only. The interactive mode and any removal run only against `tempfile` fixtures or folders under `.tmp\`. Real cleanup of D:\ happens with the user at the keyboard.
- **One elevation per run**: removal is two passes; locked picks go to a single `unlock::offer(&[paths])`, one `sudo` session, one unfiltered `handle.exe` dump filtered by every locked path, a loop until clear or Done, then one retry pass.
- **Junctions are links, never trees**: remove with `remove_dir` on the link after re-checking it is still a reparse point; never size, walk, recycle or recursively delete through one (`D:\yaat-server.wt\yaat` → `X:\dev\yaat` is real).
- **The scan writes nothing**: every git call clears the repo-local env vars (`git::clear_repo_env`) and runs with `GIT_OPTIONAL_LOCKS=0`, `core.fsmonitor=false`; `merge-tree` writes to a scratch object dir.
- **Recycle Bin capacity is checked before recycling** (D:'s cap measured at 14844 MB); an item over it asks for a permanent delete instead of letting the Shell nuke it silently.
- Lints are strict (pedantic, no unwrap/panic/print): output through a locked stdout writer, tests assert with `anyhow::ensure!`.

## Agents and gates

- Explore: `Explore`. Rust design second opinion: `oracle`.
- Implementer: tier 1 and tier 2 briefs go to DeepSeek through `dispatch`, as `plan-execution` routes them; its mid-run `QUESTION` channel takes the small API-shape calls a brief leaves open, answered with `dispatch answer`. The Opus `implementer` through `Agent` takes tier 3 briefs and any brief whose open design call the orchestrator cannot settle before dispatch. The message announcing a dispatch names each brief's route.
- Reviewers: `code-review` for every item.
- Gates, each wrapped as `cmd > .tmp/<name>.log 2>&1; $rc = $LASTEXITCODE; Get-Content .tmp/<name>.log -Tail 20; "rc=$rc"` from the repo root, test runs at BelowNormal priority:
  - `cargo fmt --all -- --check`
  - `cargo clippy --all-targets --all-features -- -D warnings`
  - `cargo test` (no prek hook runs it)
  - `cargo deny check` when `Cargo.toml` or `Cargo.lock` changed
- C# gates (the rewrite, `docs/plans/csharp-rewrite.md`), each through `pwsh -NoProfile -File tools/gate.ps1 -Log .tmp/<name>.log -TimeoutSeconds 600 -Slot heavy -- <command>` from the repo root:
  - `dotnet build WorktreeSweep.slnx -c Release` (warnings are errors)
  - `dotnet test WorktreeSweep.slnx -c Release --no-build` (runs under Microsoft.Testing.Platform through the root `global.json`)
  - `dotnet csharpier check .`
  - `dotnet format style WorktreeSweep.slnx --verify-no-changes --severity info` and the same with `analyzers`
- Needs the user: anything that runs elevated. `handle.exe` sees nothing unelevated, and a UAC prompt needs a person. Ask the user to run the command with `!`, quoting a Windows path in single quotes (`'D:\x\y'`): bash eats unquoted backslashes, and `handle.exe` matches only backslash paths.
- Parent-side gate: `git status --short` in the repo.

## Traps

- **A test that runs `git` inherits the hook environment.** Fixture helpers must go through `git::clear_repo_env` (C#: `GitRunner`, which clears `GitRunner.RepoLocalEnvVars`); a test that passes on its own and fails in `git commit` is this.
- **C# tests that set process environment variables** go in the xUnit collection "process environment", which runs without parallelism; anywhere else they leak into concurrent git-backed tests.
- **`dotnet test` refuses VSTest on the .NET 10 SDK** with xUnit v3 4.x: the root `global.json` sets the Microsoft.Testing.Platform runner. Never add `xunit.runner.visualstudio` or `Microsoft.NET.Test.Sdk` back.
- **A bare class name filters to zero tests.** `dotnet test … --filter-class SweepProcessTests` runs nothing and exits 8 with "Zero tests ran", no error; the filter takes a fully qualified name or a wildcard (`--filter-class *SweepProcessTests`). A brief that scopes a run names the wildcard form, and an exit of 8 is never a pass.
- **`prek run --all-files` sees only tracked files**, so a C# hook shows "(no files to check)" on a branch whose files are still untracked; run `prek run <hook> --files <path>` to exercise it.
- **`git worktree list` can drop a worktree and still exit 0.** A linked worktree whose `.git\worktrees\<id>\gitdir` is unreadable or empty is left out of the list with no error, and a repo whose list fails is dropped by discovery; either way the live worktree comes back as an orphan "registered elsewhere". `Trace` output never reaches xUnit, so a `Discover`-based assertion names the discovered repos, the discovery errors and the orphans in its message.
- **D:\ changes under you.** Other sessions add and remove worktrees there all the time; a brief never hard-codes counts from D:\, only paths that must or must not appear.
- **Cargo's `did not finalize incremental compilation session directory … Access is denied`** is environmental and harmless.
- **`dialoguer` needs a terminal**: interactive code sits at the edge; logic is tested through pure functions with explicit choices.
- **A Shell call can put a dialog on the user's desktop.** A test or probe that reaches `IFileOperation` runs it on a thread with a timeout, so a dialog fails the test rather than hanging it. Never run such a test red on purpose while the user is at the machine.
- **D: is a Dev Drive (ReFS); C: and `%TEMP%` are not.** On C:, a freshly written, unlocked tree can fail a rename or recycle for 1–3 s while a scanner holds it. A test there that expects success can flake; a test that expects `Locked` cannot.
- **Unelevated `handle.exe` is slow (about 141 s for a dump) and sees only some of the caller's own processes.** Elevated, it takes 1.4 s. Never judge the unlock flow's speed from an unelevated run.

## Concurrency

- Ceiling: **three** implementers. The C# rewrite's chains (C2–C5, C6–C8, C9–C11 in `docs/plans/csharp-rewrite.md`) touch disjoint folders under `src/WorktreeSweep.Core/` until they meet at C11, so items from different chains run side by side; items in one chain still run in order. Shared files that serialize two items: `tests/WorktreeSweep.Tests/Fixture.cs`, `src/WorktreeSweep.Core/Git/GitRunner.cs` and the csproj files.
- Branch: each implementer gets its own worktree, `git worktree add ../worktree-sweep.wt/<slug> -b <slug> <base>`, per the user-level `nextup` §3 (an item under a feature marker is cut from `feat/<name>` instead). The main checkout hosts at most one implementer, and none while a gate runs there. Record `branch.<slug>.base` and `branch.<slug>.landOn` as §3 **Base and target** says (`main` and `main` by default).
- Gates in concurrent trees go through `tools/gate.ps1`: a heavy run waits for one of the machine-wide heavy slots, so three trees' builds and tests queue rather than oversubscribe the machine.
- Context: read the status bar's figure at every landing (`jq .context_window.used_percentage <scratchpad>/statusline.json`); past 40% stop refilling, per the user-level `nextup`.

## Docs map

| What changed | Owning docs |
|---|---|
| CLI flag, subcommand, output or behaviour a user sees | `README.md` usage and safety sections, `CHANGELOG.md` |
| A term used in a project-specific sense | `docs/README.md` glossary |
| JSON report shape | `README.md` (the `--json` section), since agents read it |
| A design decision that changes the approved design | `docs/design.md` |
| An item finished | **land** its issue after the commit; brief file to `docs/plans/archive/` |

## Landing

- A multi-file item (from a brief): commit on `<slug>` (the prek hook runs fmt and clippy), `git push -u origin <slug>`, `gh pr create --base <landOn>` with the agent-authored marker line as the body's first line, then, from the main checkout, `gh pr merge <N> --rebase` and `git pull --ff-only` on `<landOn>`.

  An item built in a worktree then has its worktree and branch removed per the user-level `nextup` §4 step 6 (`git cherry` check, `git worktree remove`, `git branch -D <slug>`) and `git push origin --delete <slug>`; `--delete-branch` cannot delete a branch a worktree has checked out. Merge PRs yourself as you go. `feat/*` names belong to feature branches alone.
- An item under a feature marker (user-level `nextup` §3, "Feature branches") has `landOn` = `feat/<name>`, so its PR targets the feature branch; the feature PR into `main` merges only through `/ship` on the feature branch, and the item is **land**ed with the note `on feat/<name>, ships with #N`. The repo has no CI, so the feature PR's checks are the local gates.
- A small change (docs, plan, config, one-line fix): commit on `main` and `git push`. A session running from a worktree offers `/ship` instead of pushing (user-level `nextup`, "A worktree session offers a ship instead of a push").
- Commit messages: ≤4-char type tag, imperative, ≤72-char subject, the session's attribution trailers.
