//! A parser for the CSV that Sysinternals `handle.exe -nobanner -v` prints.
//!
//! With a name to match, each row is `Process,PID,Type,Handle,Name`; without one (every handle on the system) it is
//! `Process,PID,User,Handle,Type,Share Flags,Name`. The header line says which. The Name is last, not quoted and
//! carries a trailing space, so it is everything after the last separating comma, trimmed.

use std::path::PathBuf;

use anyhow::Result;
use tracing::warn;

/// The line `handle.exe` prints when nothing matches.
pub const NO_MATCH: &str = "No matching handles found.";

/// One open handle.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct HandleRow {
    /// The image name of the process holding the handle, such as `pwsh.exe`.
    pub process: String,
    /// The process id.
    pub pid: u32,
    /// The handle type, such as `File`.
    pub kind: String,
    /// The handle value.
    pub handle: u64,
    /// The object the handle refers to.
    pub name: PathBuf,
}

/// One process and the handles it holds.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Locker {
    /// The image name of the process.
    pub process: String,
    /// The process id.
    pub pid: u32,
    /// The handles, in the order handle.exe listed them.
    pub handles: Vec<HeldHandle>,
}

/// One handle a [`Locker`] holds.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct HeldHandle {
    /// The handle value.
    pub handle: u64,
    /// The handle type, such as `File` or `Section`.
    pub kind: String,
    /// The object the handle refers to.
    pub name: PathBuf,
}

/// Where the columns sit in one layout. Process is always first and Name always last.
#[derive(Debug, Clone, Copy)]
struct Layout {
    width: usize,
    pid: usize,
    kind: usize,
    handle: usize,
}

impl Layout {
    /// `Process,PID,Type,Handle,Name`, printed when handle.exe is given a name to match.
    const NAMED: Self = Self {
        width: 5,
        pid: 1,
        kind: 2,
        handle: 3,
    };

    /// The layout a header line such as `Process,PID,User,Handle,Type,Share Flags,Name` describes; `None` when the
    /// line is not a header.
    fn from_header(line: &str) -> Option<Self> {
        let columns: Vec<&str> = line.split(',').map(str::trim).collect();
        if columns.first() != Some(&"Process") || columns.last() != Some(&"Name") {
            return None;
        }
        let position = |title: &str| columns.iter().position(|column| *column == title);
        Some(Self {
            width: columns.len(),
            pid: position("PID")?,
            kind: position("Type")?,
            handle: position("Handle")?,
        })
    }
}

/// Parses `handle.exe -nobanner -v` output, with or without a name to match. The column layout comes from the
/// header line (`Process,PID,Type,Handle,Name` when none is seen). Blank lines and the no-match line are skipped;
/// a line with too few fields or a bad pid or handle is skipped with a warning.
///
/// # Errors
///
/// None: a bad line never fails the parse. The `Result` leaves room for errors the format may need later.
pub fn parse(stdout: &str) -> Result<Vec<HandleRow>> {
    let mut layout = Layout::NAMED;
    let mut rows = Vec::new();
    for line in stdout.lines() {
        let trimmed = line.trim();
        if trimmed.is_empty() || trimmed == NO_MATCH {
            continue;
        }
        if let Some(header) = Layout::from_header(trimmed) {
            layout = header;
            continue;
        }
        rows.extend(parse_line(trimmed, layout));
    }
    Ok(rows)
}

/// One row; the Name is everything after the last separating comma, so a comma inside it is kept.
fn parse_line(line: &str, layout: Layout) -> Option<HandleRow> {
    let fields: Vec<&str> = line.splitn(layout.width, ',').collect();
    if fields.len() < layout.width {
        warn!(
            "skipping a handle.exe line with fewer than {} fields: {line:?}",
            layout.width
        );
        return None;
    }
    let Ok(pid) = fields[layout.pid].trim().parse::<u32>() else {
        warn!("skipping a handle.exe line with a bad pid: {line:?}");
        return None;
    };
    let Some(handle) = parse_hex(fields[layout.handle].trim()) else {
        warn!("skipping a handle.exe line with a bad handle: {line:?}");
        return None;
    };
    Some(HandleRow {
        process: fields[0].trim().to_owned(),
        pid,
        kind: fields[layout.kind].trim().to_owned(),
        handle,
        name: PathBuf::from(fields[layout.width - 1].trim()),
    })
}

fn parse_hex(text: &str) -> Option<u64> {
    let digits = text
        .strip_prefix("0x")
        .or_else(|| text.strip_prefix("0X"))?;
    u64::from_str_radix(digits, 16).ok()
}

/// Groups rows by process, sorted by pid, keeping each process's handles in the order they appeared.
#[must_use]
pub fn group_by_process(rows: Vec<HandleRow>) -> Vec<Locker> {
    let mut lockers: Vec<Locker> = Vec::new();
    for row in rows {
        let held = HeldHandle {
            handle: row.handle,
            kind: row.kind,
            name: row.name,
        };
        match lockers.iter_mut().find(|locker| locker.pid == row.pid) {
            Some(locker) => locker.handles.push(held),
            None => lockers.push(Locker {
                process: row.process,
                pid: row.pid,
                handles: vec![held],
            }),
        }
    }
    lockers.sort_by_key(|locker| locker.pid);
    lockers
}

#[cfg(test)]
mod tests {
    use std::path::Path;

    use anyhow::ensure;

    use super::*;

    const SAMPLE: &str = include_str!("../tests/fixtures/handle-sample.csv");
    const DUMP_SAMPLE: &str = include_str!("../tests/fixtures/handle-dump-sample.csv");

    #[test]
    fn unfiltered_dump_layout_is_parsed() -> Result<()> {
        let rows = parse(DUMP_SAMPLE)?;
        ensure!(rows.len() == 5, "{rows:?}");
        ensure!(
            rows[4]
                == HandleRow {
                    process: "winlogon.exe".to_owned(),
                    pid: 1720,
                    kind: "File".to_owned(),
                    handle: 0x54,
                    name: PathBuf::from(r"C:\Windows\System32"),
                },
            "{rows:?}"
        );
        ensure!(
            rows[0]
                == HandleRow {
                    process: "GameInputRedistService.exe".to_owned(),
                    pid: 7036,
                    kind: "File".to_owned(),
                    handle: 0x58,
                    name: PathBuf::from(r"C:\Windows\System32"),
                },
            "{rows:?}"
        );
        ensure!(
            rows[1].kind == "Section" && rows[1].handle == 0x444,
            "{rows:?}"
        );
        ensure!(
            rows[1].name
                == Path::new(r"\Sessions\1\BaseNamedObjects\windows_shell_global_counters"),
            "{rows:?}"
        );
        ensure!(
            rows[3].name == Path::new(r"D:\example\repo.wt\feature\notes, draft.txt"),
            "{rows:?}"
        );
        let pids: Vec<u32> = group_by_process(rows)
            .iter()
            .map(|locker| locker.pid)
            .collect();
        ensure!(pids == vec![1720, 7036, 9620, 35952], "{pids:?}");
        Ok(())
    }

    #[test]
    fn parses_the_recorded_fixture() -> Result<()> {
        let rows = parse(SAMPLE)?;
        ensure!(rows.len() == 2, "{rows:?}");
        ensure!(
            rows[0]
                == HandleRow {
                    process: "pwsh.exe".to_owned(),
                    pid: 64336,
                    kind: "File".to_owned(),
                    handle: 0x54,
                    name: PathBuf::from(r"D:\worktree-sweep\.tmp\lockprobe"),
                },
            "{rows:?}"
        );
        ensure!(rows[1].handle == 0x748, "{rows:?}");
        ensure!(
            rows[1].name == Path::new(r"D:\worktree-sweep\.tmp\lockprobe\held.txt"),
            "{rows:?}"
        );
        let lockers = group_by_process(rows);
        ensure!(lockers.len() == 1, "{lockers:?}");
        ensure!(
            lockers[0].pid == 64336 && lockers[0].handles.len() == 2,
            "{lockers:?}"
        );
        Ok(())
    }

    #[test]
    fn no_match_line_is_empty() -> Result<()> {
        ensure!(parse("No matching handles found.\r\n")?.is_empty());
        Ok(())
    }

    #[test]
    fn empty_input_is_empty() -> Result<()> {
        ensure!(parse("")?.is_empty());
        ensure!(parse("\n\n")?.is_empty());
        Ok(())
    }

    #[test]
    fn malformed_line_in_the_middle_is_skipped() -> Result<()> {
        let text = "Process,PID,Type,Handle,Name\n\
                    a.exe,10,File,0x10,D:\\a \n\
                    garbage line\n\
                    b.exe,notapid,File,0x11,D:\\b \n\
                    c.exe,12,File,zz,D:\\c \n\
                    d.exe,13,File,0x13,D:\\d \n";
        let rows = parse(text)?;
        let pids: Vec<u32> = rows.iter().map(|row| row.pid).collect();
        ensure!(pids == vec![10, 13], "{rows:?}");
        Ok(())
    }

    #[test]
    fn name_containing_a_comma_is_kept_whole() -> Result<()> {
        let rows = parse("x.exe,7,File,0x1F,D:\\a,b\\c, d.txt \n")?;
        ensure!(rows.len() == 1, "{rows:?}");
        ensure!(rows[0].name == Path::new(r"D:\a,b\c, d.txt"), "{rows:?}");
        Ok(())
    }

    #[test]
    fn crlf_line_endings_are_trimmed() -> Result<()> {
        let rows = parse(
            "Process,PID,Type,Handle,Name\r\nx.exe,7,File,0x1,D:\\a \r\ny.exe,3,File,0x2,D:\\b\r\n",
        )?;
        ensure!(rows.len() == 2, "{rows:?}");
        ensure!(rows[0].name == Path::new(r"D:\a"), "{rows:?}");
        ensure!(rows[1].name == Path::new(r"D:\b"), "{rows:?}");
        let lockers = group_by_process(rows);
        ensure!(lockers[0].pid == 3 && lockers[1].pid == 7, "{lockers:?}");
        Ok(())
    }
}
