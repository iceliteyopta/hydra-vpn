use hydra_routing::hysteria::config;
use std::io::{self, Read};
fn main() {
    let mut input = String::new();
    if io::stdin().take(65537).read_to_string(&mut input).is_err() || input.len() > 65536 {
        eprintln!("Invalid input");
        std::process::exit(1);
    }
    match config(&input) {
        Ok(value) => println!("{value}"),
        Err(error) => {
            eprintln!("{error}");
            std::process::exit(1);
        }
    }
}
