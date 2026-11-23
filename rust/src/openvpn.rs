use serde_json::{json, Value};
use std::collections::HashMap;

/// Convert a portable OpenVPN profile. Resolve external certificate files at the
/// caller, so mobile document imports and Windows directory imports can differ.
pub fn endpoint(
    profile: &str,
    tag: &str,
    credentials: Option<(&str, &str)>,
    read_file: impl Fn(&str) -> Result<String, String>,
) -> Result<Value, String> {
    let mut blocks = HashMap::new();
    let mut directives = Vec::new();
    let mut lines = profile.lines();
    while let Some(line) = lines.next() {
        let line = line.trim_start_matches('\u{feff}').trim();
        if line.is_empty() || line.starts_with(['#', ';']) {
            continue;
        }
        if let Some(key) = line.strip_prefix('<').and_then(|s| s.strip_suffix('>')) {
            let closing = format!("</{key}>");
            let mut data = String::new();
            loop {
                let line = lines.next().ok_or("Unclosed OpenVPN block")?;
                if line.trim() == closing {
                    break;
                }
                data.push_str(line);
                data.push('\n');
            }
            blocks.insert(key.to_ascii_lowercase(), data);
        } else {
            directives.push(line);
        }
    }
    let mut ep = json!({"type":"openvpn-client","tag":tag});
    let mut tls = json!({});
    let mut remotes = Vec::new();
    let mut default_port = 1194u16;
    let mut network = "udp";
    let mut direction = None;
    let mut auth = false;
    let mut keys = HashMap::new();
    for line in directives {
        let mut fields = line.split_whitespace();
        let cmd = fields.next().unwrap().to_ascii_lowercase();
        let arg = fields.next().unwrap_or("");
        let val = json!(arg);
        match cmd.as_str() {
            "remote" => {
                if arg.is_empty() {
                    return Err("Missing OpenVPN remote".into());
                }
                let port = fields.next().map(parse_port).transpose()?;
                let proto = fields.next().map(net).transpose()?;
                remotes.push((arg, port, proto));
            }
            "proto" => network = net(arg)?,
            "port" | "rport" => default_port = parse_port(arg)?,
            "remote-random" => ep["remote_random"] = json!(true),
            "dev" if arg.starts_with("tap") => {
                return Err("TAP requires an external OpenVPN client".into())
            }
            "pkcs12" => return Err("PKCS#12 requires an external OpenVPN client".into()),
            "ca" | "cert" | "key" | "tls-auth" | "tls-crypt" | "tls-crypt-v2" | "secret" => {
                let rest = line[cmd.len()..].trim();
                let (path, tail) = if let Some(rest) = rest.strip_prefix('"') {
                    rest.split_once('"').ok_or("Unclosed quoted OpenVPN path")?
                } else {
                    let end = rest.find(char::is_whitespace).unwrap_or(rest.len());
                    (&rest[..end], &rest[end..])
                };
                if !path.is_empty() && path != "[inline]" {
                    keys.insert(cmd.clone(), read_file(path)?);
                }
                if matches!(cmd.as_str(), "tls-auth" | "secret") && !tail.trim().is_empty() {
                    direction = tail.split_whitespace().next();
                }
            }
            "key-direction" => direction = Some(arg),
            "verify-x509-name" => {
                tls["server_name"] = val;
                if let Some(mode) = fields.next() {
                    tls["server_name_type"] = json!(mode);
                }
            }
            "remote-cert-tls" => tls["remote_certificate_tls"] = val,
            "tls-version-min" => tls["version_min"] = val,
            "tls-cipher" => tls["cipher"] = val,
            "cipher" => ep["cipher"] = val,
            "data-ciphers" | "ncp-ciphers" => {
                ep["data_ciphers"] = json!(arg.split(':').collect::<Vec<_>>())
            }
            "data-ciphers-fallback" => ep["data_ciphers_fallback"] = val,
            "auth" => ep["auth"] = val,
            "auth-user-pass" => auth = true,
            "comp-lzo" => {
                ep["compression_lzo"] = json!(if arg.is_empty() { "adaptive" } else { arg })
            }
            "compress" => ep["compression"] = json!(if arg.is_empty() { "stub" } else { arg }),
            "allow-compression" => ep["allow_compression"] = val,
            "tun-mtu" | "fragment" | "mssfix" => {
                if !arg.is_empty() {
                    let key = match cmd.as_str() {
                        "tun-mtu" => "mtu",
                        "mssfix" => "mss_fix",
                        _ => "fragment",
                    };
                    ep[key] = json!(arg.parse::<u32>().map_err(|_| "Invalid OpenVPN size")?);
                }
            }
            _ => {}
        }
    }
    if blocks.contains_key("pkcs12") {
        return Err("Unsupported PKCS#12 block".into());
    }
    for (key, data) in blocks {
        keys.entry(key).or_insert(data);
    }
    for (src, dst) in [
        ("ca", "certificate"),
        ("cert", "client_certificate"),
        ("key", "client_key"),
    ] {
        if let Some(value) = keys.remove(src) {
            tls[dst] = json!(value);
        }
    }
    for (src, kind) in [
        ("tls-crypt-v2", "tls_crypt_v2"),
        ("tls-crypt", "tls_crypt"),
        ("tls-auth", "tls_auth"),
    ] {
        if let Some(value) = keys.remove(src) {
            let mut wrap = json!({"type":kind,"key":value});
            if kind == "tls_auth" {
                if let Some(d) = direction {
                    wrap["direction"] = json!(key_direction(d)?);
                }
            }
            tls["control_wrap"] = wrap;
            break;
        }
    }
    if let Some(value) = keys.remove("secret") {
        ep["mode"] = json!("static_key");
        ep["static_key"] = json!(value);
        if let Some(d) = direction {
            ep["key_direction"] = json!(key_direction(d)?);
        }
    } else {
        if let Some(cipher) = ep.as_object_mut().unwrap().remove("cipher") {
            ep.as_object_mut()
                .unwrap()
                .entry("data_ciphers_fallback")
                .or_insert(cipher);
        }
        if !tls.as_object().unwrap().is_empty() {
            ep["tls"] = tls;
        }
    }
    if remotes.is_empty() {
        return Err("Missing OpenVPN remote".into());
    }
    ep["servers"] = json!(remotes
        .into_iter()
        .map(|(host, port, proto)| {
            let mut remote = json!({"server":host,"server_port":port.unwrap_or(default_port)});
            if let Some(proto) = proto {
                remote["network"] = json!(proto);
            }
            remote
        })
        .collect::<Vec<_>>());
    ep["network"] = json!(network);
    if let Some((user, password)) = credentials {
        ep["username"] = json!(user);
        ep["password"] = json!(password);
    } else if auth {
        return Err("OpenVPN requires credentials".into());
    }
    ep["pull_filters"] = json!([
        "redirect-gateway",
        "redirect-private",
        "block-outside-dns",
        "block-ipv6"
    ]
    .map(|text| json!({"action":"ignore","text":text})));
    Ok(ep)
}

fn key_direction(s: &str) -> Result<&'static str, String> {
    match s {
        "0" => Ok("server"),
        "1" => Ok("client"),
        _ => Err("Invalid OpenVPN key direction".into()),
    }
}
fn net(s: &str) -> Result<&'static str, String> {
    if s.starts_with("tcp") {
        Ok("tcp")
    } else if s.starts_with("udp") {
        Ok("udp")
    } else {
        Err("Invalid OpenVPN transport".into())
    }
}
fn parse_port(s: &str) -> Result<u16, String> {
    s.parse::<u16>()
        .ok()
        .filter(|p| *p > 0)
        .ok_or_else(|| "Invalid OpenVPN port".into())
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn preserves_tls_and_server_defaults() {
        let ep=endpoint("remote vpn.example\nport 443\nproto tcp-client\nca \"test ca.crt\"\n<tls-auth>\nKEY\n</tls-auth>\nkey-direction 1\ncipher AES-256-CBC","Work",None,|p| {assert_eq!(p,"test ca.crt");Ok("CA".into())}).unwrap();
        assert_eq!(ep["servers"][0]["server_port"], 443);
        assert_eq!(ep["tls"]["certificate"], "CA");
        assert_eq!(ep["tls"]["control_wrap"]["direction"], "client");
        assert_eq!(ep["data_ciphers_fallback"], "AES-256-CBC");
    }
}
