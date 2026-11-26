using System;
using System.Collections.Generic;
using System.Management;
using System.Net.NetworkInformation;

namespace Hydra
{
    internal static class WifiNetworks
    {
        // Windows also lists VPN network profiles. Match only connected wireless adapters.
        internal static HashSet<string> Connected(List<string> warnings)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            try
            {
                var adapters = new HashSet<int>();
                foreach (NetworkInterface adapter in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (adapter.NetworkInterfaceType != NetworkInterfaceType.Wireless80211 ||
                        adapter.OperationalStatus != OperationalStatus.Up) continue;
                    var properties = adapter.GetIPProperties();
                    var v4 = adapter.Supports(NetworkInterfaceComponent.IPv4) ? properties.GetIPv4Properties() : null;
                    var v6 = adapter.Supports(NetworkInterfaceComponent.IPv6) ? properties.GetIPv6Properties() : null;
                    if (v4 != null) adapters.Add(v4.Index);
                    if (v6 != null) adapters.Add(v6.Index);
                }
                if (adapters.Count == 0) return names;
                using (var query = new ManagementObjectSearcher(@"root\StandardCimv2",
                    "SELECT Name, InterfaceIndex FROM MSFT_NetConnectionProfile WHERE IPv4Connectivity <> 0 OR IPv6Connectivity <> 0"))
                using (var profiles = query.Get())
                {
                    foreach (ManagementObject profile in profiles)
                    using (profile)
                    {
                        if (adapters.Contains(Convert.ToInt32(profile["InterfaceIndex"])))
                            names.Add(Convert.ToString(profile["Name"]));
                    }
                }
            }
            catch (Exception ex)
            {
                warnings.Add("Wi-Fi network lookup failed: " + ex.Message);
            }
            return names;
        }
    }
}
