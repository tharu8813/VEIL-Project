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
    #[cfg(test)]
    tests::interruption_checkpoint(root, "before-marker");
    publish_replace(&root.join(MARKER), |file| {
        file.write_all(generation.as_bytes())
    })?;
    let active = staging.keep();
    #[cfg(test)]
    tests::interruption_checkpoint(root, "after-marker");
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


#[cfg(test)]
mod tests {
    use super::*;
    use std::process::{Command, Stdio};
    use std::thread;
    use std::time::{Duration, Instant};

    const ROOT_ENV: &str = "VEIL_UNIT_ROTATION_ROOT";
    const PHASE_ENV: &str = "VEIL_UNIT_ROTATION_PHASE";
    const READY: &str = ".rotation-unit-ready";
    const PASSWORD: &str = "synthetic-interruption-password";
    const OLD_CODE: &str = "synthetic-interruption-old-code";
    const NEW_CODE: &str = "synthetic-interruption-new-code";
    const FILES: [(&str, &[u8]); 3] = [
        ("objects/1234567890abcdef1234567890abcdef.veil", b"synthetic object body"),
        ("meta/1234567890abcdef1234567890abcdef.veil", b"synthetic metadata"),
        ("index/202611250102030123456.veil", b"synthetic index"),
    ];

    // This checkpoint is present only in the unit-test executable, not in Debug or Release
    // product binaries. The child is killed by the parent while waiting here.
    pub(super) fn interruption_checkpoint(root: &Path, phase: &str) {
        if std::env::var_os(ROOT_ENV).as_deref() != Some(root.as_os_str())
            || std::env::var(PHASE_ENV).as_deref() != Ok(phase)
        {
            return;
        }
        let mut signal = OpenOptions::new()
            .create_new(true)
            .write(true)
            .open(root.join(READY))
            .unwrap();
        signal.write_all(phase.as_bytes()).unwrap();
        signal.sync_all().unwrap();
        thread::sleep(Duration::from_secs(90));
        panic!("rotation interruption parent did not terminate the child");
    }

    #[test]
    fn rotation_interruption_child() {
        let Some(root) = std::env::var_os(ROOT_ENV).map(PathBuf::from) else {
            return; // The ordinary test enumeration does not supply a child fixture.
        };
        assert!(matches!(
            std::env::var(PHASE_ENV).as_deref(),
            Ok("before-marker" | "after-marker")
        ));
        let _lock = lock_session(&root).unwrap();
        let current = active(&root).unwrap();
        let keys = vault_credentials::unlock(&current, SecretString::from(PASSWORD), false).unwrap();
        rotate(
            &root,
            &current,
            &keys,
            SecretString::from(PASSWORD),
            SecretString::from(NEW_CODE),
        )
        .unwrap();
        panic!("rotation completed without reaching the requested checkpoint");
    }

    // Explicit fixture mode for the isolated VM runner. Never compiled into a product executable.
    #[test]
    fn rotation_vm_fixture() {
        let Ok(mode) = std::env::var("VEIL_UNIT_ROTATION_FIXTURE") else {
            return;
        };
        let root = PathBuf::from(std::env::var_os(ROOT_ENV).expect("synthetic fixture root"));
        assert!(root.is_absolute());
        assert_eq!(root.file_name().and_then(|name| name.to_str()), Some("profile"));
        match mode.as_str() {
            "prepare" => {
                assert!(fs::read_dir(&root).unwrap().next().is_none());
                for folder in ["objects", "meta", "index"] {
                    fs::create_dir(root.join(folder)).unwrap();
                }
                let keys = vault_credentials::create(
                    &root, SecretString::from(PASSWORD), SecretString::from(OLD_CODE),
                ).unwrap();
                for (file, bytes) in FILES {
                    keys.seal_new(Purpose::Download, &root.join(file), bytes).unwrap();
                }
                println!("ROTATION_FIXTURE_PREPARED");
            }
            "check" => {
                let phase = std::env::var(PHASE_ENV).unwrap();
                assert!(matches!(phase.as_str(), "before-marker" | "after-marker"));
                let _lock = lock_session(&root).unwrap();
                let chosen = active(&root).unwrap();
                let switched = phase == "after-marker";
                assert_eq!(chosen != root, switched);
                let old = vault_credentials::unlock(&root, SecretString::from(OLD_CODE), true).unwrap();
                let accepted = if switched { NEW_CODE } else { OLD_CODE };
                let rejected = if switched { OLD_CODE } else { NEW_CODE };
                let reopened = vault_credentials::unlock(&chosen, SecretString::from(accepted), true).unwrap();
                assert!(vault_credentials::unlock(&chosen, SecretString::from(rejected), true).is_err());
                assert!(vault_credentials::unlock(&chosen, SecretString::from(PASSWORD), false).is_ok());
                for (file, bytes) in FILES {
                    assert_eq!(&*old.read_small(Purpose::Download, &root.join(file)).unwrap(), bytes);
                    let path = chosen.join(file);
                    assert_eq!(&*reopened.read_small(Purpose::Download, &path).unwrap(), bytes);
                    if switched {
                        assert!(old.verify(Purpose::Download, &path).is_err());
                    }
                }
                println!("ROTATION_FIXTURE_RECOVERED");
            }
            _ => panic!("Unknown rotation fixture operation"),
        }
    }

    fn interrupt_at(root: &Path, phase: &str) {
        let mut child = Command::new(std::env::current_exe().unwrap())
            .args(["--exact", "vault_generation::tests::rotation_interruption_child"])
            .env(ROOT_ENV, root)
            .env(PHASE_ENV, phase)
            .stdin(Stdio::null())
            .stdout(Stdio::null())
            .stderr(Stdio::null())
            .spawn()
            .unwrap();
        let start = Instant::now();
        let ready = loop {
            if fs::read_to_string(root.join(READY)).is_ok_and(|value| value == phase) {
                break true;
            }
            if child.try_wait().unwrap().is_some() || start.elapsed() >= Duration::from_secs(60) {
                break false;
            }
            thread::sleep(Duration::from_millis(25));
        };
        // Always reap the child, even if the checkpoint was never reached.
        let killed = child.kill();
        let status = child.wait().unwrap();
        assert!(ready, "child never reached {phase}");
        killed.unwrap();
        assert!(!status.success());
    }

    #[test]
    fn interrupted_rotation_preserves_authoritative_generation_at_both_boundaries() {
        for phase in ["before-marker", "after-marker"] {
            let lab = tempfile::tempdir().unwrap();
            let root = lab.path();
            for folder in ["objects", "meta", "index"] {
                fs::create_dir(root.join(folder)).unwrap();
            }
            let original = vault_credentials::create(
                root,
                SecretString::from(PASSWORD),
                SecretString::from(OLD_CODE),
            )
            .unwrap();
            let mut ciphertexts = Vec::new();
            for (file, bytes) in FILES {
                let path = root.join(file);
                original.seal_new(Purpose::Download, &path, bytes).unwrap();
                ciphertexts.push(fs::read(path).unwrap());
            }

            interrupt_at(root, phase);
            let _lock = lock_session(root).expect("interrupted process released the session lock");
            let chosen = active(root).unwrap();
            let switched = phase == "after-marker";
            assert_eq!(chosen != root, switched);
            let accepted = if switched { NEW_CODE } else { OLD_CODE };
            let rejected = if switched { OLD_CODE } else { NEW_CODE };
            let reopened = vault_credentials::unlock(
                &chosen, SecretString::from(accepted), true,
            ).unwrap();
            assert!(vault_credentials::unlock(
                &chosen, SecretString::from(rejected), true,
            ).is_err());
            assert!(vault_credentials::unlock(
                &chosen, SecretString::from(PASSWORD), false,
            ).is_ok());

            for ((file, bytes), old_ciphertext) in FILES.into_iter().zip(ciphertexts) {
                let path = chosen.join(file);
                assert_eq!(&*reopened.read_small(Purpose::Download, &path).unwrap(), bytes);
                // The previous generation must not have been retired before the switch;
                // after switching but before retirement it is intentionally still present.
                assert_eq!(fs::read(root.join(file)).unwrap(), old_ciphertext);
                if switched {
                    assert!(original.verify(Purpose::Download, &path).is_err());
                } else {
                    assert_eq!(fs::read(path).unwrap(), old_ciphertext);
                }
            }
            // There is exactly one new sibling; an interrupted rotation must not silently
            // delete retained encrypted generations or choose them merely because they exist.
            assert_eq!(fs::read_dir(root).unwrap().filter(|entry| {
                entry.as_ref().is_ok_and(|entry| {
                    entry.file_name().to_str().is_some_and(is_generation_name)
                })
            }).count(), 1);
        }
    }
}
