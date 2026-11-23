use crate::{compile, hysteria, openvpn, Platform, Rule};
use serde::Deserialize;
use serde_json::{json, Value};
use std::{
    collections::{BTreeSet, HashMap},
    fs,
    path::{Component, Path},
};

#[derive(Deserialize)]
#[serde(rename_all = "PascalCase")]
struct Tunnel {
    name: String,
    #[serde(default = "yes")]
    enabled: bool,
    #[serde(rename = "Type")]
    kind: String,
    link: Option<String>,
    id: Option<String>,
    config_file: Option<String>,
    ovpn_driver: Option<String>,
    ovpn_user: Option<String>,
    ovpn_pass_protected: Option<String>,
    dns_server: Option<String>,
}
fn yes() -> bool {
    true
}

#[derive(Deserialize)]
#[serde(rename_all = "PascalCase")]
pub struct Profile {
    tunnels: Vec<Tunnel>,
    rules: Vec<Rule>,
    #[serde(rename = "Final")]
    final_tag: String,
    default_dns: Option<String>,
    rule_set_detour: Option<String>,
    #[serde(default)]
    strict_route: bool,
    #[serde(default)]
    pub kill_switch: bool,
    kill_tunnel: Option<String>,
    kill_apps: Option<Vec<String>>,
    kill_domains: Option<String>,
}

fn part(value: Option<&str>) -> Result<&str, String> {
    let value = value.ok_or("Missing tunnel path")?;
    let mut parts = Path::new(value).components();
    if !matches!(parts.next(), Some(Component::Normal(_))) || parts.next().is_some() {
        return Err("Invalid tunnel path".into());
    }
    Ok(value)
}

fn dns_server(spec: &str, tag: &str) -> Result<Value, String> {
    if spec == "local" {
        return Ok(json!({"type":"local","tag":tag}));
    }
    let value = if spec.contains("://") {
        spec.to_owned()
    } else {
        format!("udp://{spec}")
    };
    let uri = url::Url::parse(&value).map_err(|_| "Invalid DNS server")?;
    let host = uri
        .host_str()
        .ok_or("Missing DNS host")?
        .trim_matches(['[', ']']);
    let mut server = json!({"tag":tag,"type":uri.scheme(),"server":host});
    if let Some(port) = uri.port() {
        server["server_port"] = json!(port);
    }
    if uri.scheme() == "https" && !matches!(uri.path(), "" | "/" | "/dns-query") {
        server["path"] = json!(uri.path());
    }
    Ok(server)
}

impl Profile {
    pub fn config(
        &self,
        dir: &Path,
        unprotect: impl Fn(&str) -> Result<String, String>,
    ) -> Result<Value, String> {
        let mut outbounds = vec![json!({"type":"direct","tag":"direct"})];
        let mut endpoints = Vec::new();
        let mut dns = vec![dns_server("local", "dns-local")?];
        let mut dns_rules = Vec::new();
        let mut routes = vec![
            json!({"action":"sniff"}),
            json!({"protocol":"dns","action":"hijack-dns"}),
            json!({"process_name":["sing-box.exe"],"outbound":"direct"}),
        ];
        let mut tags = Vec::new();
        let mut ovpn = Vec::new();
        let mut dns_specs = HashMap::new();
        for t in self.tunnels.iter().filter(|t| t.enabled) {
            if tags.contains(&t.name) || matches!(t.name.as_str(), "direct" | "block" | "") {
                return Err("Duplicate or reserved tunnel name".into());
            }
            match t.kind.as_str() {
                "hysteria2" => {
                    let mut cfg =
                        hysteria::config(t.link.as_deref().ok_or("Missing Hysteria2 link")?)?;
                    let mut out = cfg["outbounds"][0].take();
                    out["tag"] = json!(t.name);
                    outbounds.push(out);
                }
                "openvpn" if matches!(t.ovpn_driver.as_deref(), None | Some("native" | "")) => {
                    let base = dir
                        .join("tunnels")
                        .join(part(t.id.as_deref())?)
                        .canonicalize()
                        .map_err(|_| "Tunnel directory missing")?;
                    let file = base.join(part(t.config_file.as_deref())?);
                    let text = fs::read_to_string(file).map_err(|_| "OpenVPN file unavailable")?;
                    let password = match t.ovpn_pass_protected.as_deref() {
                        None | Some("") => String::new(),
                        Some(secret) => unprotect(secret)?,
                    };
                    let auth = t
                        .ovpn_user
                        .as_deref()
                        .filter(|s| !s.is_empty())
                        .map(|user| (user, password.as_str()));
                    endpoints.push(openvpn::endpoint(&text, &t.name, auth, |name| {
                        let path = base
                            .join(name)
                            .canonicalize()
                            .map_err(|_| "OpenVPN certificate file unavailable")?;
                        if !path.starts_with(&base) {
                            return Err(
                                "OpenVPN certificate is outside the tunnel directory".into()
                            );
                        }
                        fs::read_to_string(path)
                            .map_err(|_| "Cannot read OpenVPN certificate".into())
                    })?);
                    ovpn.push(t.name.clone());
                }
                _ => return Err(format!("Unsupported tunnel: {}", t.kind)),
            }
            if let Some(spec) = t.dns_server.as_deref().filter(|s| !s.is_empty()) {
                dns_specs.insert(t.name.as_str(), spec);
            }
            tags.push(t.name.clone());
        }
        if self.final_tag != "direct" && !tags.contains(&self.final_tag) {
            return Err("Default tunnel is unavailable".into());
        }
        let mut protected = Vec::new();
        if self.kill_switch {
            let target = self
                .kill_tunnel
                .as_ref()
                .filter(|t| tags.contains(t))
                .unwrap_or(&self.final_tag);
            if !tags.contains(target) {
                return Err("Protection requires an enabled tunnel".into());
            }
            for path in self.kill_apps.iter().flatten() {
                if let Some(name) = path
                    .rsplit('\\')
                    .next()
                    .filter(|s| !s.is_empty() && !s.contains('*'))
                {
                    protected.push(Rule {
                        enabled: true,
                        kind: "process_name".into(),
                        values: name.into(),
                        target: target.clone(),
                    });
                }
            }
            if let Some(domains) = &self.kill_domains {
                protected.push(Rule {
                    enabled: true,
                    kind: "domain_suffix".into(),
                    values: domains.clone(),
                    target: target.clone(),
                });
            }
        }
        let mut sets = BTreeSet::new();
        let mut dns_tags = BTreeSet::new();
        for rule in protected
            .iter()
            .chain(self.rules.iter())
            .filter(|r| r.enabled)
        {
            for route in compile(std::slice::from_ref(rule), &tags, Platform::Windows)? {
                if let Some(names) = route["rule_set"].as_array() {
                    for name in names.iter().filter_map(Value::as_str) {
                        sets.insert(name.to_owned());
                    }
                }
                if matches!(
                    rule.kind.as_str(),
                    "domain" | "domain_suffix" | "domain_keyword" | "geosite"
                ) && rule.target != "block"
                {
                    let key = if rule.kind == "geosite" {
                        "rule_set"
                    } else {
                        &rule.kind
                    };
                    let server = if rule.target == "direct" {
                        "dns-local".into()
                    } else {
                        dns_tags.insert(rule.target.clone());
                        format!("dns-{}", rule.target)
                    };
                    dns_rules.push(json!({(key):route[key].clone(),"server":server}));
                }
                routes.push(route);
            }
        }
        for tag in &ovpn {
            routes.push(json!({"preferred_by":[tag],"outbound":tag}));
            if !dns_specs.contains_key(tag.as_str()) {
                let server = format!("dns-{tag}");
                dns.push(json!({"type":"openvpn","tag":server,"endpoint":tag,"accept_default_resolvers":true,"accept_search_domain":true}));
                dns_rules.insert(0, json!({"preferred_by":[server],"server":server}));
                dns_tags.remove(tag);
            }
        }
        for tag in dns_tags {
            let mut s = dns_server(
                dns_specs
                    .get(tag.as_str())
                    .copied()
                    .unwrap_or("https://1.1.1.1/dns-query"),
                &format!("dns-{tag}"),
            )?;
            s["detour"] = json!(tag);
            dns.push(s);
        }
        let mut final_dns = "dns-local";
        if let Some(spec) = self
            .default_dns
            .as_deref()
            .filter(|s| !matches!(*s, "" | "local"))
        {
            dns.push(dns_server(spec, "dns-default")?);
            final_dns = "dns-default";
        }
        routes.push(json!({"ip_is_private":true,"outbound":"direct"}));
        let detour = self
            .rule_set_detour
            .as_deref()
            .filter(|d| tags.iter().any(|t| t == d))
            .unwrap_or("direct");
        let sets:Vec<_>=sets.into_iter().map(|tag|{
            let repo=if tag.starts_with("geoip-"){"sing-geoip"}else{"sing-geosite"};
            json!({"type":"remote","tag":tag,"format":"binary","url":format!("https://raw.githubusercontent.com/SagerNet/{repo}/rule-set/{tag}.srs"),"download_detour":detour})
        }).collect();
        Ok(json!({
            "dns":{"servers":dns,"rules":dns_rules,"final":final_dns,"strategy":"ipv4_only"},
            "route":{"rules":routes,"final":self.final_tag,"rule_set":sets,"auto_detect_interface":true,"find_process":true,"default_domain_resolver":"dns-local"},
            "inbounds":[{"type":"tun","tag":"tun-in","interface_name":"Hydra","address":["172.19.0.1/30","fdfe:dcba:9876::1/126"],"mtu":9000,"auto_route":true,"strict_route":self.strict_route,"stack":"mixed"}],
            "endpoints":endpoints,"outbounds":outbounds
        }))
    }
}
