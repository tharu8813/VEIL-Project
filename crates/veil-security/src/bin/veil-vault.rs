//! Phase 4-0 download vault bridge. The desktop app streams download bytes into this process;
//! plaintext never touches disk. Commands (stdin line 1 is always the keyring passphrase):
//!
//!   veil-vault init   <keyring>          create a new keyring envelope
//!   veil-vault seal   <keyring> <out>    stdin after the passphrase: framed plaintext
//!   veil-vault verify <keyring> <file>   authenticate an object, print its plaintext length
//!
//! Frame format for `seal`: repeated `[u32 LE length][bytes]` frames, then a zero-length frame
//! followed by the total plaintext length as u64 LE. A stream that ends before the terminator, or
//! whose total does not match, is an interrupted download: the object is not published.
#![forbid(unsafe_code)]

use std::io::{self, BufRead, Read};
use std::path::PathBuf;
use std::process::ExitCode;
use veil_security::{Keyring, Purpose, SecretString};
use zeroize::Zeroizing;

const MAX_FRAME: usize = 1024 * 1024;

fn main() -> ExitCode {
    match run() {
        Ok(output) => {
            println!("{output}");
            ExitCode::SUCCESS
        }
        Err(error) => {
            // Errors never include plaintext or the passphrase.
            eprintln!("{{\"error\":\"{:?}\",\"message\":\"{}\"}}", error.kind(), error.to_string().replace('"', "'"));
            ExitCode::FAILURE
        }
    }
}

fn run() -> io::Result<String> {
    let args: Vec<String> = std::env::args().skip(1).collect();
    let stdin = io::stdin();
    let mut input = stdin.lock();
    let passphrase = read_passphrase(&mut input)?;
    match args.as_slice() {
        [command, keyring] if command == "init" => {
            Keyring::create(&PathBuf::from(keyring), passphrase)?;
            Ok("{\"created\":true}".into())
        }
        [command, keyring, out] if command == "seal" => {
            let keys = Keyring::unlock(&PathBuf::from(keyring), passphrase)?;
            let mut frames = Frames::new(&mut input);
            keys.seal_new(Purpose::Download, &PathBuf::from(out), &mut frames)?;
            Ok(format!("{{\"bytes\":{}}}", frames.total))
        }
        [command, keyring, file] if command == "verify" => {
            let keys = Keyring::unlock(&PathBuf::from(keyring), passphrase)?;
            let bytes = keys.verify(Purpose::Download, &PathBuf::from(file))?;
            Ok(format!("{{\"bytes\":{bytes}}}"))
        }
        _ => Err(io::Error::new(io::ErrorKind::InvalidInput, "usage: veil-vault init|seal|verify <keyring> [path]")),
    }
}

fn read_passphrase(input: &mut impl BufRead) -> io::Result<SecretString> {
    let mut line = Zeroizing::new(String::new());
    input.read_line(&mut line)?;
    let trimmed = line.trim_end_matches(['\r', '\n']);
    if trimmed.is_empty() {
        return Err(io::Error::new(io::ErrorKind::InvalidInput, "A non-empty passphrase is required"));
    }
    Ok(SecretString::from(trimmed.to_owned()))
}

/// Presents the framed stream as plain bytes and fails unless the terminator and total arrive.
struct Frames<'a, R: Read> {
    input: &'a mut R,
    remaining_in_frame: usize,
    total: u64,
    finished: bool,
}

impl<'a, R: Read> Frames<'a, R> {
    fn new(input: &'a mut R) -> Self {
        Self { input, remaining_in_frame: 0, total: 0, finished: false }
    }

    fn truncated() -> io::Error {
        io::Error::new(io::ErrorKind::UnexpectedEof, "Download stream ended before completion")
    }
}

impl<R: Read> Read for Frames<'_, R> {
    fn read(&mut self, buffer: &mut [u8]) -> io::Result<usize> {
        if self.finished {
            return Ok(0);
        }
        if self.remaining_in_frame == 0 {
            let mut header = [0u8; 4];
            self.input.read_exact(&mut header).map_err(|_| Self::truncated())?;
            let length = u32::from_le_bytes(header) as usize;
            if length == 0 {
                let mut total = [0u8; 8];
                self.input.read_exact(&mut total).map_err(|_| Self::truncated())?;
                if u64::from_le_bytes(total) != self.total {
                    return Err(io::Error::new(io::ErrorKind::InvalidData, "Download length mismatch"));
                }
                let mut extra = [0u8; 1];
                if self.input.read(&mut extra)? != 0 {
                    return Err(io::Error::new(io::ErrorKind::InvalidData, "Data after download terminator"));
                }
                self.finished = true;
                return Ok(0);
            }
            if length > MAX_FRAME {
                return Err(io::Error::new(io::ErrorKind::InvalidData, "Download frame too large"));
            }
            self.remaining_in_frame = length;
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

