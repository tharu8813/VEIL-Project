//! Download vault bridge between the desktop app and the Rust core (ADR-0005).
//!
//! One-shot commands (stdin line 1 is the keyring passphrase):
//!   veil-vault init   <keyring>          create a keyring envelope
//!   veil-vault seal   <keyring> <out>    stdin after the passphrase: framed plaintext
//!   veil-vault verify <keyring> <file>   authenticate an object, print its plaintext length
//!
//! Long-lived mode, keys stay in this process:
//!   veil-vault serve <vault-dir>
//!   first line (tab separated): `create\t<passphrase>\t<recovery code>` | `unlock\t<passphrase>` |
//!   `recover\t<recovery code>`; then commands, one per line:
//!     seal\t<path>            + framed plaintext          -> ok\t<bytes>
//!     verify\t<path>                                      -> ok\t<bytes>
//!     read\t<path>            (objects up to 1 MiB)       -> ok\t<len>\n<raw bytes>
//!     export\t<object>\t<dest>                            -> ok\t<bytes>
//!     rewrap\t<new passphrase> / rewrap-recovery\t<new code> -> ok
//!     quit
//!   Errors are `err\t<reason>` without paths or secrets; the session continues.
//!
//! Frames: `[u32 LE length][bytes]` repeated, then a zero-length frame and the total as u64 LE.
//! A frame length of 0xFFFF_FFFF aborts the object (e.g. the download failed). Objects are
//! published only after a complete, length-checked stream.
#![forbid(unsafe_code)]

use std::io::{self, BufRead, BufReader, Read, Write};
use std::path::Path;
use std::process::ExitCode;
use veil_security::{Keyring, Purpose, SecretString};
use zeroize::Zeroizing;

const MAX_FRAME: usize = 1024 * 1024;
const ABORT: u32 = u32::MAX;
const KEYRING: &str = "keyring.age";
const RECOVERY: &str = "keyring.recovery.age";

fn main() -> ExitCode {
    let args: Vec<String> = std::env::args().skip(1).collect();
    let stdin = io::stdin();
    let mut input = BufReader::new(stdin.lock());
    let result = match args.as_slice() {
        [command, dir] if command == "serve" => serve(Path::new(dir), &mut input).map(|()| String::new()),
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
        _ => Err(io::Error::new(io::ErrorKind::InvalidInput, "usage: veil-vault init|seal|verify|serve")),
    }
}

fn serve(dir: &Path, input: &mut impl BufRead) -> io::Result<()> {
    let stdout = io::stdout();
    let mut out = stdout.lock();
    let first = read_line(input)?;
    let keys = start(dir, &first).inspect_err(|error| {
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
            ["seal", path] => match seal(&keys, Path::new(path), input) {
                Ok(total) => Ok(format!("ok\t{total}")),
                Err(error) => Err(error),
            },
            ["verify", path] => keys.verify(Purpose::Download, Path::new(path)).map(|n| format!("ok\t{n}")),
            ["export", object, dest] => {
                keys.export_new(Purpose::Download, Path::new(object), Path::new(dest)).map(|n| format!("ok\t{n}"))
            }
            ["read", path] => match keys.read_small(Purpose::Download, Path::new(path)) {
                Ok(bytes) => {
                    writeln!(out, "ok\t{}", bytes.len())?;
                    out.write_all(&bytes)?;
                    out.flush()?;
                    continue;
                }
                Err(error) => Err(error),
            },
            ["rewrap", passphrase] => secret_str(passphrase)
                .and_then(|secret| keys.write_envelope(&dir.join(KEYRING), secret, true))
                .map(|()| "ok".into()),
            ["rewrap-recovery", code] => secret_str(code)
                .and_then(|secret| keys.write_envelope(&dir.join(RECOVERY), secret, true))
                .map(|()| "ok".into()),
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

fn start(dir: &Path, first: &str) -> io::Result<Keyring> {
    match first.split('\t').collect::<Vec<_>>().as_slice() {
        ["create", passphrase, code] => {
            let (passphrase, code) = (secret_str(passphrase)?, secret_str(code)?);
            let keys = Keyring::generate();
            keys.write_envelope(&dir.join(KEYRING), passphrase, false)?;
            keys.write_envelope(&dir.join(RECOVERY), code, false)?;
            Ok(keys)
        }
        ["unlock", passphrase] => Keyring::unlock(&dir.join(KEYRING), secret_str(passphrase)?),
        ["recover", code] => Keyring::unlock(&dir.join(RECOVERY), secret_str(code)?),
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
        let mut sink = vec![0u8; 64 * 1024];
        loop {
            match self.read(&mut sink) {
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

