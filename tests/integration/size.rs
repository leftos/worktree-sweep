use std::fs;

use anyhow::{Result, ensure};
use worktree_sweep::signals::walk_size;

use crate::fixture::Fixture;

#[test]
fn size_walk_counts_files_and_bytes() -> Result<()> {
    let fx = Fixture::new()?;
    let dir = fx.path("measured");
    fs::create_dir_all(dir.join("sub").join("empty"))?;
    fs::write(dir.join("a.txt"), "abc")?;
    fs::write(dir.join("sub").join("b.txt"), "defgh")?;

    let size = walk_size(&dir);
    ensure!(
        size.files == 2 && size.bytes == 8 && size.unreadable == 0,
        "got {size:?}"
    );
    ensure!(
        size.last_write_unix.is_some(),
        "no last write time: {size:?}"
    );
    Ok(())
}
