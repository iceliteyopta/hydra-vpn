use serde::Deserialize;
use serde_json::{json, Value};
use std::net::IpAddr;

#[derive(Deserialize)]
#[serde(rename_all = "PascalCase")]
pub struct Rule {
    #[serde(default = "enabled")]
    pub enabled: bool,
    pub kind: String,
    pub values: String,
    pub target: String,
}

fn enabled() -> bool {
    true
}

#[derive(Clone, Copy, PartialEq)]
pub enum Platform {
    Windows,
    Android,
}

/// Compile explicit profile rules in order. Callers supply enabled outbound tags.
pub fn compile(rules: &[Rule], tags: &[String], platform: Platform) -> Result<Vec<Value>, String> {
    let mut result = Vec::new();
    for rule in rules.iter().filter(|rule| rule.enabled) {
        if !matches!(rule.target.as_str(), "direct" | "block") && !tags.contains(&rule.target) {
            return Err(format!("Unknown tunnel: {}", rule.target));
        }
        let mut matcher = json!({});
        let kind = rule.kind.as_str();
        let mut values = Vec::new();
        for raw in rule.values.split(|c: char| {
            matches!(c, ',' | ';' | '\n' | '\r')
                || (!matches!(kind, "process_name" | "process_folder") && c.is_ascii_whitespace())
        }) {
            let raw = raw.trim();
            if raw.is_empty() {
                continue;
            }
            values.push(match kind {
                "domain" | "domain_suffix" | "domain_keyword" => {
                    let host = raw.split_once("://").map_or(raw, |(_, host)| host);
                    let host = host
                        .split('/')
                        .next()
                        .unwrap_or("")
                        .trim_start_matches("*.")
                        .trim_start_matches('.');
                    if host.is_empty() {
                        return Err("Empty domain".into());
                    }
                    json!(idna::domain_to_ascii(host)
                        .map_err(|_| format!("Invalid domain: {host}"))?
                        .to_lowercase())
                }
                "ip_cidr" => {
                    let (ip, prefix) = raw
                        .split_once('/')
                        .map_or((raw, None), |(ip, prefix)| (ip, Some(prefix)));
                    let ip: IpAddr = ip.parse().map_err(|_| format!("Invalid address: {raw}"))?;
                    let bits = if ip.is_ipv4() { 32 } else { 128 };
                    let prefix = match prefix {
                        Some(p) => p
                            .parse::<u8>()
                            .ok()
                            .filter(|p| *p <= bits)
                            .ok_or_else(|| format!("Invalid prefix: {raw}"))?,
                        None => bits,
                    };
                    json!(format!("{ip}/{prefix}"))
                }
                "port" => json!(raw
                    .parse::<u16>()
                    .ok()
                    .filter(|p| *p > 0)
                    .ok_or_else(|| format!("Invalid port: {raw}"))?),
                "process_name" if platform == Platform::Windows => {
                    json!(if raw.to_ascii_lowercase().ends_with(".exe") {
                        raw.to_owned()
                    } else {
                        format!("{raw}.exe")
                    })
                }
                "geosite" | "geoip" => json!(format!("{kind}-{}", raw.to_lowercase())),
                "process_folder" if platform == Platform::Windows => {
                    let mut folder = raw.trim_matches('"').to_owned();
                    for (key, value) in std::env::vars() {
                        let needle = format!("%{key}%");
                        if let Some(index) = folder
                            .to_ascii_lowercase()
                            .find(&needle.to_ascii_lowercase())
                        {
                            folder.replace_range(index..index + needle.len(), &value);
                        }
                    }
                    let mut regex = String::from("(?i)^");
                    for ch in folder.trim_end_matches(['/', '\\']).chars() {
                        if r"\.+*?()|[]{}^$".contains(ch) {
                            regex.push('\\');
                        }
                        regex.push(ch);
                    }
                    regex.push_str(r"\\");
                    json!(regex)
                }
                "package_name" if platform == Platform::Android => json!(raw),
                _ => return Err(format!("Unsupported rule: {kind}")),
            });
        }
        if values.is_empty() {
            continue;
        }
        let field = match kind {
            "geosite" | "geoip" => "rule_set",
            "process_folder" => "process_path_regex",
            _ => kind,
        };
        matcher[field] = json!(values);
        if rule.target == "block" {
            matcher["action"] = json!("reject");
        } else {
            matcher["action"] = json!("route");
            matcher["outbound"] = json!(rule.target);
        }
        result.push(matcher);
    }
    Ok(result)
}
#[cfg(test)]
mod tests {
    use super::*;

    fn rule(kind: &str, values: &str, target: &str) -> Rule {
        Rule {
            enabled: true,
            kind: kind.into(),
            values: values.into(),
            target: target.into(),
        }
    }

    #[test]
    fn preserves_rule_order_and_target() {
        let rules = [
            rule("domain_suffix", "*.РФ", "direct"),
            rule("ip_cidr", "10.0.0.1", "Work"),
        ];
        let compiled = compile(&rules, &["Work".into()], Platform::Windows).unwrap();
        assert_eq!(compiled[0]["domain_suffix"], json!(["xn--p1ai"]));
        assert_eq!(compiled[1]["ip_cidr"], json!(["10.0.0.1/32"]));
        assert_eq!(compiled[1]["outbound"], "Work");
    }

    #[test]
    fn distinguishes_android_packages_from_windows_processes() {
        let rules = [rule("package_name", "org.example.app", "block")];
        assert!(compile(&rules, &[], Platform::Windows).is_err());
        let compiled = compile(&rules, &[], Platform::Android).unwrap();
        assert_eq!(compiled[0]["action"], "reject");
    }

    #[test]
    fn rejects_missing_tunnel_and_invalid_port() {
        assert!(compile(
            &[rule("domain", "example.com", "Missing")],
            &[],
            Platform::Android
        )
        .is_err());
        assert!(compile(&[rule("port", "65536", "direct")], &[], Platform::Windows).is_err());
    }
}
pub mod hysteria;
pub mod openvpn;
pub mod profile;
