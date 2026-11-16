using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Hydra
{
    public static class TunnelTypes
    {
        public const string OpenVpn = "openvpn";
        public const string AmneziaWg = "amneziawg";
        public const string WireGuard = "wireguard";

        public static bool IsLink(string t)
        {
            return t != OpenVpn && t != AmneziaWg && t != WireGuard;
        }
    }

    public class Tunnel
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Type { get; set; }
        public bool Enabled { get; set; }
        public string Server { get; set; }
        // Для протоколов по ссылке (vless://, hy2:// ...)
        public string Link { get; set; }
        // Для OpenVPN / WireGuard / AmneziaWG: файл внутри data\tunnels\<Id>\
        public string ConfigFile { get; set; }
        // OpenVPN
        public string OvpnUser { get; set; }
        public string OvpnPassProtected { get; set; }
        public string OvpnDriver { get; set; }
        // DNS-сервер внутри туннеля (для доменов, которые идут через этот туннель)
        public string DnsServer { get; set; }

        public Tunnel()
        {
            Id = Guid.NewGuid().ToString("N").Substring(0, 6);
            Enabled = true;
            OvpnDriver = "native";
        }

        public string InterfaceName() { return "MT-" + Id; }
        public string Dir() { return Path.Combine(AppPaths.TunnelsDir, Id); }
        public string ConfigPath() { return string.IsNullOrEmpty(ConfigFile) ? null : Path.Combine(Dir(), ConfigFile); }
    }

    public class Rule
    {
        public bool Enabled { get; set; }
        public string Kind { get; set; }
        public string Values { get; set; }
        public string Target { get; set; }
        public Rule() { Enabled = true; Kind = "domain_suffix"; Values = ""; Target = "direct"; }
    }

    public class Profile
    {
        public List<Tunnel> Tunnels { get; set; }
        public List<Rule> Rules { get; set; }
        public string Final { get; set; }
        public string DefaultDns { get; set; }
        public bool StrictRoute { get; set; }
        public string LogLevel { get; set; }
        public string RuleSetDetour { get; set; }
        public string SingBoxPath { get; set; }
        public string OpenVpnPath { get; set; }
        public string AwgGoPath { get; set; }
        public string Language { get; set; } // auto | ru | en
        // Kill switch: эти приложения и домены никогда не выходят в интернет мимо VPN
        public bool KillSwitch { get; set; }
        public List<string> KillApps { get; set; }
        public string KillDomains { get; set; }
        public string KillTunnel { get; set; }
        // Обновления с GitHub
        public bool CheckUpdates { get; set; }
        public string SkippedVersion { get; set; }

        public Profile()
        {
            CheckUpdates = true;
            SkippedVersion = "";
            KillApps = new List<string>(Hydra.KillSwitch.DefaultApps);
            KillDomains = Hydra.KillSwitch.DefaultDomains;
            KillTunnel = "";
            Tunnels = new List<Tunnel>();
            Rules = new List<Rule>();
            Final = "direct";
            DefaultDns = "local";
            LogLevel = "info";
            RuleSetDetour = "direct";
            SingBoxPath = "";
            OpenVpnPath = "";
            AwgGoPath = "";
        }

        public Tunnel Find(string name)
        {
            foreach (var t in Tunnels) if (t.Name == name) return t;
            return null;
        }

        public string UniqueName(string baseName)
        {
            if (string.IsNullOrEmpty(baseName)) baseName = "tunnel";
            baseName = baseName.Trim();
            if (baseName == "direct" || baseName == "block") baseName += "-1";
            string n = baseName; int i = 2;
            while (Find(n) != null) n = baseName + " " + (i++);
            return n;
        }
    }

    public static class AppPaths
    {
        public static string BaseDir { get { return AppDomain.CurrentDomain.BaseDirectory; } }
        public static string DataDir
        {
            get
            {
                string env = Environment.GetEnvironmentVariable("HYDRA_DATA");
                return string.IsNullOrEmpty(env) ? Path.Combine(BaseDir, "data") : env;
            }
        }
        public static string TunnelsDir { get { return Path.Combine(DataDir, "tunnels"); } }
        public static string RunDir { get { return Path.Combine(DataDir, "run"); } }
        public static string CoresDir { get { return Path.Combine(BaseDir, "cores"); } }
        public static string ProfileFile { get { return Path.Combine(DataDir, "profile.json"); } }

        public static string FirstExisting(params string[] paths)
        {
            foreach (var p in paths) if (!string.IsNullOrEmpty(p) && File.Exists(p)) return p;
            return null;
        }

        public static string SingBox(Profile p)
        {
            return FirstExisting(p.SingBoxPath, Path.Combine(CoresDir, "sing-box.exe"), Path.Combine(CoresDir, "sing-box", "sing-box.exe"));
        }

        public static string OpenVpn(Profile p)
        {
            string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            return FirstExisting(p.OpenVpnPath, Path.Combine(CoresDir, "openvpn", "openvpn.exe"), Path.Combine(pf, "OpenVPN", "bin", "openvpn.exe"), @"C:\Program Files\OpenVPN\bin\openvpn.exe");
        }

        public static string AwgGo(Profile p)
        {
            return FirstExisting(p.AwgGoPath, Path.Combine(CoresDir, "amneziawg", "amneziawg-go.exe"), Path.Combine(CoresDir, "amneziawg-go.exe"));
        }
    }

    public static class Secret
    {
        public static string Protect(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var b = ProtectedData.Protect(Encoding.UTF8.GetBytes(s), null, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(b);
        }

        public static string Unprotect(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            try
            {
                var b = ProtectedData.Unprotect(Convert.FromBase64String(s), null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(b);
            }
            catch { return ""; }
        }
    }

    public class ProfileStore
    {
        public Profile Profile;

        public static ProfileStore Load()
        {
            var st = new ProfileStore();
            try
            {
                if (File.Exists(AppPaths.ProfileFile))
                    st.Profile = Json.Deserialize<Profile>(File.ReadAllText(AppPaths.ProfileFile, Encoding.UTF8));
            }
            catch (Exception ex)
            {
                string bak = AppPaths.ProfileFile + ".broken";
                try { File.Copy(AppPaths.ProfileFile, bak, true); } catch { }
                System.Diagnostics.Debug.WriteLine(ex);
            }
            if (st.Profile == null) st.Profile = new Profile();
            if (st.Profile.Tunnels == null) st.Profile.Tunnels = new List<Tunnel>();
            if (st.Profile.Rules == null) st.Profile.Rules = new List<Rule>();
            return st;
        }

        public void Save()
        {
            Directory.CreateDirectory(AppPaths.DataDir);
            string tmp = AppPaths.ProfileFile + ".tmp";
            File.WriteAllText(tmp, Json.Serialize(Profile), new UTF8Encoding(false));
            if (File.Exists(AppPaths.ProfileFile)) File.Delete(AppPaths.ProfileFile);
            File.Move(tmp, AppPaths.ProfileFile);
        }
    }
}
