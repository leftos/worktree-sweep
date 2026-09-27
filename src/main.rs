//! `worktree-sweep`: finds stale git worktrees under a folder and reports them.

use std::io::{IsTerminal, Write};
use std::path::PathBuf;
use std::time::{SystemTime, UNIX_EPOCH};

use anyhow::{Context, Result, bail};
use clap::{Parser, Subcommand};
use tracing_subscriber::filter::{LevelFilter, Targets};
use tracing_subscriber::prelude::*;
use worktree_sweep::{pick, recycle, remove, report};

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
    },
}

fn main() -> Result<()> {
    init_tracing();
    let cli = Cli::parse();
    if let Some(Command::Unlock { .. }) = cli.command {
        bail!("`unlock` is not implemented in this build");
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
    Ok(())
}

/// The default mode after the table: pick, confirm, remove, and print one summary line per pick.
fn sweep_interactively(report: &report::Report, now: i64, out: &mut impl Write) -> Result<()> {
    let _com = recycle::ComApartment::init()?;
    let mut prompter = pick::TermPrompter;
    let picks = pick::pick(report, now, &mut prompter)?;
    if picks.is_empty() {
        writeln!(out, "Nothing picked; nothing removed.").context("cannot write the summary")?;
        return Ok(());
    }
    let swept = remove::remove_picks(&picks, &report.root, &mut prompter)?;
    for line in remove::summary(&swept, &report.root) {
        writeln!(out, "{line}").context("cannot write the summary")?;
    }
    out.flush().context("cannot flush standard output")?;
    Ok(())
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
