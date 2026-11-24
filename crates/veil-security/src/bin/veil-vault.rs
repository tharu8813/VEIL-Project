//! Download vault bridge between the desktop app and the Rust core (ADR-0005).
//!
//! One-shot commands (stdin line 1 is the keyring passphrase):
//!   veil-vault init   <keyring>          create a keyring envelope
//!   veil-vault seal   <keyring> <out>    stdin after the passphrase: framed plaintext
//!   veil-vault verify <keyring> <file>   authenticate an object, print its plaintext length
//!   veil-vault restore <backup> <dir>   restore into a new directory; stdin adds a recovery-code line
//!
//! Long-lived mode, keys stay in this process:
//!   veil-vault serve <vault-dir>
//!   first line (tab separated): `create\t<passphrase>\t<recovery code>` | `unlock\t<passphrase>` |
//!   `recover\t<recovery code>`; then commands, one per line:
//!     seal\t<path>            + framed plaintext          -> ok\t<bytes>
//!     verify\t<path>                                      -> ok\t<bytes>
//!     read\t<path>            (objects up to 1 MiB)       -> ok\t<len>\n<raw bytes>
//!     export\t<object>\t<dest>                            -> ok\t<bytes>
//!     backup\t<dest>\t<backup password>                   -> ok\t<items>
//!     rewrap\t<new passphrase> / rewrap-recovery\t<new code> -> ok
//!     rewrap-both\t<new passphrase>\t<new code>             -> ok
//!     location / rotate-content\t<hex password>\t<new recovery code>
//!     quit
//!   Product startup uses unlock-profile/create-profile and rewrap-profile/recover-profile
//!   with a hex-encoded UTF-8 password field and a recovery code field where required.
//!   Errors are `err\t<reason>` without paths or secrets; the session continues.
//!
//! Frames: `[u32 LE length][bytes]` repeated, then a zero-length frame and the total as u64 LE.
//! A frame length of 0xFFFF_FFFF aborts the object (e.g. the download failed). Objects are
//! published only after a complete, length-checked stream.
#![forbid(unsafe_code)]

use std::fs::File;
use std::io::{self, BufRead, BufReader, Read, Write};
use std::path::{Path, PathBuf};
use std::process::ExitCode;
use veil_security::{Keyring, Purpose, SecretString, vault_backup, vault_credentials, vault_generation};
use zeroize::Zeroizing;

const MAX_FRAME: usize = 1024 * 1024;
const ABORT: u32 = u32::MAX;

fn main() -> ExitCode {
    let args: Vec<String> = std::env::args().skip(1).collect();
    let stdin = io::stdin();
    let mut input = BufReader::new(stdin.lock());
    let result = match args.as_slice() {
        [command, dir] if command == "serve" => serve(Path::new(dir), &mut input).map(|()| String::new()),
        [command, path] if matches!(command.as_str(), "passwords-read" | "passwords-create" | "passwords-replace" | "passwords-rotate") =>
            password_command(command, Path::new(path), &mut input).map(|()| String::new()),
        _ => one_shot(&args, &mut input),
    };
    match result {
        Ok(output) => {
            if !output.is_empty() {
                println!("{output}");
            }
            ExitCode::SUCCESS
        }
        Err(error) => {
            eprintln!("{}", reason(&error));
            ExitCode::FAILURE
        }
    }
}

fn password_command(command: &str, path: &Path, input: &mut impl BufRead) -> io::Result<()> {
    use veil_security::password_store;
    let passphrase = password_line(input)?;
    match command {
        "passwords-read" => {
            let plain = password_store::read(path, passphrase)?;
            let mut out = io::stdout().lock();
            out.write_all(&plain)?;
            out.flush()
        }
        "passwords-create" | "passwords-replace" =>
            password_store::write(path, passphrase, input, command == "passwords-replace"),
        "passwords-rotate" => {
            let new = password_line(input)?;
            if !input.fill_buf()?.is_empty() {
                return Err(io::Error::new(io::ErrorKind::InvalidInput, "Unexpected password rotation payload"));
            }
            password_store::rotate(path, passphrase, new)
        },
        _ => Err(io::Error::new(io::ErrorKind::InvalidInput, "Invalid password store command")),
    }
}

// Bounded hex UTF-8 line. Rotation takes two lines, never command-line secrets.
fn password_line(input: &mut impl BufRead) -> io::Result<SecretString> {
    let mut encoded = Zeroizing::new(String::new());
    if (&mut *input).take(2050).read_line(&mut encoded)? == 0 || !encoded.ends_with('\n') {
        return Err(io::Error::new(io::ErrorKind::InvalidInput, "Invalid password store request"));
    }
    profile_secret(encoded.trim_end_matches(['\r', '\n']))
}

fn one_shot(args: &[String], input: &mut impl BufRead) -> io::Result<String> {
    let passphrase = secret(read_line(input)?)?;
    match args {
        [command, keyring] if command == "init" => {
            Keyring::create(Path::new(keyring), passphrase)?;
            Ok("{\"created\":true}".into())
        }
        [command, keyring, out] if command == "seal" => {
            let keys = Keyring::unlock(Path::new(keyring), passphrase)?;
            let total = seal(&keys, Path::new(out), input)?;
            Ok(format!("{{\"bytes\":{total}}}"))
        }
        [command, keyring, file] if command == "verify" => {
            let keys = Keyring::unlock(Path::new(keyring), passphrase)?;
            Ok(format!("{{\"bytes\":{}}}", keys.verify(Purpose::Download, Path::new(file))?))
        }
        [command, backup, dir] if command == "restore" => {
            vault_backup::restore(Path::new(backup), Path::new(dir), passphrase, secret(read_line(input)?)?)?;
            Ok("{\"restored\":true}".into())
        }
        _ => Err(io::Error::new(io::ErrorKind::InvalidInput, "usage: veil-vault init|seal|verify|restore|serve")),
    }
}

fn serve(dir: &Path, input: &mut impl BufRead) -> io::Result<()> {
    let stdout = io::stdout();
    let mut out = stdout.lock();
    let first = read_line(input)?;
    let (_lock, mut active, mut keys) = open_session(dir, &first).inspect_err(|error| {
        let _ = writeln!(out, "err\t{}", reason(error));
        let _ = out.flush();
    })?;
    drop(first);
    writeln!(out, "ok")?;
    out.flush()?;

    loop {
        let line = match read_line(input) {
            Ok(line) => line,
            Err(error) if error.kind() == io::ErrorKind::UnexpectedEof => return Ok(()),
            Err(error) => return Err(error),
        };
        let parts: Vec<&str> = line.split('\t').collect();
        let reply = match parts.as_slice() {
            ["quit"] => return Ok(()),
            ["location"] => Ok(format!("ok\t{}", generation_name(dir, &active))),
            ["rotate-content", password, code] => (|| {
                let (next, replacement, retired) =
                    vault_generation::rotate(dir, &active, &keys, profile_secret(password)?, secret_str(code)?)?;
                active = next;
                keys = replacement;
                Ok(format!("ok\t{}\t{}", generation_name(dir, &active), if retired { "retired" } else { "retained" }))
            })(),
            ["seal", path] => match seal(&keys, Path::new(path), input) {
                Ok(total) => Ok(format!("ok\t{total}")),
                Err(error) => Err(error),
            },
            ["verify", path] => keys.verify(Purpose::Download, Path::new(path)).map(|n| format!("ok\t{n}")),
            ["export", object, dest] => {
                keys.export_new(Purpose::Download, Path::new(object), Path::new(dest)).map(|n| format!("ok\t{n}"))
            }
            ["backup", dest, password] => secret_str(password)
                .and_then(|secret| vault_backup::export(&keys, &active, Path::new(dest), secret))
                .map(|count| format!("ok\t{count}")),
            [command @ ("read" | "read-preview"), path] => match if *command == "read" {
                keys.read_small(Purpose::Download, Path::new(path))
            } else {
                keys.read_preview(Path::new(path))
            } {
                Ok(bytes) => {
                    writeln!(out, "ok\t{}", bytes.len())?;
                    out.write_all(&bytes)?;
                    out.flush()?;
                    continue;
                }
                Err(error) => Err(error),
            },
            ["rewrap", passphrase] => secret_str(passphrase)
                .and_then(|secret| vault_credentials::replace(&active, &keys, Some(secret), None))
                .map(|()| "ok".into()),
            ["rewrap-recovery", code] => secret_str(code)
                .and_then(|secret| vault_credentials::replace(&active, &keys, None, Some(secret)))
                .map(|()| "ok".into()),
            ["rewrap-both", passphrase, code] => (|| {
                vault_credentials::replace(&active, &keys, Some(secret_str(passphrase)?), Some(secret_str(code)?))?;
                Ok("ok".into())
            })(),
            ["rewrap-profile", password] => (|| {
                vault_credentials::replace(&active, &keys, Some(profile_secret(password)?), None)?;
                Ok("ok".into())
            })(),
            ["recover-profile", password, code] => (|| {
                vault_credentials::replace(&active, &keys, Some(profile_secret(password)?), Some(secret_str(code)?))?;
                Ok("ok".into())
            })(),
            // Only the command word and field count are echoed: arguments may hold secrets.
            _ => Err(io::Error::new(
                io::ErrorKind::InvalidInput,
                format!(
                    "unknown command {:?} with {} fields",
                    parts.first().map(|word| word.chars().take(16).collect::<String>()).unwrap_or_default(),
                    parts.len()
                ),
            )),
        };
        drop(line);
        match reply {
            Ok(text) => writeln!(out, "{text}")?,
            Err(error) => writeln!(out, "err\t{}", reason(&error))?,
        }
        out.flush()?;
    }
}

fn open_session(dir: &Path, first: &str) -> io::Result<(File, PathBuf, Keyring)> {
    if first.starts_with("restore\t") {
        let keys = start(dir, first)?;
        return Ok((vault_generation::lock_session(dir)?, dir.to_path_buf(), keys));
    }
    let lock = vault_generation::lock_session(dir)?;
    let active = vault_generation::active(dir)?;
    if first.starts_with("create\t") || first.starts_with("create-profile\t") {
        vault_generation::require_no_generations(dir)?;
    }
    let keys = start(&active, first)?;
    Ok((lock, active, keys))
}

fn generation_name<'a>(root: &Path, active: &'a Path) -> &'a str {
    if active == root { "root" } else { active.file_name().and_then(|name| name.to_str()).expect("validated generation name") }
}

fn start(dir: &Path, first: &str) -> io::Result<Keyring> {
    match first.split('\t').collect::<Vec<_>>().as_slice() {
        ["create", passphrase, code] => vault_credentials::create(dir, secret_str(passphrase)?, secret_str(code)?),
        ["unlock", passphrase] => vault_credentials::unlock(dir, secret_str(passphrase)?, false),
        ["recover", code] => vault_credentials::unlock(dir, secret_str(code)?, true),
        ["create-profile", password, code] => vault_credentials::create(dir, profile_secret(password)?, secret_str(code)?),
        ["unlock-profile", password] => vault_credentials::unlock(dir, profile_secret(password)?, false),
        ["restore", source, password, code] => vault_backup::restore(Path::new(source), dir, secret_str(password)?, secret_str(code)?),
        _ => Err(io::Error::new(io::ErrorKind::InvalidInput, "bad start command")),
    }
}

/// Seals one framed stream. On any failure the rest of the stream is consumed so a long-lived
/// session stays in sync, and nothing is published.
fn seal(keys: &Keyring, out: &Path, input: &mut impl Read) -> io::Result<u64> {
    let mut frames = Frames::new(input);
    let result = keys.seal_new(Purpose::Download, out, &mut frames);
    if result.is_err() && !frames.ended {
        frames.drain()?;
    }
    result.map(|()| frames.total)
}

fn read_line(input: &mut impl BufRead) -> io::Result<Zeroizing<String>> {
    let mut line = Zeroizing::new(String::new());
    if input.read_line(&mut line)? == 0 {
        return Err(io::Error::new(io::ErrorKind::UnexpectedEof, "input closed"));
    }
    while line.ends_with(['\r', '\n']) {
        line.pop();
    }
    Ok(line)
}

fn secret(line: Zeroizing<String>) -> io::Result<SecretString> {
    secret_str(&line)
}

fn secret_str(value: &str) -> io::Result<SecretString> {
    if value.is_empty() || value.contains(['\t', '\r', '\n']) {
        return Err(io::Error::new(io::ErrorKind::InvalidInput, "A non-empty single-line passphrase is required"));
    }
    Ok(SecretString::from(value.to_owned()))
}

/// Profile passwords use hex on the wire so broker-accepted whitespace cannot become protocol syntax.
fn profile_secret(hex: &str) -> io::Result<SecretString> {
    let invalid = || io::Error::new(io::ErrorKind::InvalidInput, "Invalid profile password encoding");
    if hex.is_empty() || !hex.len().is_multiple_of(2) || hex.len() > 256 * 4 * 2 {
        return Err(invalid());
    }
    let mut bytes = Zeroizing::new(Vec::with_capacity(hex.len() / 2));
    for &[high, low] in hex.as_bytes().as_chunks::<2>().0 {
        let high = (high as char).to_digit(16).ok_or_else(invalid)?;
        let low = (low as char).to_digit(16).ok_or_else(invalid)?;
        bytes.push(((high << 4) | low) as u8);
    }
    let password = std::str::from_utf8(&bytes).map_err(|_| invalid())?;
    Ok(SecretString::from(password.to_owned()))
}

fn reason(error: &io::Error) -> String {
    format!("{:?}: {}", error.kind(), error).replace(['\t', '\r', '\n'], " ")
}

/// Presents the framed stream as plain bytes; fails unless the terminator and total arrive.
struct Frames<'a, R: Read> {
    input: &'a mut R,
    remaining_in_frame: usize,
    total: u64,
    ended: bool,
}

impl<'a, R: Read> Frames<'a, R> {
    fn new(input: &'a mut R) -> Self {
        Self { input, remaining_in_frame: 0, total: 0, ended: false }
    }

    fn truncated() -> io::Error {
        io::Error::new(io::ErrorKind::UnexpectedEof, "Download stream ended before completion")
    }

    /// Discards the rest of the current object stream up to its terminator or abort frame.
    fn drain(&mut self) -> io::Result<()> {
        let mut sink = Zeroizing::new(vec![0u8; 64 * 1024]);
        loop {
            match self.read(&mut sink[..]) {
                Ok(0) => return Ok(()),
                Ok(_) => {}
                Err(_) if self.ended => return Ok(()),
                Err(error) => return Err(error),
            }
        }
    }
}

impl<R: Read> Read for Frames<'_, R> {
    fn read(&mut self, buffer: &mut [u8]) -> io::Result<usize> {
        if self.ended {
            return Ok(0);
        }
        if self.remaining_in_frame == 0 {
            let mut header = [0u8; 4];
            self.input.read_exact(&mut header).map_err(|_| Self::truncated())?;
            let length = u32::from_le_bytes(header);
            if length == ABORT {
                self.ended = true;
                return Err(io::Error::other("Download aborted by the app"));
            }
            if length == 0 {
                let mut total = [0u8; 8];
                self.input.read_exact(&mut total).map_err(|_| Self::truncated())?;
                self.ended = true;
                if u64::from_le_bytes(total) != self.total {
                    return Err(io::Error::new(io::ErrorKind::InvalidData, "Download length mismatch"));
                }
                return Ok(0);
            }
            if length as usize > MAX_FRAME {
                return Err(io::Error::new(io::ErrorKind::InvalidData, "Download frame too large"));
            }
            self.remaining_in_frame = length as usize;
        }
        let want = buffer.len().min(self.remaining_in_frame);
        let read = self.input.read(&mut buffer[..want])?;
        if read == 0 {
            return Err(Self::truncated());
        }
        self.remaining_in_frame -= read;
        self.total += read as u64;
        Ok(read)
    }
}

