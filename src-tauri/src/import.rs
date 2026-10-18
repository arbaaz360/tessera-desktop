//! Adding pictures, the shell's part: the file picker's filter, and a log
//! line per drop.
//!
//! Tauri 2's drag-drop handler is on for the main window (`dragDropEnabled`,
//! its default): files dragged from File Explorer or Finder arrive as paths,
//! with the pointer position, as the `tauri://drag-*` events. The web app
//! listens to them, decides the target from what is under the pointer (an
//! album or the library) and sends the paths to the core
//! (`POST /api/import`). The core decides what each path means: indexed where
//! it is, or copied into the Tessera folder. The shell only logs what came in,
//! in counts, so a drop that "did nothing" can be told from one that never
//! arrived.

use std::path::PathBuf;

/// What the core's scanner enumerates as pictures and videos; the file picker
/// offers the same.
pub const MEDIA_EXTENSIONS: &[&str] = &[
    "jpg", "jpeg", "png", "webp", "gif", "bmp", "tif", "tiff", "heic", "heif", "avif", "jxl",
    "dng", "cr2", "cr3", "nef", "arw", "orf", "rw2", "raf", "mp4", "mov", "webm", "mkv", "avi",
    "m4v", "wmv", "vob", "3gp", "mts", "m2ts", "mpg", "mpeg", "flv", "insp", "insv",
];

/// A drop, in counts.
#[derive(Debug, Default, PartialEq, Eq)]
pub struct DropSummary {
    pub files: usize,
    pub media: usize,
    pub folders: usize,
    pub missing: usize,
}

/// Count what a drop carried: folders, files (and how many of those are
/// pictures or videos), and paths that are not there.
pub fn summarize(paths: &[PathBuf]) -> DropSummary {
    let mut s = DropSummary::default();
    for path in paths {
        if path.is_dir() {
            s.folders += 1;
        } else if path.is_file() {
            s.files += 1;
            if is_media(path) {
                s.media += 1;
            }
        } else {
            s.missing += 1;
        }
    }
    s
}

pub fn is_media(path: &std::path::Path) -> bool {
    path.extension()
        .and_then(|e| e.to_str())
        .map(|e| MEDIA_EXTENSIONS.iter().any(|m| m.eq_ignore_ascii_case(e)))
        .unwrap_or(false)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn a_drop_is_counted_by_kind() {
        let dir = tempfile::tempdir().unwrap();
        let photo = dir.path().join("a.JPG");
        let note = dir.path().join("notes.txt");
        let folder = dir.path().join("Holiday");
        std::fs::write(&photo, b"x").unwrap();
        std::fs::write(&note, b"x").unwrap();
        std::fs::create_dir(&folder).unwrap();

        let s = summarize(&[photo, note, folder, dir.path().join("gone.png")]);
        assert_eq!(
            s,
            DropSummary {
                files: 2,
                media: 1,
                folders: 1,
                missing: 1
            }
        );
    }

    #[test]
    fn the_picker_offers_pictures_and_videos_once_each() {
        let mut seen = std::collections::HashSet::new();
        for ext in MEDIA_EXTENSIONS {
            assert!(seen.insert(*ext), "{ext} listed twice");
            assert_eq!(*ext, ext.to_ascii_lowercase());
        }
        for wanted in ["jpg", "png", "heic", "mp4", "mov"] {
            assert!(seen.contains(wanted), "{wanted} missing");
        }
    }
}
