//! VEIL's independent developer cryptography experiment.
#![forbid(unsafe_code)]

use age::secrecy::ExposeSecret;
pub use age::secrecy::SecretString;
use age::{Decryptor, Encryptor, scrypt, x25519};
use std::cell::Cell;
use std::fs::File;
use std::io::{self, BufReader, Read, Write};
use std::path::Path;
use zeroize::Zeroizing;

pub mod vault_credentials;
pub mod vault_backup;
pub mod vault_generation;
pub mod password_store;

const VERSION: &str = "VEIL-KEYRING-1";
const SCRYPT_LOG_N: u8 = 17;
const ENVELOPE_LIMIT: usize = 4096;
const SMALL_OBJECT_LIMIT: usize = 1024 * 1024;
const PREVIEW_OBJECT_LIMIT: usize = 8 * 1024 * 1024;
const BUFFER_SIZE: usize = 64 * 1024;
const PART_PREFIX: &str = ".veil-encrypted-";

#[derive(Debug, Clone, Copy)]
pub enum Purpose {
    Profile,
    Download,
    Backup,
}

/// Owns the unlocked identities. Drop after all operations finish.
pub struct Keyring {
    profile: x25519::Identity,
    download: x25519::Identity,
    backup: x25519::Identity,
}

impl Keyring {
    /// Generates distinct keys and publishes a new passphrase-encrypted envelope.
    /// The destination must not exist.
    pub fn create(path: &Path, passphrase: SecretString) -> io::Result<Self> {
        let keys = Self::generate();
        keys.write_envelope(path, passphrase, false)?;
        Ok(keys)
    }

    /// Generates three distinct identities without writing anything.
    pub fn generate() -> Self {
        Self {
            profile: x25519::Identity::generate(),
            download: x25519::Identity::generate(),
            backup: x25519::Identity::generate(),
        }
    }

    fn rotate_content_keys(&self) -> Self {
        Self {
            profile: self.profile.clone(),
            download: x25519::Identity::generate(),
            backup: x25519::Identity::generate(),
        }
    }

    /// Wraps these keys in a passphrase-encrypted envelope. With `replace`, an existing envelope is
    /// swapped atomically (passphrase change, recovery-code reissue); otherwise the path must not exist.
    pub fn write_envelope(&self, path: &Path, passphrase: SecretString, replace: bool) -> io::Result<()> {
        let write = |staging: &mut File| self.write_envelope_to(staging, passphrase);
        if replace { publish_replace(path, write) } else { publish_new(path, write) }
    }

    fn write_envelope_to(&self, output: impl Write, passphrase: SecretString) -> io::Result<()> {
        require_passphrase(&passphrase)?;
        let mut recipient = scrypt::Recipient::new(passphrase);
        recipient.set_work_factor(SCRYPT_LOG_N);
        let encryptor = make_encryptor(&recipient)?;
        let mut encrypted = encryptor.wrap_output(output)?;
        writeln!(encrypted, "{VERSION}")?;
        for identity in [&self.profile, &self.download, &self.backup] {
            writeln!(encrypted, "{}", identity.to_string().expose_secret())?;
        }
        encrypted.finish()?;
        Ok(())
    }

    /// Decrypts an object to a new plaintext file. The file appears at `destination` only after the
    /// whole object authenticated; on any failure nothing is left behind. The destination must not exist.
    pub fn export_new(&self, purpose: Purpose, object: &Path, destination: &Path) -> io::Result<u64> {
        let mut total = 0;
        self.read_object(purpose, object, |plaintext| {
            publish_new_plain(destination, |staging| {
                total = transfer(plaintext, staging)?;
                Ok(())
            })
        })?;
        Ok(total)
    }

    /// Reads a complete, authenticated envelope before returning usable keys.
    pub fn unlock(path: &Path, passphrase: SecretString) -> io::Result<Self> {
        let ciphertext = read_envelope_file(path, ENVELOPE_LIMIT)?;
        Self::unlock_envelope(&ciphertext, passphrase)
    }

    fn unlock_envelope(ciphertext: &[u8], passphrase: SecretString) -> io::Result<Self> {
        require_passphrase(&passphrase)?;
        if ciphertext.len() > ENVELOPE_LIMIT {
            return Err(invalid("Key envelope exceeds size limit"));
        }
        let decryptor = Decryptor::new_buffered(ciphertext).map_err(decryption_error)?;
        if !decryptor.is_scrypt() {
            return Err(invalid("Expected a passphrase-encrypted key envelope"));
        }
        let mut identity = scrypt::Identity::new(passphrase);
        identity.set_max_work_factor(SCRYPT_LOG_N);
        let decrypted = decryptor
            .decrypt(std::iter::once(&identity as &dyn age::Identity))
            .map_err(decryption_error)?;
        let payload = read_bounded(decrypted, ENVELOPE_LIMIT)?;
        let mut lines = std::str::from_utf8(&payload)
            .map_err(|_| invalid("Invalid key envelope payload"))?
            .lines();
        if lines.next() != Some(VERSION) {
            return Err(invalid("Unsupported key envelope version"));
        }
        let keys = Self {
            profile: parse_identity(lines.next())?,
            download: parse_identity(lines.next())?,
            backup: parse_identity(lines.next())?,
        };
        if lines.next().is_some() {
            return Err(invalid("Invalid key envelope payload"));
        }
        Ok(keys)
    }

    /// Streams plaintext from the supplied reader to a new ciphertext file.
    /// The destination must not exist.
    pub fn seal_new(
        &self,
        purpose: Purpose,
        path: &Path,
        mut plaintext: impl Read,
    ) -> io::Result<()> {
        publish_new(path, |staging| {
            self.write_object(purpose, staging, |encrypted| {
                transfer(&mut plaintext, encrypted)?;
                Ok(())
            })
        })
    }

    fn write_object(&self, purpose: Purpose, output: impl Write,
        write: impl FnOnce(&mut dyn Write) -> io::Result<()>) -> io::Result<()> {
        let recipient = self.identity(purpose).to_public();
        let mut encrypted = make_encryptor(&recipient)?.wrap_output(output)?;
        write(&mut encrypted)?;
        encrypted.finish()?;
        Ok(())
    }

    /// Authenticates every chunk and the end of the object without exporting plaintext.
    pub fn verify(&self, purpose: Purpose, path: &Path) -> io::Result<u64> {
        self.read_object(purpose, path, |plaintext| {
            transfer(plaintext, &mut io::sink())
        })
    }

    /// Returns up to 1 MiB only after authenticating the complete object.
    pub fn read_small(&self, purpose: Purpose, path: &Path) -> io::Result<Zeroizing<Vec<u8>>> {
        self.read_object(purpose, path, |plaintext| {
            read_bounded(plaintext, SMALL_OBJECT_LIMIT)
        })
    }

    /// Authenticates the complete download object within a separate, fixed preview budget.
    /// Does not expand the metadata/keyring small-object allowance.
    pub fn read_preview(&self, path: &Path) -> io::Result<Zeroizing<Vec<u8>>> {
        self.read_object(Purpose::Download, path, |plaintext| {
            read_bounded(plaintext, PREVIEW_OBJECT_LIMIT)
        })
    }

    fn read_object<T>(
        &self,
        purpose: Purpose,
        path: &Path,
        read: impl FnOnce(&mut dyn Read) -> io::Result<T>,
    ) -> io::Result<T> {
        self.read_object_from(purpose, File::open(path)?, read)
    }

    fn read_object_from<T>(
        &self,
        purpose: Purpose,
        input: impl Read,
        read: impl FnOnce(&mut dyn Read) -> io::Result<T>,
    ) -> io::Result<T> {
        // Bound parsing before authentication, including incomplete or malformed headers.
        let remaining = Cell::new(ENVELOPE_LIMIT);
        let input = HeaderBudget {
            input,
            remaining: &remaining,
        };
        let decryptor = Decryptor::new_buffered(BufReader::new(input)).map_err(decryption_error)?;
        remaining.set(usize::MAX);
        let mut decrypted = decryptor
            .decrypt(std::iter::once(self.identity(purpose) as &dyn age::Identity))
            .map_err(decryption_error)?;
        read(&mut decrypted)
    }

    fn identity(&self, purpose: Purpose) -> &x25519::Identity {
        match purpose {
            Purpose::Profile => &self.profile,
            Purpose::Download => &self.download,
            Purpose::Backup => &self.backup,
        }
    }
}

struct HeaderBudget<'a, R> {
    input: R,
    remaining: &'a Cell<usize>,
}

impl<R: Read> Read for HeaderBudget<'_, R> {
    fn read(&mut self, buffer: &mut [u8]) -> io::Result<usize> {
        let limit = self.remaining.get().min(buffer.len());
        let bytes = self.input.read(&mut buffer[..limit])?;
        self.remaining.set(self.remaining.get() - bytes);
        Ok(bytes)
    }
}

fn require_passphrase(passphrase: &SecretString) -> io::Result<()> {
    if passphrase.expose_secret().is_empty() {
        return Err(io::Error::new(
            io::ErrorKind::InvalidInput,
            "A non-empty passphrase is required",
        ));
    }
    Ok(())
}

fn parse_identity(line: Option<&str>) -> io::Result<x25519::Identity> {
    line.and_then(|identity| identity.parse().ok())
        .ok_or_else(|| invalid("Invalid key envelope identity"))
}

fn read_bounded(reader: impl Read, limit: usize) -> io::Result<Zeroizing<Vec<u8>>> {
    let mut bytes = Zeroizing::new(Vec::with_capacity(limit + 1));
    reader.take((limit + 1) as u64).read_to_end(&mut bytes)?;
    if bytes.len() > limit {
        return Err(invalid("Decrypted data exceeds size limit"));
    }
    Ok(bytes)
}

fn transfer(reader: &mut (impl Read + ?Sized), writer: &mut (impl Write + ?Sized)) -> io::Result<u64> {
    let mut buffer = Zeroizing::new([0u8; BUFFER_SIZE]);
    let mut total = 0u64;
    loop {
        match reader.read(&mut buffer[..]) {
            Ok(0) => return Ok(total),
            Ok(bytes) => {
                writer.write_all(&buffer[..bytes])?;
                total += bytes as u64;
            }
            Err(error) if error.kind() == io::ErrorKind::Interrupted => continue,
            Err(error) => return Err(error),
        }
    }
}

fn make_encryptor(recipient: &dyn age::Recipient) -> io::Result<Encryptor> {
    Encryptor::with_recipients(std::iter::once(recipient))
        .map_err(|_| io::Error::other("Cannot initialize age encryption"))
}

fn read_envelope_file(path: &Path, limit: usize) -> io::Result<Vec<u8>> {
    let mut bytes = Vec::with_capacity(limit + 1);
    File::open(path)?.take((limit + 1) as u64).read_to_end(&mut bytes)?;
    if bytes.len() > limit {
        return Err(invalid("Key credential file exceeds size limit"));
    }
    Ok(bytes)
}

fn publish_new(path: &Path, write: impl FnOnce(&mut File) -> io::Result<()>) -> io::Result<()> {
    let parent = path
        .parent()
        .filter(|path| !path.as_os_str().is_empty())
        .unwrap_or_else(|| Path::new("."));
    let mut staging = tempfile::Builder::new()
        .prefix(PART_PREFIX)
        .tempfile_in(parent)?;
    write(staging.as_file_mut())?;
    staging.as_file().sync_all()?;
    staging
        .persist_noclobber(path)
        .map_err(|error| error.error)?;
    Ok(())
}

fn publish_replace(path: &Path, write: impl FnOnce(&mut File) -> io::Result<()>) -> io::Result<()> {
    let parent = path
        .parent()
        .filter(|path| !path.as_os_str().is_empty())
        .unwrap_or_else(|| Path::new("."));
    let mut staging = tempfile::Builder::new()
        .prefix(PART_PREFIX)
        .tempfile_in(parent)?;
    write(staging.as_file_mut())?;
    staging.as_file().sync_all()?;
    staging.persist(path).map_err(|error| error.error)?;
    Ok(())
}

/// Like `publish_new`, but for exported plaintext: the staging name does not claim to be ciphertext.
fn publish_new_plain(path: &Path, write: impl FnOnce(&mut File) -> io::Result<()>) -> io::Result<()> {
    let parent = path
        .parent()
        .filter(|path| !path.as_os_str().is_empty())
        .unwrap_or_else(|| Path::new("."));
    let mut staging = tempfile::Builder::new()
        .prefix(".veil-export-")
        .tempfile_in(parent)?;
    write(staging.as_file_mut())?;
    staging.as_file().sync_all()?;
    staging
        .persist_noclobber(path)
        .map_err(|error| error.error)?;
    Ok(())
}

fn invalid(message: &'static str) -> io::Error {
    io::Error::new(io::ErrorKind::InvalidData, message)
}

fn decryption_error(_: age::DecryptError) -> io::Error {
    invalid("Cannot authenticate or decode age data")
}
