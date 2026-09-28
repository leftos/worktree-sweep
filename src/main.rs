//! `worktree-sweep`: finds stale git worktrees under a folder and reports them.

use std::io::{IsTerminal, Write};
use std::path::{Path, PathBuf};
use std::process::ExitCode;
use std::time::{SystemTime, UNIX_EPOCH};

use anyhow::{Context, Result};
use clap::{Parser, Subcommand};
use dialoguer::Confirm;
use dialoguer::console::Term;
use tracing_subscriber::filter::{LevelFilter, Targets};
use tracing_subscriber::prelude::*;
use worktree_sweep::tui::review::{self, Review, StepKind};
use worktree_sweep::{agent, pick, recycle, remove, report, unlock};

/// Find stale git worktrees and orphan worktree folders under ROOT.
#[derive(Debug, Parser)]
#[command(version, about, args_conflicts_with_subcommands = true)]
struct Cli {
    #[command(subcommand)]
    command: Option<Command>,

    /// Folder to scan (default: the current directory).
    root: Option<PathBuf>,

    /// Print the report as one JSON document and exit.
    #[arg(long)]
    json: bool,

    /// List the candidates and exit.
    #[arg(long)]
    list: bool,
}

#[derive(Debug, Subcommand)]
enum Command {
    /// Clear file locks on the given paths (runs elevated).
    #[command(hide = true)]
    Unlock {
        /// Folders whose locks to clear.
        #[arg(required = true)]
        paths: Vec<PathBuf>,

        /// The process that started the unelevated run; it is marked, and stopping it is not the default.
        #[arg(long, hide = true)]
        caller_pid: Option<u32>,

        /// The unelevated worktree-sweep; it and its children are never offered.
        #[arg(long, hide = true)]
        sweep_pid: Option<u32>,
    },

    /// Remove one worktree without asking, or report what holds it and mark it released (for agents).
    Remove {
        /// The root folder of a registered linked worktree.
        path: PathBuf,

        /// Print the result as one JSON document (required).
        #[arg(long, required = true)]
        json: bool,

        /// Remove it even when uncommitted, unmerged or unpushed work would be lost; lifts a git lock first.
        #[arg(long)]
        force: bool,

        /// Stop the build and language servers holding it (cargo, rust-analyzer, .NET build servers), then retry.
        #[arg(long)]
        stop_build_servers: bool,
    },
}

fn main() -> Result<ExitCode> {
    init_tracing();
    let cli = Cli::parse();
    match cli.command {
        Some(Command::Unlock {
            paths,
            caller_pid,
            sweep_pid,
        }) => {
            return Ok(ExitCode::from(unlock::run_elevated(
                &paths, caller_pid, sweep_pid,
            )?));
        }
        Some(Command::Remove {
            path,
            json: _,
            force,
            stop_build_servers,
        }) => {
            return remove_one(
                &path,
                agent::Options {
                    force,
                    stop_build_servers,
                },
            );
        }
        None => {}
    }
    let root = match cli.root {
        Some(root) => root,
        None => std::env::current_dir().context("cannot read the current directory")?,
    };
    let interactive = !cli.json && !cli.list;
    if interactive {
        pick::ensure_interactive()?;
    }
    let report = worktree_sweep::scan(&root)?;
    let now = now_unix();
    let stdout = std::io::stdout();
    let mut out = stdout.lock();
    if cli.json {
        report::write_json(&report, &mut out)?;
    } else {
        write!(out, "{}", report::render_table(&report, now)).context("cannot write the table")?;
    }
    out.flush().context("cannot flush standard output")?;
    if interactive && !report.candidates.is_empty() {
        sweep_interactively(&report, now, &mut out)?;
    }
    Ok(ExitCode::SUCCESS)
}

/// `remove`: one worktree, one JSON document; the exit code carries the status.
fn remove_one(path: &Path, options: agent::Options) -> Result<ExitCode> {
    let report = agent::run(path, options)?;
    let stdout = std::io::stdout();
    let mut out = stdout.lock();
    agent::write_json(&report, &mut out)?;
    out.flush().context("cannot flush standard output")?;
    Ok(ExitCode::from(agent::exit_code(report.status)))
}

/// The default mode after the table: pick, answer every question, confirm once, remove, and print one summary line
/// per pick.
fn sweep_interactively(report: &report::Report, now: i64, out: &mut impl Write) -> Result<()> {
    let _com = recycle::ComApartment::init()?;
    let picked = pick::pick(report, now)?;
    if picked.is_empty() {
        writeln!(out, "Nothing picked; nothing removed.").context("cannot write the summary")?;
        return Ok(());
    }
    let Some(decisions) = review_on_terminal(&picked, &report.root)? else {
        writeln!(out, "Nothing removed.").context("cannot write the summary")?;
        return Ok(());
    };
    let swept = remove::remove_picks(&decisions, &mut |_| {}, &mut |paths| unlock::offer(paths));
    for line in remove::summary(&swept, &report.root) {
        writeln!(out, "{line}").context("cannot write the summary")?;
    }
    out.flush().context("cannot flush standard output")?;
    Ok(())
}

/// Asks Review's questions on the terminal, then the final confirmation (default no) unless every pick is
/// skipped; `None` when the user cancels there.
fn review_on_terminal<'a>(
    picked: &[&'a report::Candidate],
    root: &Path,
) -> Result<Option<Vec<remove::Decision<'a>>>> {
    let term = Term::stderr();
    let mut review = Review::new(picked, root, &mut remove::read_capacity);
    while let Some(step) = review.step() {
        let body = match step.kind {
            StepKind::Loss | StepKind::Link => {
                let name = picked
                    .get(step.pick)
                    .map(|candidate| report::relative_path(candidate.path(), root))
                    .unwrap_or_default();
                format!("{name}: {}", step.body)
            }
            StepKind::Permanent | StepKind::Branch => step.body.clone(),
        };
        term.write_line(&body)?;
        let yes = Confirm::new()
            .with_prompt(step.question)
            .default(step.default)
            .interact()?;
        review.answer(yes);
    }
    let totals = review
        .totals()
        .context("the review ended with a question unanswered")?;
    let runnable = totals.recycle + totals.permanent + totals.links + totals.prunes;
    if runnable > 0 {
        term.write_line(&review::final_sentence(&totals))?;
        let go = Confirm::new()
            .with_prompt("Remove them?")
            .default(false)
            .interact()?;
        if !go {
            return Ok(None);
        }
    }
    review
        .decisions()
        .context("the review ended with a question unanswered")
        .map(Some)
}

/// The current time in unix seconds; 0 when the clock is before 1970.
fn now_unix() -> i64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .ok()
        .and_then(|elapsed| i64::try_from(elapsed.as_secs()).ok())
        .unwrap_or_default()
}

/// Logs to stderr; the filter comes from `RUST_LOG` (`warn`, `worktree_sweep=debug`, …), default `warn`.
fn init_tracing() {
    let filter = std::env::var("RUST_LOG")
        .ok()
        .and_then(|value| value.parse::<Targets>().ok())
        .unwrap_or_else(|| Targets::new().with_default(LevelFilter::WARN));
    tracing_subscriber::registry()
        .with(
            tracing_subscriber::fmt::layer()
                .with_writer(std::io::stderr)
                .with_ansi(std::io::stderr().is_terminal()),
        )
        .with(filter)
        .init();
}
