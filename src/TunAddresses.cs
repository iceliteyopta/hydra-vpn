using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Numerics;
using System.Text.RegularExpressions;

namespace Hydra
{
    internal sealed class TunAddresses
    {
        internal readonly IPAddress IPv4, IPv6;
        internal readonly bool ForeignTunDefault;

        internal TunAddresses(List<Subnet> occupied, bool foreignTunDefault = false)
        {
            ForeignTunDefault = foreignTunDefault;
            IPv4 = FindAddress(occupied, "172.19.0.0/30", "172.16.0.0/12", "192.168.0.0/16", "10.0.0.0/8");
            IPv6 = FindAddress(occupied, "fdfe:dcba:9876::/126", "fd00::/8");
        }

        // Address arithmetic is unsigned and in network byte order for both families.
        static BigInteger Number(IPAddress address)
        {
            var bytes = address.GetAddressBytes();
            Array.Reverse(bytes);
            Array.Resize(ref bytes, bytes.Length + 1);
            return new BigInteger(bytes);
        }

        static IPAddress Address(BigInteger value, int length)
        {
            var bytes = value.ToByteArray();
            Array.Resize(ref bytes, length);
            Array.Reverse(bytes);
            return new IPAddress(bytes);
        }

        internal sealed class Subnet
        {
            internal readonly int Bytes;
            internal readonly BigInteger First, Last;

            internal Subnet(IPAddress ip, int prefix)
            {
                Bytes = ip.GetAddressBytes().Length;
                if (prefix < 0 || prefix > Bytes * 8) throw new ArgumentOutOfRangeException("prefix");
                int bits = Bytes * 8 - prefix;
                First = (Number(ip) >> bits) << bits;
                Last = First + (BigInteger.One << bits) - 1;
            }

            internal static Subnet Parse(string cidr)
            {
                var parts = cidr.Split('/');
                return new Subnet(IPAddress.Parse(parts[0]), int.Parse(parts[1]));
            }
        }

        static IPAddress FindAddress(List<Subnet> occupied, params string[] pools)
        {
            foreach (var cidr in pools)
            {
                var pool = Subnet.Parse(cidr);
                var candidate = pool.First;
                // A /30 and a /126 both occupy four addresses. Skip whole occupied prefixes.
                foreach (var used in occupied.Where(s => s.Bytes == pool.Bytes).OrderBy(s => s.First))
                {
                    if (used.First > candidate + 3) break;
                    if (used.Last >= candidate) candidate = (used.Last / 4 + 1) * 4;
                }
                if (candidate + 3 <= pool.Last) return Address(candidate + 1, pool.Bytes);
            }
            throw new InvalidOperationException("Нет свободной частной подсети для адаптера Hydra.");
        }

        static bool IsForeignTun(NetworkInterface adapter)
        {
            // Windows reports Wintun adapters as IF_TYPE_PROP_VIRTUAL (53).
            return adapter.OperationalStatus == OperationalStatus.Up &&
                (int)adapter.NetworkInterfaceType == 53 &&
                !string.Equals(adapter.Name, ConfigBuilder.TunName, StringComparison.OrdinalIgnoreCase) &&
                (string.Equals(adapter.Name, "neko-tun", StringComparison.OrdinalIgnoreCase) ||
                 adapter.Description.StartsWith("sing-tun Tunnel", StringComparison.OrdinalIgnoreCase));
        }

        internal static bool IsForeignDefault(string[] cells, HashSet<IPAddress> tunnelAddresses)
        {
            // Active IPv4 rows have an interface address before the metric. Persistent
            // rows have only network, netmask, gateway and metric (no interface).
            IPAddress iface;
            uint metric;
            return cells.Length >= 5 && cells[0] == "0.0.0.0" && cells[1] == "0.0.0.0" &&
                uint.TryParse(cells[cells.Length - 1], out metric) &&
                IPAddress.TryParse(cells[cells.Length - 2], out iface) && tunnelAddresses.Contains(iface);
        }

        internal static TunAddresses Detect()
        {
            var occupied = new List<Subnet>();
            var tunnelAddresses = new HashSet<IPAddress>();
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                bool foreignTun = IsForeignTun(adapter);
                foreach (var addr in adapter.GetIPProperties().UnicastAddresses)
                {
                    occupied.Add(new Subnet(addr.Address, addr.PrefixLength));
                    if (foreignTun && addr.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                        tunnelAddresses.Add(addr.Address);
                }
            }

            // route print also covers routed LANs with no local interface address.
            // Default and split-default routes are transit paths, not allocations.
            string table;
            if (Proc.Run(Path.Combine(Environment.SystemDirectory, "route.exe"), "print", out table, 10000) != 0)
                throw new InvalidOperationException("Не удалось прочитать таблицу маршрутов Windows.");
            bool foreignDefault = false;
            foreach (var line in table.Split('\n'))
            {
                var cells = Regex.Split(line.Trim(), @"\s+");
                if (cells.Length < 3) continue;
                if (IsForeignDefault(cells, tunnelAddresses)) foreignDefault = true;
                IPAddress ip, mask;
                if (cells[0].Contains(".") && IPAddress.TryParse(cells[0], out ip) &&
                    IPAddress.TryParse(cells[1], out mask) && mask.GetAddressBytes().Length == 4)
                {
                    int prefix = 0;
                    foreach (byte b in mask.GetAddressBytes())
                        for (int bit = 128; bit != 0; bit >>= 1) if ((b & bit) != 0) prefix++;
                    if (prefix > 1) occupied.Add(new Subnet(ip, prefix));
                }
                else if (cells[2].Contains(":") && cells[2].Contains("/"))
                {
                    var parts = cells[2].Split('/');
                    int prefix;
                    if (IPAddress.TryParse(parts[0], out ip) && int.TryParse(parts[1], out prefix) && prefix > 1)
                        occupied.Add(new Subnet(ip, prefix));
                }
            }
            return new TunAddresses(occupied, foreignDefault);
        }

        internal string BlockedIPv4
        {
            get
            {
                var n = Number(IPv4);
                string before = Address(n - 1, 4).ToString(), after = Address(n + 1, 4).ToString();
                if (IPv4.GetAddressBytes()[0] < 127)
                    return "0.0.0.0-" + before + "," + after + "-126.255.255.255,128.0.0.0-255.255.255.255";
                return "0.0.0.0-126.255.255.255,128.0.0.0-" + before + "," + after + "-255.255.255.255";
            }
        }

        internal string BlockedIPv6
        {
            get
            {
                var n = Number(IPv6);
                return "::2-" + Address(n - 1, 16) + "," + Address(n + 1, 16) + "-ffff:ffff:ffff:ffff:ffff:ffff:ffff:ffff";
            }
        }
    }
}
