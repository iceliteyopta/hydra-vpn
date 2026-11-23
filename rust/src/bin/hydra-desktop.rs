#![cfg_attr(windows, windows_subsystem = "windows")]

#[cfg(windows)]
mod desktop {
    use std::{
        env,
        ffi::{c_void, OsStr, OsString},
        io, mem,
        os::windows::{
            ffi::{OsStrExt, OsStringExt},
            process::CommandExt,
        },
        path::{Path, PathBuf},
        process::{Child, Command, Stdio},
        ptr,
    };

    fn wide(value: &OsStr) -> Vec<u16> {
        value.encode_wide().chain(Some(0)).collect()
    }

    #[repr(C)]
    struct OpenFile {
        size: u32,
        owner: *mut c_void,
        instance: *mut c_void,
        filter: *const u16,
        custom_filter: *mut u16,
        max_custom: u32,
        filter_index: u32,
        file: *mut u16,
        max_file: u32,
        title_file: *mut u16,
        max_title: u32,
        initial_dir: *const u16,
        title: *const u16,
        flags: u32,
        offset: u16,
        extension: u16,
        default_extension: *const u16,
        data: isize,
        hook: *const c_void,
        template: *const u16,
        reserved: *mut c_void,
        reserved_number: u32,
        flags_ex: u32,
    }
    #[link(name = "comdlg32")]
    unsafe extern "system" {
        fn GetOpenFileNameW(file: *mut OpenFile) -> i32;
        fn CommDlgExtendedError() -> u32;
    }
    #[link(name = "user32")]
    unsafe extern "system" {
        fn MessageBoxW(hwnd: *mut c_void, text: *const u16, title: *const u16, flags: u32) -> i32;
    }
    fn message(text: &str, flags: u32) -> i32 {
        unsafe {
            MessageBoxW(
                ptr::null_mut(),
                wide(text.as_ref()).as_ptr(),
                wide("Hydra · Rust".as_ref()).as_ptr(),
                flags,
            )
        }
    }
    fn choose() -> io::Result<Option<PathBuf>> {
        let mut buffer = vec![0u16; 32768];
        let filter = wide("Hydra (*.json, *.txt)\0*.json;*.txt\0".as_ref());
        let title = wide("Выбери JSON sing-box или файл со ссылкой Hysteria2".as_ref());
        let mut dialog: OpenFile = unsafe { mem::zeroed() };
        dialog.size = mem::size_of::<OpenFile>() as u32;
        dialog.file = buffer.as_mut_ptr();
        dialog.max_file = buffer.len() as u32;
        dialog.title = title.as_ptr();
        dialog.filter = filter.as_ptr();
        dialog.flags = 0x1000 | 0x800 | 0x8;
        if unsafe { GetOpenFileNameW(&mut dialog) } == 0 {
            let code = unsafe { CommDlgExtendedError() };
            return if code == 0 {
                Ok(None)
            } else {
                Err(io::Error::other(format!("Ошибка выбора файла {code}")))
            };
        }
        Ok(Some(
            OsString::from_wide(&buffer[..buffer.iter().position(|c| *c == 0).unwrap()]).into(),
        ))
    }

    fn command(core: &Path, config: &Path, action: &str) -> Command {
        let mut cmd = Command::new(core);
        cmd.arg(action)
            .arg("-c")
            .arg(config)
            .current_dir(config.parent().unwrap())
            .creation_flags(0x08000000);
        cmd
    }

    fn spawn_core(
        core: &Path,
        config: &Path,
        action: &str,
        generated: Option<&[u8]>,
    ) -> io::Result<Child> {
        use io::Write;
        let mut cmd = if generated.is_some() {
            let mut cmd = Command::new(core);
            cmd.arg(action)
                .args(["-c", "stdin"])
                .current_dir(config.parent().unwrap())
                .creation_flags(0x08000000);
            cmd
        } else {
            command(core, config, action)
        };
        if action == "check" {
            cmd.stderr(Stdio::piped());
        }
        cmd.stdin(if generated.is_some() {
            Stdio::piped()
        } else {
            Stdio::null()
        });
        let mut child = cmd.spawn()?;
        if let Some(bytes) = generated {
            if let Err(err) = child.stdin.take().unwrap().write_all(bytes) {
                let _ = child.kill();
                let _ = child.wait();
                return Err(err);
            }
        }
        Ok(child)
    }
    #[repr(C)]
    struct Blob {
        size: u32,
        data: *mut u8,
    }
    #[link(name = "crypt32")]
    unsafe extern "system" {
        fn CryptStringToBinaryW(
            text: *const u16,
            len: u32,
            flags: u32,
            data: *mut u8,
            size: *mut u32,
            skip: *mut u32,
            kind: *mut u32,
        ) -> i32;
        fn CryptUnprotectData(
            input: *const Blob,
            desc: *mut *mut u16,
            entropy: *const Blob,
            reserved: *mut c_void,
            prompt: *mut c_void,
            flags: u32,
            output: *mut Blob,
        ) -> i32;
    }
    #[link(name = "kernel32")]
    unsafe extern "system" {
        fn LocalFree(ptr: *mut c_void) -> *mut c_void;
    }
    fn unprotect(encoded: &str) -> Result<String, String> {
        let text = wide(encoded.as_ref());
        let len = u32::try_from(text.len() - 1).map_err(|_| "Invalid protected password")?;
        let mut size = 0;
        unsafe {
            if CryptStringToBinaryW(
                text.as_ptr(),
                len,
                1,
                ptr::null_mut(),
                &mut size,
                ptr::null_mut(),
                ptr::null_mut(),
            ) == 0
            {
                return Err("Invalid protected password".into());
            }
            let mut bytes = vec![0u8; size as usize];
            if CryptStringToBinaryW(
                text.as_ptr(),
                len,
                1,
                bytes.as_mut_ptr(),
                &mut size,
                ptr::null_mut(),
                ptr::null_mut(),
            ) == 0
            {
                return Err("Invalid protected password".into());
            }
            let input = Blob {
                size,
                data: bytes.as_mut_ptr(),
            };
            let mut output = Blob {
                size: 0,
                data: ptr::null_mut(),
            };
            if CryptUnprotectData(
                &input,
                ptr::null_mut(),
                ptr::null(),
                ptr::null_mut(),
                ptr::null_mut(),
                1,
                &mut output,
            ) == 0
            {
                return Err("Windows could not unlock the profile password".into());
            }
            let result = if output.size == 0 {
                Ok(String::new())
            } else {
                let plain = std::slice::from_raw_parts_mut(output.data, output.size as usize);
                let result = String::from_utf8(plain.to_vec())
                    .map_err(|_| "Invalid password encoding".to_string());
                plain.fill(0);
                result
            };
            LocalFree(output.data.cast());
            result
        }
    }
    struct Session(Child);
    impl Drop for Session {
        fn drop(&mut self) {
            let _ = self.0.kill();
            let _ = self.0.wait();
        }
    }

    pub fn run() -> Result<(), Box<dyn std::error::Error>> {
        let exe = env::current_exe()?;
        let core = exe.parent().unwrap().join("cores/sing-box.exe");
        let mut args = env::args_os().skip(1);
        let check_only = args.next().is_some_and(|a| a == "--check");
        let config = if check_only {
            PathBuf::from(args.next().ok_or("Требуется путь к конфигурации")?)
        } else if let Some(path) = choose()? {
            path
        } else {
            return Ok(());
        };
        let config = config.canonicalize()?;
        let generated = if config
            .extension()
            .is_some_and(|e| e.eq_ignore_ascii_case("txt"))
        {
            let text = std::fs::read_to_string(&config)?;
            Some(serde_json::to_vec(&hydra_routing::hysteria::config(
                text.trim_start_matches('\u{feff}'),
            )?)?)
        } else {
            None
        };
        let mut protected = false;
        let generated = if generated.is_none()
            && config
                .extension()
                .is_some_and(|e| e.eq_ignore_ascii_case("json"))
        {
            let raw = std::fs::read_to_string(&config)?;
            let value: serde_json::Value =
                serde_json::from_str(raw.trim_start_matches('\u{feff}'))?;
            if value.get("Tunnels").is_some() {
                let profile: hydra_routing::profile::Profile = serde_json::from_value(value)?;
                protected = profile.kill_switch;
                Some(serde_json::to_vec(
                    &profile.config(config.parent().unwrap(), unprotect)?,
                )?)
            } else {
                None
            }
        } else {
            generated
        };
        let checked =
            spawn_core(&core, &config, "check", generated.as_deref())?.wait_with_output()?;
        if !checked.status.success() {
            return Err(String::from_utf8_lossy(&checked.stderr).into_owned().into());
        }
        if check_only {
            return Ok(());
        }
        if protected {
            return Err("В профиле включена защита брандмауэром. Для подключения этого профиля пока используй основную Hydra.".into());
        }
        if message("Запустить sing-box с выбранной конфигурацией? Защита приложений брандмауэром в этой сборке ещё недоступна.", 4 | 0x20) != 6 { return Ok(()); }
        let mut session = Session(spawn_core(&core, &config, "run", generated.as_deref())?);
        loop {
            if let Some(status) = session.0.try_wait()? {
                return Err(format!("sing-box завершился ({status})").into());
            }
            if message("Процесс sing-box запущен. Отключить его?", 4 | 0x20) == 6
            {
                break;
            }
        }
        Ok(())
    }
    #[cfg(test)]
    mod tests {
        use super::*;
        use std::{
            fs,
            io::{BufRead, BufReader},
            sync::mpsc,
            thread,
            time::Duration,
        };

        #[test]
        fn starts_and_stops_core_without_tun() {
            let core = Path::new(env!("CARGO_MANIFEST_DIR")).join("../cores/sing-box.exe");
            let config = env::temp_dir().join(format!("hydra-smoke-{}.json", std::process::id()));
            fs::write(
                &config,
                r#"{"log":{"level":"info"},"outbounds":[{"type":"direct"}]}"#,
            )
            .unwrap();
            let mut session = Session(
                command(&core, &config, "run")
                    .stderr(Stdio::piped())
                    .spawn()
                    .unwrap(),
            );
            let reader = BufReader::new(session.0.stderr.take().unwrap());
            let (send, recv) = mpsc::channel();
            thread::spawn(move || {
                for line in reader.lines().map_while(Result::ok) {
                    if line.contains("sing-box started") {
                        let _ = send.send(());
                    }
                }
            });
            let started = recv.recv_timeout(Duration::from_secs(8));
            drop(session);
            fs::remove_file(config).unwrap();
            started.unwrap();
        }
    }
    pub fn error(text: &str) {
        message(text, 0x10);
    }
}

#[cfg(windows)]
fn main() {
    if let Err(e) = desktop::run() {
        if std::env::args_os().nth(1).is_none() {
            desktop::error(&e.to_string());
        }
        std::process::exit(1);
    }
}
#[cfg(not(windows))]
fn main() {
    eprintln!("Windows desktop binary");
}
