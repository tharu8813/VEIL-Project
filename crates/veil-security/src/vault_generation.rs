//! Rekey into an encrypted sibling generation, then switch all vault state with one rename.
use crate::{
    Keyring, Purpose, SecretString, invalid, publish_replace, read_envelope_file, vault_backup,
    vault_credentials,
};
use std::fs::{self, File, Metadata, OpenOptions};
use std::io::{self, Write};
use std::path::{Path, PathBuf};

const MARKER: &str = "active-generation";
const PREFIX: &str = "gen-";
const RANDOM_LENGTH: usize = 16;

/// Holds one serve process per vault, including the time spent re-encrypting.
pub fn lock_session(root: &Path) -> io::Result<File> {
    let metadata = fs::symlink_metadata(root)?;
    require_plain(&metadata)?;
    if !metadata.is_dir() {
        return Err(invalid("Vault root is not a directory"));
    }
    let path = root.join(".vault-session.lock");
    if let Ok(metadata) = fs::symlink_metadata(&path) {
        require_plain(&metadata)?;
    }
    let file = OpenOptions::new()
        .read(true)
        .write(true)
        .create(true)
        .truncate(false)
        .open(path)?;
    file.try_lock()?;
    Ok(file)
}

pub fn active(root: &Path) -> io::Result<PathBuf> {
    require_plain(&fs::symlink_metadata(root)?)?;
    let marker = root.join(MARKER);
    match fs::symlink_metadata(&marker) {
        Err(error) if error.kind() == io::ErrorKind::NotFound => return Ok(root.to_path_buf()),
        Err(error) => return Err(error),
        Ok(metadata) => require_plain(&metadata)?,
    }
    let bytes = read_envelope_file(&marker, PREFIX.len() + RANDOM_LENGTH)?;
    let name = std::str::from_utf8(&bytes).map_err(|_| invalid("Invalid vault generation"))?;
    if !is_generation_name(name) {
        return Err(invalid("Invalid vault generation"));
    }
    let directory = root.join(name);
    require_plain(&fs::symlink_metadata(&directory)?)?;
    if !directory.is_dir() {
        return Err(invalid("Vault generation is not a directory"));
    }
    Ok(directory)
}

pub fn require_no_generations(root: &Path) -> io::Result<()> {
    for entry in fs::read_dir(root)? {
        if entry?.file_name().to_str().is_some_and(is_generation_name) {
            return Err(invalid("A previous vault generation exists; cannot create a new vault here"));
        }
    }
    Ok(())
}

fn is_generation_name(name: &str) -> bool {
    name.strip_prefix(PREFIX).is_some_and(|suffix| {
        suffix.len() == RANDOM_LENGTH && suffix.bytes().all(|byte| byte.is_ascii_alphanumeric())
    })
}

/// The caller must hold the session lock and supply the currently authenticated keys.
pub fn rotate(
    root: &Path,
    current: &Path,
    keys: &Keyring,
    password: SecretString,
    code: SecretString,
) -> io::Result<(PathBuf, Keyring, bool)> {
    if active(root)? != current {
        return Err(invalid("The active vault changed during this session"));
    }
    let authenticated = vault_credentials::unlock(current, password.clone(), false)?;
    if authenticated.download.to_public() != keys.download.to_public() {
        return Err(invalid("The vault changed during this session"));
    }
    let next = keys.rotate_content_keys();
    let staging = tempfile::Builder::new()
        .prefix(PREFIX)
        .rand_bytes(RANDOM_LENGTH)
        .tempdir_in(root)?;
    let destination = staging.path();
    for name in ["objects", "meta", "index"] {
        fs::create_dir(destination.join(name))?;
    }

    let mut migrated = Vec::new();
    for name in ["objects", "meta", "index"] {
        let source = current.join(name);
        require_plain(&fs::symlink_metadata(&source)?)?;
        for entry in fs::read_dir(source)? {
            let path = entry?.path();
            let id = path
                .file_stem()
                .and_then(|stem| stem.to_str())
                .ok_or_else(|| invalid("Invalid vault object filename"))?;
            let expected_name = if name == "index" {
                id.len() == 21 && id.bytes().all(|byte| byte.is_ascii_digit())
            } else {
                vault_backup::valid_id(id.as_bytes())
            };
            if path.extension().is_none_or(|extension| extension != "veil") || !expected_name {
                // Leave unfamiliar ciphertext in place rather than silently retiring it.
                return Err(invalid("Unrecognized file prevents vault key rotation"));
            }
            let metadata = fs::symlink_metadata(&path)?;
            require_plain(&metadata)?;
            if !metadata.is_file() {
                return Err(invalid("Vault generation entry is not a regular file"));
            }
            let target = destination.join(name).join(format!("{id}.veil"));
            if name != "objects" {
                let bytes = keys.read_small(Purpose::Download, &path)?;
                next.seal_new(Purpose::Download, &target, bytes.as_slice())?;
            } else {
                keys.read_object(Purpose::Download, &path, |source| {
                    next.seal_new(Purpose::Download, &target, source)
                })?;
            }
            migrated.push(path);
        }
    }
    let mut primary = Vec::new();
    next.write_envelope_to(&mut primary, password)?;
    vault_credentials::write_new(destination, &next, &primary, code)?;
    let generation = destination
        .file_name()
        .and_then(|name| name.to_str())
        .ok_or_else(|| invalid("Invalid vault generation name"))?;
    publish_replace(&root.join(MARKER), |file| {
        file.write_all(generation.as_bytes())
    })?;
    let active = staging.keep();
    let retired = retire(root, current, &migrated);
    Ok((active, next, retired))
}

fn require_plain(metadata: &Metadata) -> io::Result<()> {
    #[cfg(windows)]
    let reparse = {
        use std::os::windows::fs::MetadataExt;
        metadata.file_attributes() & 0x400 != 0
    };
    #[cfg(not(windows))]
    let reparse = metadata.file_type().is_symlink();
    if reparse {
        return Err(invalid("Vault generation may not use links or junctions"));
    }
    Ok(())
}

fn retire(root: &Path, previous: &Path, migrated: &[PathBuf]) -> bool {
    let mut removed = true;
    for name in [
        "keyring.bundle",
        "keyring.age",
        "keyring.recovery.age",
        ".vault-credentials.lock",
    ] {
        removed &= remove(&previous.join(name), false);
    }
    for path in migrated {
        removed &= remove(path, false);
    }
    for name in ["objects", "meta", "index"] {
        removed &= remove(&previous.join(name), true);
    }
    if root != previous {
        removed &= fs::remove_dir(previous).is_ok();
    }
    removed
}

fn remove(path: &Path, directory: bool) -> bool {
    match fs::symlink_metadata(path) {
        Err(error) => error.kind() == io::ErrorKind::NotFound,
        Ok(metadata) if require_plain(&metadata).is_ok() => {
            if directory {
                fs::remove_dir(path).is_ok()
            } else {
                fs::remove_file(path).is_ok()
            }
        }
        Ok(_) => false,
    }
}
