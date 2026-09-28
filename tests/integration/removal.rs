use std::fs::{self, OpenOptions};
use std::io::Write;
use std::os::windows::fs::OpenOptionsExt;
use std::path::{Path, PathBuf};
use std::process::Command;
use std::sync::mpsc;
use std::time::Duration;

use anyhow::{Context, Result, ensure};
use worktree_sweep::discover::OrphanKind;
use worktree_sweep::remove::{
    Action, BranchChoice, Decision, Method, Outcome, Plan, Progress, RemoveError, after_removed,
    branch_offer, permanent_delete, remove_candidate, remove_picks,
};
use worktree_sweep::report::Candidate;

use crate::fixture::{Fixture, add_worktree, git, same_path};

/// Makes a directory junction; `false` (with a logged message) when `mklink /J` is unavailable.
fn make_junction(link: &Path, target: &Path) -> Result<bool> {
    match Command::new("cmd")
        .arg("/c")
        .arg("mklink")
        .arg("/J")
        .arg(link)
        .arg(target)
        .output()
    {
        Ok(output) if output.status.success() => Ok(true),
        Ok(output) => {
            writeln!(
                std::io::stderr(),
                "skipping: mklink /J failed: {}",
                String::from_utf8_lossy(&output.stderr).trim()
            )?;
            Ok(false)
        }
        Err(error) => {
            writeln!(
                std::io::stderr(),
                "skipping: cannot run cmd /c mklink /J: {error}"
            )?;
            Ok(false)
        }
    }
}

fn make_readonly(path: &Path) -> Result<()> {
    let mut permissions = fs::metadata(path)?.permissions();
    permissions.set_readonly(true);
    fs::set_permissions(path, permissions)?;
    Ok(())
}

#[test]
fn permanent_delete_clears_readonly_files() -> Result<()> {
    let fx = Fixture::new()?;
    let tree = fx.path("tree");
    fs::create_dir_all(tree.join("objects").join("ab"))?;
    let object = tree.join("objects").join("ab").join("cdef");
    fs::write(&object, "blob")?;
    make_readonly(&object)?;
    fs::write(tree.join("plain.txt"), "x")?;

    permanent_delete(&tree)?;
    ensure!(!tree.exists(), "{} still exists", tree.display());
    Ok(())
}

#[test]
fn permanent_delete_does_not_follow_junctions() -> Result<()> {
    let fx = Fixture::new()?;
    let target = fx.path("outside");
    fs::create_dir_all(&target)?;
    let kept = target.join("keep.txt");
    fs::write(&kept, "keep")?;
    let tree = fx.path("tree");
    fs::create_dir_all(tree.join("sub"))?;
    if !make_junction(&tree.join("sub").join("link"), &target)? {
        return Ok(());
    }

    permanent_delete(&tree)?;
    ensure!(!tree.exists(), "{} still exists", tree.display());
    ensure!(
        kept.exists(),
        "the junction's target lost {}",
        kept.display()
    );
    Ok(())
}

#[test]
fn locked_file_is_classified_as_locked() -> Result<()> {
    let fx = Fixture::new()?;
    let tree = fx.path("tree");
    fs::create_dir_all(&tree)?;
    let held = tree.join("held.txt");
    fs::write(&held, "held")?;
    let _handle = OpenOptions::new().read(true).share_mode(0).open(&held)?;

    match permanent_delete(&tree) {
        Err(RemoveError::Locked {
            path,
            first_locked_file,
        }) => {
            ensure!(same_path(&path, &tree), "locked path: {}", path.display());
            let file = first_locked_file.context("no locked file named")?;
            ensure!(same_path(&file, &held), "locked file: {}", file.display());
        }
        other => anyhow::bail!("expected Locked, got {other:?}"),
    }
    Ok(())
}

#[test]
fn removing_junction_orphan_keeps_target() -> Result<()> {
    let fx = Fixture::new()?;
    let target = fx.path("live-repo");
    fs::create_dir_all(&target)?;
    let kept = target.join("file.txt");
    fs::write(&kept, "keep")?;
    fs::create_dir_all(fx.path("x.wt"))?;
    let link = fx.path("x.wt").join("yaat");
    if !make_junction(&link, &target)? {
        return Ok(());
    }

    let report = fx.scan()?;
    let candidate = report
        .candidates
        .iter()
        .find(|candidate| same_path(candidate.path(), &link))
        .context("the link is not a candidate")?;
    ensure!(
        matches!(candidate, Candidate::Orphan(orphan) if orphan.orphan.orphan_kind == OrphanKind::Link),
        "{candidate:?}"
    );
    remove_candidate(candidate, Action::RemoveLink)?;

    ensure!(
        fs::symlink_metadata(&link).is_err(),
        "{} still exists",
        link.display()
    );
    ensure!(kept.exists(), "the link's target lost {}", kept.display());
    Ok(())
}

#[test]
fn removing_registered_worktree_prunes_registration() -> Result<()> {
    let fx = Fixture::new()?;
    let repo = fx.repo("repo")?;
    let wt = fx.path("repo.wt/feat");
    add_worktree(&repo, &wt, "feat")?;

    let report = fx.scan()?;
    let candidate = report
        .candidates
        .iter()
        .find(|candidate| same_path(candidate.path(), &wt))
        .context("the worktree is not a candidate")?;
    let Candidate::Registered(registered) = candidate else {
        anyhow::bail!("not registered: {candidate:?}");
    };
    let offer = branch_offer(registered).context("no branch offer for a branch with no commits")?;
    ensure!(
        !offer.force,
        "a branch with no commits needs only -d: {offer:?}"
    );

    remove_candidate(candidate, Action::Delete(Method::Permanent))?;
    let notes = after_removed(candidate, &BranchChoice::Delete(offer));

    ensure!(!wt.exists(), "{} still exists", wt.display());
    let list = git(&repo, &["worktree", "list", "--porcelain"])?;
    ensure!(!list.contains("repo.wt/feat"), "still listed:\n{list}");
    let branches = git(&repo, &["branch", "--list", "feat"])?;
    ensure!(
        branches.is_empty(),
        "branch feat survives: {branches}; notes: {notes:?}"
    );
    ensure!(notes == ["branch feat deleted"], "notes: {notes:?}");
    Ok(())
}

#[test]
fn declined_branch_is_kept_with_a_note() -> Result<()> {
    let fx = Fixture::new()?;
    let repo = fx.repo("repo")?;
    let wt = fx.path("repo.wt/feat");
    add_worktree(&repo, &wt, "feat")?;

    let report = fx.scan()?;
    let candidate = report
        .candidates
        .iter()
        .find(|candidate| same_path(candidate.path(), &wt))
        .context("the worktree is not a candidate")?;
    let Candidate::Registered(registered) = candidate else {
        anyhow::bail!("not registered: {candidate:?}");
    };
    let offer = branch_offer(registered).context("no branch offer for a branch with no commits")?;

    remove_candidate(candidate, Action::Delete(Method::Permanent))?;
    let notes = after_removed(candidate, &BranchChoice::Keep(offer));

    ensure!(notes == ["branch feat kept"], "notes: {notes:?}");
    let branches = git(&repo, &["branch", "--list", "feat"])?;
    ensure!(
        branches.contains("feat"),
        "branch feat is gone: {branches:?}"
    );
    Ok(())
}

#[test]
fn skipped_decision_never_touches_the_disk() -> Result<()> {
    let fx = Fixture::new()?;
    let folder = fx.path("x.wt").join("stray");
    fs::create_dir_all(&folder)?;
    fs::write(folder.join("file.txt"), "keep")?;

    let report = fx.scan()?;
    let candidate = report
        .candidates
        .iter()
        .find(|candidate| same_path(candidate.path(), &folder))
        .context("the stray folder is not a candidate")?;
    let decisions = [Decision {
        candidate,
        plan: Plan::Skip("not confirmed".to_owned()),
        branch: BranchChoice::NotOffered,
    }];
    let mut progress: Vec<Progress> = Vec::new();
    let mut offers = 0_usize;
    let swept = remove_picks(&decisions, &mut |step| progress.push(step), &mut |_| {
        offers += 1;
        Ok(worktree_sweep::unlock::UnlockOutcome::Skipped)
    });

    ensure!(
        folder.join("file.txt").exists(),
        "{} was touched",
        folder.display()
    );
    ensure!(swept.len() == 1, "swept: {swept:?}");
    ensure!(
        swept[0].outcome == Outcome::Skipped("not confirmed".to_owned()),
        "outcome: {:?}",
        swept[0].outcome
    );
    ensure!(offers == 0, "offer_unlock was called {offers} times");
    ensure!(progress.is_empty(), "progress: {progress:?}");
    Ok(())
}

/// Touches the real Recycle Bin: run by hand with `cargo test -- --ignored recycle_moves_folder_to_recycle_bin`.
#[test]
#[ignore = "moves a scratch folder to the real Recycle Bin"]
fn recycle_moves_folder_to_recycle_bin() -> Result<()> {
    let scratch: PathBuf = Path::new(env!("CARGO_MANIFEST_DIR"))
        .join(".tmp")
        .join(format!("recycle-test-{}", std::process::id()));
    fs::create_dir_all(scratch.join("sub"))?;
    fs::write(scratch.join("sub").join("file.txt"), "recycle me")?;

    let _com = worktree_sweep::recycle::ComApartment::init()?;
    worktree_sweep::recycle::recycle(&scratch)?;
    ensure!(!scratch.exists(), "{} still exists", scratch.display());
    Ok(())
}

/// A folder with a file held open with no sharing is reported as `Locked` by `recycle`, without the Shell's
/// "Folder In Use" dialog and without recycling any part of it. `recycle` runs on its own thread so that a dialog
/// fails the test on the timeout instead of hanging it.
#[test]
fn recycling_locked_folder_is_classified_as_locked() -> Result<()> {
    let fx = Fixture::new()?;
    let tree = fx.path("tree");
    fs::create_dir_all(tree.join("sub"))?;
    let held = tree.join("sub").join("held.txt");
    fs::write(&held, "held")?;
    let loose = tree.join("loose.txt");
    fs::write(&loose, "loose")?;
    let _handle = OpenOptions::new().read(true).share_mode(0).open(&held)?;

    let (sender, receiver) = mpsc::channel();
    let target = tree.clone();
    std::thread::spawn(move || {
        let result = worktree_sweep::recycle::ComApartment::init()
            .map_err(RemoveError::from)
            .and_then(|_com| worktree_sweep::recycle::recycle(&target));
        let _ = sender.send(result);
    });
    let result = receiver
        .recv_timeout(Duration::from_secs(10))
        .context("recycle did not return within 10 s: the Shell is showing a dialog")?;

    match result {
        Err(RemoveError::Locked { path, .. }) => {
            ensure!(same_path(&path, &tree), "locked path: {}", path.display());
        }
        other => anyhow::bail!("expected Locked, got {other:?}"),
    }
    ensure!(
        held.exists() && loose.exists(),
        "part of {} was recycled",
        tree.display()
    );
    Ok(())
}
