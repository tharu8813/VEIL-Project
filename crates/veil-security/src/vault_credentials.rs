//! One atomic publication point for the two independently encrypted vault key envelopes.
use crate::{
    ENVELOPE_LIMIT, Keyring, SecretString, invalid, publish_new, publish_replace,
    read_envelope_file,
};
use std::fs::{self, File, OpenOptions};
use std::io::{self, Write};
use std::path::Path;

const BUNDLE: &str = "keyring.bundle";
const LEGACY_PRIMARY: &str = "keyring.age";
const LEGACY_RECOVERY: &str = "keyring.recovery.age";
const MAGIC: &[u8] = b"VEIL-VAULT-CREDENTIALS-1\n";
const HEADER_SIZE: usize = MAGIC.len() + 4;
const LIMIT: usize = HEADER_SIZE + ENVELOPE_LIMIT * 2;

pub fn create(dir: &Path, passphrase: SecretString, code: SecretString) -> io::Result<Keyring> {
    let _lock = lock(dir)?;
    for name in [BUNDLE, LEGACY_PRIMARY, LEGACY_RECOVERY] {
        if dir.join(name).try_exists()? {
            return Err(io::Error::new(
                io::ErrorKind::AlreadyExists,
                "Vault credentials already exist",
            ));
        }
    }
    let keys = Keyring::generate();
    let primary = envelope(&keys, passphrase)?;
    write_new(dir, &keys, &primary, code)?;
    Ok(keys)
}

pub(crate) fn write_new(
    dir: &Path,
    keys: &Keyring,
    primary: &[u8],
    code: SecretString,
) -> io::Result<()> {
    let recovery = envelope(keys, code)?;
    publish_new(&dir.join(BUNDLE), |file| {
        write_pair(file, primary, &recovery)
    })
}

pub fn unlock(dir: &Path, secret: SecretString, recovery: bool) -> io::Result<Keyring> {
    if let Some(bytes) = read_bundle(dir)? {
        let (pass, code) = pair(&bytes)?;
        let keys = Keyring::unlock_envelope(if recovery { code } else { pass }, secret)?;
        retire_legacy(dir);
        return Ok(keys);
    }
    let name = if recovery {
        LEGACY_RECOVERY
    } else {
        LEGACY_PRIMARY
    };
    Keyring::unlock(&dir.join(name), secret)
}

/// Preserves a slot when its replacement is absent. Both resulting envelopes are durable in the
/// staging file before its single rename. The lock keeps concurrent single-slot edits from racing.
pub fn replace(
    dir: &Path,
    keys: &Keyring,
    passphrase: Option<SecretString>,
    code: Option<SecretString>,
) -> io::Result<()> {
    let _lock = lock(dir)?;
    let previous = read_bundle(dir)?;
    let current = previous.as_deref().map(pair).transpose()?;
    let slot = |replacement, existing: Option<&[u8]>, legacy| match replacement {
        Some(secret) => envelope(keys, secret),
        None => match existing {
            Some(bytes) => Ok(bytes.to_vec()),
            None => read_envelope_file(&dir.join(legacy), ENVELOPE_LIMIT),
        },
    };
    let primary = slot(passphrase, current.map(|(pass, _)| pass), LEGACY_PRIMARY)?;
    let recovery = slot(code, current.map(|(_, code)| code), LEGACY_RECOVERY)?;
    publish_replace(&dir.join(BUNDLE), |file| {
        write_pair(file, &primary, &recovery)
    })?;
    retire_legacy(dir);
    Ok(())
}

fn retire_legacy(dir: &Path) {
    // Retry on a later successful unlock if the previous process stopped after publication.
    for name in [LEGACY_PRIMARY, LEGACY_RECOVERY] {
        if let Err(error) = fs::remove_file(dir.join(name))
            && error.kind() != io::ErrorKind::NotFound
        {
            eprintln!(
                "Could not remove superseded vault envelope ({:?})",
                error.kind()
            );
        }
    }
}

fn lock(dir: &Path) -> io::Result<File> {
    let file = OpenOptions::new()
        .read(true)
        .write(true)
        .create(true)
        .truncate(false)
        .open(dir.join(".vault-credentials.lock"))?;
    file.lock()?;
    Ok(file)
}

fn read_bundle(dir: &Path) -> io::Result<Option<Vec<u8>>> {
    match read_envelope_file(&dir.join(BUNDLE), LIMIT) {
        Ok(bytes) => Ok(Some(bytes)),
        Err(error) if error.kind() == io::ErrorKind::NotFound => Ok(None),
        Err(error) => Err(error),
    }
}

fn envelope(keys: &Keyring, secret: SecretString) -> io::Result<Vec<u8>> {
    let mut bytes = Vec::new();
    keys.write_envelope_to(&mut bytes, secret)?;
    Ok(bytes)
}

fn write_pair(file: &mut File, primary: &[u8], recovery: &[u8]) -> io::Result<()> {
    validate_sizes(primary, recovery)?;
    file.write_all(MAGIC)?;
    file.write_all(&(primary.len() as u32).to_le_bytes())?;
    file.write_all(primary)?;
    file.write_all(recovery)
}

fn pair(bytes: &[u8]) -> io::Result<(&[u8], &[u8])> {
    let bytes = bytes
        .strip_prefix(MAGIC)
        .ok_or_else(|| invalid("Unknown vault credentials format"))?;
    let (length, envelopes) = bytes
        .split_first_chunk::<4>()
        .ok_or_else(|| invalid("Truncated vault credentials header"))?;
    let (primary, recovery) = envelopes
        .split_at_checked(u32::from_le_bytes(*length) as usize)
        .ok_or_else(|| invalid("Truncated vault credentials"))?;
    validate_sizes(primary, recovery)?;
    Ok((primary, recovery))
}

fn validate_sizes(primary: &[u8], recovery: &[u8]) -> io::Result<()> {
    if [primary, recovery]
        .iter()
        .any(|bytes| !(1..=ENVELOPE_LIMIT).contains(&bytes.len()))
    {
        return Err(invalid("Invalid vault credential envelope size"));
    }
    Ok(())
}
