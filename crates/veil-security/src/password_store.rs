//! Separate passphrase-encrypted password store. Never writes decrypted credentials to disk.
use super::*;

const MAGIC: &[u8] = b"VEIL-PASSWORDS-1\n";
pub const LIMIT: usize = 1024 * 1024;

pub fn read(path: &Path, passphrase: SecretString) -> io::Result<Zeroizing<Vec<u8>>> {
    require_passphrase(&passphrase)?;
    let file = File::open(path)?;
    // Limit ciphertext size before parsing, and independently bound the unauthenticated header.
    if file.metadata()?.len() > (LIMIT + 8192) as u64 {
        return Err(invalid("Password store exceeds size limit"));
    }
    let remaining = Cell::new(ENVELOPE_LIMIT);
    let input = HeaderBudget { input: file, remaining: &remaining };
    let decryptor = Decryptor::new_buffered(BufReader::new(input)).map_err(decryption_error)?;
    remaining.set(usize::MAX);
    if !decryptor.is_scrypt() {
        return Err(invalid("Expected a passphrase-encrypted password store"));
    }
    let mut identity = scrypt::Identity::new(passphrase);
    identity.set_max_work_factor(SCRYPT_LOG_N);
    let decrypted = decryptor.decrypt(std::iter::once(&identity as &dyn age::Identity))
        .map_err(decryption_error)?;
    let plain = read_bounded(decrypted, LIMIT + MAGIC.len())?;
    let payload = plain.strip_prefix(MAGIC).ok_or_else(|| invalid("Unsupported password store format"))?;
    Ok(Zeroizing::new(payload.to_vec()))
}

pub fn write(path: &Path, passphrase: SecretString, input: impl Read, replace: bool) -> io::Result<()> {
    require_passphrase(&passphrase)?;
    let plain = read_bounded(input, LIMIT)?;
    if plain.is_empty() {
        return Err(invalid("Empty password store payload"));
    }
    // Replacement authenticates the previous file first; a corrupt or missing file is not reset.
    if replace {
        drop(read(path, SecretString::from(passphrase.expose_secret().to_owned()))?);
    }
    publish(path, passphrase, &plain, replace)
}

/// Authenticate the current envelope before changing its passphrase. Only ciphertext is staged.
pub fn rotate(path: &Path, old: SecretString, new: SecretString) -> io::Result<()> {
    require_passphrase(&new)?;
    let plain = read(path, old)?;
    publish(path, new, &plain, true)
}

fn publish(path: &Path, passphrase: SecretString, plain: &[u8], replace: bool) -> io::Result<()> {
    let write = |staging: &mut File| {
        let mut recipient = scrypt::Recipient::new(passphrase);
        recipient.set_work_factor(SCRYPT_LOG_N);
        let mut encrypted = make_encryptor(&recipient)?.wrap_output(staging)?;
        encrypted.write_all(MAGIC)?;
        encrypted.write_all(plain)?;
        encrypted.finish()?;
        Ok(())
    };
    if replace { publish_replace(path, write) } else { publish_new(path, write) }
}
