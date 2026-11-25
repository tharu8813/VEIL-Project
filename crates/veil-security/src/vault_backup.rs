//! Portable vault snapshot: a password envelope followed by a Backup-key authenticated stream.
use crate::{
    ENVELOPE_LIMIT, Keyring, Purpose, SecretString, invalid, publish_new, transfer,
    vault_credentials,
};
use std::fs::{self, File, OpenOptions};
use std::io::{self, BufReader, Read, Write};
use std::path::Path;

const MAGIC: &[u8; 20] = b"VEIL-VAULT-BACKUP-1\n";
const OBJECT: u8 = 1;
const META: u8 = 2;

pub fn export(
    keys: &Keyring,
    vault: &Path,
    destination: &Path,
    password: SecretString,
) -> io::Result<u64> {
    require_new_destination(destination)?;
    let parent = destination
        .parent()
        .filter(|path| !path.as_os_str().is_empty())
        .unwrap_or_else(|| Path::new("."));
    if fs::canonicalize(parent)?.starts_with(fs::canonicalize(vault)?) {
        return Err(invalid("Save the backup outside the source vault"));
    }
    let mut envelope = Vec::new();
    keys.write_envelope_to(&mut envelope, password)?;
    let mut count = 0;
    publish_new(destination, |file| {
        file.write_all(MAGIC)?;
        file.write_all(&(envelope.len() as u32).to_le_bytes())?;
        file.write_all(&envelope)?;
        keys.write_object(Purpose::Backup, file, |data| {
            for object in fs::read_dir(vault.join("objects"))? {
                let object = object?.path();
                let Some(id) = object.file_stem().and_then(|stem| stem.to_str()) else {
                    continue;
                };
                if object
                    .extension()
                    .is_none_or(|extension| extension != "veil")
                    || !valid_id(id.as_bytes())
                {
                    continue;
                }
                write_record(keys, data, OBJECT, id, &object)?;
                let meta = vault.join("meta").join(format!("{id}.veil"));
                if meta.try_exists()? {
                    write_record(keys, data, META, id, &meta)?;
                }
                count += 1;
            }
            data.write_all(&[0])
        })
    })?;
    Ok(count)
}

/// Authenticates the full snapshot and all stored ciphertext before publishing a new vault.
pub fn restore(
    source: &Path,
    vault: &Path,
    password: SecretString,
    code: SecretString,
) -> io::Result<Keyring> {
    require_new_destination(vault)?;
    let mut source = BufReader::new(File::open(source)?);
    let mut magic = [0; MAGIC.len()];
    source.read_exact(&mut magic)?;
    if &magic != MAGIC {
        return Err(invalid("Unsupported vault backup format"));
    }
    let mut length = [0; 4];
    source.read_exact(&mut length)?;
    let length = u32::from_le_bytes(length) as usize;
    if !(1..=ENVELOPE_LIMIT).contains(&length) {
        return Err(invalid("Invalid vault backup key envelope size"));
    }
    let mut envelope = vec![0; length];
    source.read_exact(&mut envelope)?;
    let keys = Keyring::unlock_envelope(&envelope, password)?;

    let parent = vault
        .parent()
        .filter(|path| !path.as_os_str().is_empty())
        .unwrap_or_else(|| Path::new("."));
    let staging = tempfile::Builder::new()
        .prefix(".veil-restore-")
        .tempdir_in(parent)?;
    for name in ["objects", "meta", "index"] {
        fs::create_dir(staging.path().join(name))?;
    }
    keys.read_object_from(Purpose::Backup, source, |data| {
        read_records(&keys, staging.path(), data)
    })?;
    vault_credentials::write_new(staging.path(), &keys, &envelope, code)?;
    require_new_destination(vault)?;
    fs::rename(staging.path(), vault)?;
    let _ = staging.keep();
    Ok(keys)
}

fn write_record(
    keys: &Keyring,
    output: &mut dyn Write,
    kind: u8,
    id: &str,
    path: &Path,
) -> io::Result<()> {
    if !fs::symlink_metadata(path)?.file_type().is_file() {
        return Err(invalid("Vault backup entry is not a regular file"));
    }
    #[cfg(windows)]
    let mut file = {
        use std::os::windows::fs::OpenOptionsExt;
        // FILE_SHARE_READ: prevent other write/delete handles while copying.
        OpenOptions::new().read(true).share_mode(1).open(path)?
    };
    #[cfg(not(windows))]
    let mut file = File::open(path)?;
    let metadata = file.metadata()?;
    if !metadata.is_file() {
        return Err(invalid("Vault backup entry is not a regular file"));
    }
    let length = metadata.len();
    if kind == META && length > crate::SMALL_OBJECT_LIMIT as u64 + ENVELOPE_LIMIT as u64 {
        return Err(invalid("Vault metadata exceeds size limit"));
    }
    output.write_all(&[kind])?;
    output.write_all(id.as_bytes())?;
    output.write_all(&length.to_le_bytes())?;
    // Authenticate the exact ciphertext bytes streamed into the encrypted backup,
    // rather than reopening the pathname after a separate verification pass.
    {
        let mut record = CopyWhileReading { source: (&mut file).take(length), output };
        keys.read_object_from(Purpose::Download, &mut record, |plaintext| {
            if kind == META {
                crate::read_bounded(plaintext, crate::SMALL_OBJECT_LIMIT)?;
            } else {
                transfer(plaintext, &mut io::sink())?;
            }
            Ok(())
        })?;
        if record.source.limit() != 0 {
            return Err(invalid("Vault changed during backup"));
        }
    }
    if file.read(&mut [0u8; 1])? != 0 {
        return Err(invalid("Vault changed during backup"));
    }
    Ok(())
}

// The outer encrypted backup is published only if every inner record authenticates.
struct CopyWhileReading<'a, R> {
    source: R,
    output: &'a mut dyn Write,
}

impl<R: Read> Read for CopyWhileReading<'_, R> {
    fn read(&mut self, buffer: &mut [u8]) -> io::Result<usize> {
        let count = self.source.read(buffer)?;
        self.output.write_all(&buffer[..count])?;
        Ok(count)
    }
}

fn read_records(keys: &Keyring, staging: &Path, data: &mut dyn Read) -> io::Result<()> {
    let mut kind = [0];
    loop {
        data.read_exact(&mut kind)?;
        if kind[0] == 0 {
            if data.read(&mut kind)? != 0 {
                return Err(invalid("Unexpected data after vault backup terminator"));
            }
            return Ok(());
        }
        let directory = match kind[0] {
            OBJECT => "objects",
            META => "meta",
            _ => return Err(invalid("Unknown vault backup record")),
        };
        let mut id = [0; 32];
        let mut length = [0; 8];
        data.read_exact(&mut id)?;
        data.read_exact(&mut length)?;
        if !valid_id(&id) {
            return Err(invalid("Invalid vault backup object ID"));
        }
        let name = format!(
            "{}.veil",
            std::str::from_utf8(&id).expect("validated ASCII")
        );
        if kind[0] == META && !staging.join("objects").join(&name).is_file() {
            return Err(invalid("Vault backup metadata has no object"));
        }
        let length = u64::from_le_bytes(length);
        if kind[0] == META && length > crate::SMALL_OBJECT_LIMIT as u64 + ENVELOPE_LIMIT as u64 {
            return Err(invalid("Vault backup metadata exceeds size limit"));
        }
        let path = staging.join(directory).join(name);
        let mut file = OpenOptions::new()
            .write(true)
            .create_new(true)
            .open(&path)?;
        if transfer(&mut data.take(length), &mut file)? != length {
            return Err(invalid("Truncated vault backup record"));
        }
        file.sync_all()?;
        drop(file);
        if kind[0] == META {
            keys.read_small(Purpose::Download, &path)?;
        } else {
            keys.verify(Purpose::Download, &path)?;
        }
    }
}

pub(crate) fn valid_id(id: &[u8]) -> bool {
    id.len() == 32
        && id
            .iter()
            .all(|byte| byte.is_ascii_digit() || (b'a'..=b'f').contains(byte))
}

fn require_new_destination(path: &Path) -> io::Result<()> {
    match fs::symlink_metadata(path) {
        Err(error) if error.kind() == io::ErrorKind::NotFound => Ok(()),
        Err(error) => Err(error),
        Ok(_) => Err(io::Error::new(
            io::ErrorKind::AlreadyExists,
            "Vault backup or restore destination already exists",
        )),
    }
}

#[cfg(test)]
mod record_tests {
    use super::*;

    const ID: &str = "0123456789abcdef0123456789abcdef";

    #[test]
    fn backup_record_contains_exact_authenticated_ciphertext() {
        let lab = tempfile::tempdir().unwrap();
        let keys = Keyring::generate();
        let path = lab.path().join("source.veil");
        let payload = vec![0x62; crate::BUFFER_SIZE * 3];
        keys.seal_new(Purpose::Download, &path, &payload[..]).unwrap();
        let ciphertext = fs::read(&path).unwrap();
        let mut record = Vec::new();
        write_record(&keys, &mut record, OBJECT, ID, &path).unwrap();
        assert_eq!(record[0], OBJECT);
        assert_eq!(&record[1..33], ID.as_bytes());
        assert_eq!(u64::from_le_bytes(record[33..41].try_into().unwrap()), ciphertext.len() as u64);
        assert_eq!(&record[41..], ciphertext.as_slice());
        record.push(0);
        let restored = lab.path().join("restored");
        fs::create_dir(&restored).unwrap();
        fs::create_dir(restored.join("objects")).unwrap();
        fs::create_dir(restored.join("meta")).unwrap();
        read_records(&keys, &restored, &mut record.as_slice()).unwrap();
        let object = restored.join("objects").join(format!("{ID}.veil"));
        assert_eq!(fs::read(&object).unwrap(), ciphertext);
        assert_eq!(keys.verify(Purpose::Download, &object).unwrap(), payload.len() as u64);
    }

    #[test]
    fn corrupt_record_never_publishes_backup_staging() {
        let lab = tempfile::tempdir().unwrap();
        let keys = Keyring::generate();
        let path = lab.path().join("source.veil");
        let payload = vec![0x62; crate::BUFFER_SIZE * 3];
        keys.seal_new(Purpose::Download, &path, &payload[..]).unwrap();
        let mut ciphertext = fs::read(&path).unwrap();
        *ciphertext.last_mut().unwrap() ^= 1;
        fs::write(&path, &ciphertext).unwrap();
        let destination = lab.path().join("backup.veil");
        assert!(publish_new(&destination, |file| write_record(&keys, file, OBJECT, ID, &path)).is_err());
        assert!(!destination.exists());
        assert_eq!(fs::read(&path).unwrap(), ciphertext);
        assert_eq!(fs::read_dir(lab.path()).unwrap().count(), 1);
    }

    #[test]
    fn oversized_metadata_cannot_be_backed_up() {
        let lab = tempfile::tempdir().unwrap();
        let keys = Keyring::generate();
        let path = lab.path().join("meta.veil");
        let payload = vec![0x62; crate::SMALL_OBJECT_LIMIT + 1];
        keys.seal_new(Purpose::Download, &path, &payload[..]).unwrap();
        let mut record = Vec::new();
        assert!(write_record(&keys, &mut record, META, ID, &path).is_err());
    }

    #[cfg(windows)]
    #[test]
    fn backup_refuses_an_existing_source_writer() {
        let lab = tempfile::tempdir().unwrap();
        let keys = Keyring::generate();
        let path = lab.path().join("source.veil");
        keys.seal_new(Purpose::Download, &path, &b"synthetic"[..]).unwrap();
        let writer = OpenOptions::new().write(true).open(&path).unwrap();
        let mut record = Vec::new();
        assert!(write_record(&keys, &mut record, OBJECT, ID, &path).is_err());
        assert!(record.is_empty());
        drop(writer);
        write_record(&keys, &mut record, OBJECT, ID, &path).unwrap();
        assert!(!record.is_empty());
    }
}
