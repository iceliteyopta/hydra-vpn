use hydra_routing::{compile, Platform, Rule};
use serde::Deserialize;
use std::{env, fs, process::ExitCode};

#[derive(Deserialize)]
#[serde(rename_all = "PascalCase")]
struct Profile {
    tunnels: Vec<Tunnel>,
    rules: Vec<Rule>,
}

#[derive(Deserialize)]
#[serde(rename_all = "PascalCase")]
struct Tunnel {
    name: String,
    #[serde(default = "enabled")]
    enabled: bool,
}
fn enabled() -> bool {
    true
}

fn run() -> Result<(), String> {
    let args: Vec<_> = env::args().skip(1).collect();
    if args.len() != 2 {
        return Err("Usage: hydra-routing <windows|android> <profile.json>".into());
    }
    let platform = match args[0].as_str() {
        "windows" => Platform::Windows,
        "android" => Platform::Android,
        _ => return Err("Expected windows or android".into()),
    };
    let data = fs::read_to_string(&args[1]).map_err(|e| e.to_string())?;
    let profile: Profile =
        serde_json::from_str(data.trim_start_matches('\u{feff}')).map_err(|e| e.to_string())?;
    let tags: Vec<_> = profile
        .tunnels
        .into_iter()
        .filter(|t| t.enabled)
        .map(|t| t.name)
        .collect();
    let rules = compile(&profile.rules, &tags, platform)?;
    println!(
        "{}",
        serde_json::to_string_pretty(&rules).map_err(|e| e.to_string())?
    );
    Ok(())
}

fn main() -> ExitCode {
    match run() {
        Ok(()) => ExitCode::SUCCESS,
        Err(error) => {
            eprintln!("{error}");
            ExitCode::FAILURE
        }
    }
}
