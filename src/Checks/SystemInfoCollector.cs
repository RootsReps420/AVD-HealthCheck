// "This device" facts: name, uptime, OS build, last patch, serial number, Azure VM details.
// Everything here is read locally (WMI, registry, Windows Update history) apart from
// the Azure Instance Metadata Service, which is a link-local address on the VM itself.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using Microsoft.Win32;

namespace AvdHealthCheck
{
    public class DeviceInfo
    {
        public DeviceInfo() { Fields = new List<KeyValuePair<string, string>>(); }
        public List<KeyValuePair<string, string>> Fields { get; private set; }

        public void Add(string label, string value)
        {
            Fields.Add(new KeyValuePair<string, string>(label, string.IsNullOrWhiteSpace(value) ? "Not available" : value.Trim()));
        }
    }

    public static class SystemInfoCollector
    {
        /// <summary>Labels in display order, so the card can show placeholders before the data arrives.</summary>
        public static readonly string[] Labels =
        {
            "Device name", "Signed-in user", "Session", "Operating system", "Up time",
            "Last OS update", "Restart pending", "Defender intelligence", "Serial number", "Model",
            "Azure VM", "Resource group", "VM ID", "Processor", "Memory", "Disk (C:)", "IP address"
        };

        public static DeviceInfo Collect()
        {
            var info = new DeviceInfo();
            AzureVm vm = AzureVm.TryFetch();

            info.Add("Device name", Safe(DeviceName));
            info.Add("Signed-in user", Safe(() => Environment.UserDomainName + "\\" + Environment.UserName));
            info.Add("Session", Safe(SessionType));
            info.Add("Operating system", Safe(OsVersion));
            info.Add("Up time", Safe(Uptime));

            UpdateHistory updates = Safe(() => UpdateHistory.Read(), null);
            string osUpdate = updates != null ? updates.OsUpdate : null;
            info.Add("Last OS update", osUpdate ?? Safe(LastHotfix));
            info.Add("Restart pending", Safe(RestartPending));
            info.Add("Defender intelligence", updates != null ? updates.DefenderUpdate : null);

            info.Add("Serial number", Safe(SerialNumber));
            info.Add("Model", Safe(Model));
            info.Add("Azure VM", vm == null ? "Not detected (metadata service unavailable)" : vm.Size + " · " + vm.Location + (string.IsNullOrEmpty(vm.Image) ? "" : " · " + vm.Image));
            info.Add("Resource group", vm == null ? null : vm.ResourceGroup);
            string uuid = Safe(SystemUuid);
            info.Add("VM ID", vm != null ? vm.VmId : (uuid == null ? null : uuid + " (system UUID)"));
            info.Add("Processor", Safe(Processor));
            info.Add("Memory", Safe(Memory));
            info.Add("Disk (C:)", Safe(Disk));
            info.Add("IP address", Safe(IpAddress));
            return info;
        }

        // ---------------------------------------------------------------- individual facts

        private static string DeviceName()
        {
            string domain = IPGlobalProperties.GetIPGlobalProperties().DomainName;
            return string.IsNullOrEmpty(domain) ? Environment.MachineName : Environment.MachineName + " (" + domain + ")";
        }

        private static string SessionType()
        {
            bool remote = NativeMethods.GetSystemMetrics(NativeMethods.SM_REMOTESESSION) != 0;
            return remote ? "Remote session (RDP / AVD)" : "Console";
        }

        private static string OsVersion()
        {
            string caption = Wmi("Win32_OperatingSystem", "Caption") ?? "Windows";
            caption = caption.Replace("Microsoft ", "");
            using (RegistryKey key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
            {
                if (key == null) return caption;
                string display = key.GetValue("DisplayVersion") as string;
                string build = key.GetValue("CurrentBuild") as string;
                object ubr = key.GetValue("UBR");
                string full = build + (ubr != null ? "." + ubr : "");
                return caption + (string.IsNullOrEmpty(display) ? "" : " " + display) + " (build " + full + ")";
            }
        }

        private static string Uptime()
        {
            string raw = Wmi("Win32_OperatingSystem", "LastBootUpTime");
            if (raw == null) return null;
            DateTime boot = ManagementDateTimeConverter.ToDateTime(raw);
            TimeSpan up = DateTime.Now - boot;
            return Format.Duration(up) + " (since " + Format.Date(boot) + ")";
        }

        private static string LastHotfix()
        {
            // Fallback when the Windows Update history is unavailable.
            DateTime latest = DateTime.MinValue;
            string id = null;
            using (var searcher = new ManagementObjectSearcher("SELECT HotFixID, InstalledOn FROM Win32_QuickFixEngineering"))
            {
                foreach (ManagementBaseObject row in searcher.Get())
                {
                    string installed = row["InstalledOn"] as string;
                    DateTime when;
                    if (installed != null && DateTime.TryParse(installed, CultureInfo.GetCultureInfo("en-US"), DateTimeStyles.None, out when) && when > latest)
                    {
                        latest = when;
                        id = row["HotFixID"] as string;
                    }
                }
            }
            if (id == null) return null;
            return id + " · installed " + Format.Date(latest, false) + " (" + Format.Ago(latest) + ")";
        }

        // Firmware placeholders that mean "no real serial was set".
        private static readonly string[] PlaceholderSerials =
        {
            "default string", "system serial number", "to be filled by o.e.m.", "to be filled by oem",
            "none", "0", "not specified", "not applicable", "chassis serial number", "123456789"
        };

        private static string SerialNumber()
        {
            string serial = (Wmi("Win32_BIOS", "SerialNumber") ?? "").Trim();
            if (serial.Length == 0 || PlaceholderSerials.Contains(serial.ToLowerInvariant()))
                return "Not set in firmware (common on virtual machines)";
            return serial;
        }

        private static string RestartPending()
        {
            var reasons = new List<string>();
            if (KeyExists(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired")) reasons.Add("Windows Update");
            if (KeyExists(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending")) reasons.Add("component servicing");
            return reasons.Count == 0 ? "No" : "Yes · " + string.Join(", ", reasons);
        }

        private static bool KeyExists(string path)
        {
            using (RegistryKey key = Registry.LocalMachine.OpenSubKey(path))
                return key != null;
        }

        private static string Model()
        {
            string maker = Wmi("Win32_ComputerSystem", "Manufacturer");
            string model = Wmi("Win32_ComputerSystem", "Model");
            return ((maker ?? "") + " " + (model ?? "")).Trim();
        }

        private static string SystemUuid()
        {
            return Wmi("Win32_ComputerSystemProduct", "UUID");
        }

        private static string Processor()
        {
            string name = Wmi("Win32_Processor", "Name");
            return Environment.ProcessorCount + " vCPU" + (name == null ? "" : " · " + Regex.Replace(name, @"\s+", " ").Trim());
        }

        private static string Memory()
        {
            ulong totalKb = Convert.ToUInt64(Wmi("Win32_OperatingSystem", "TotalVisibleMemorySize"));
            ulong freeKb = Convert.ToUInt64(Wmi("Win32_OperatingSystem", "FreePhysicalMemory"));
            return Format.Gb(totalKb * 1024) + " total · " + Format.Gb(freeKb * 1024) + " free";
        }

        private static string Disk()
        {
            var drive = new DriveInfo("C");
            double freePct = 100.0 * drive.AvailableFreeSpace / drive.TotalSize;
            return Format.Gb((ulong)drive.TotalSize) + " total · " + Format.Gb((ulong)drive.AvailableFreeSpace) + " free (" + freePct.ToString("0") + "%)";
        }

        private static string IpAddress()
        {
            var addresses = new List<string>();
            foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                IPInterfaceProperties props = nic.GetIPProperties();
                if (props.GatewayAddresses.Count == 0) continue;
                foreach (UnicastIPAddressInformation ip in props.UnicastAddresses)
                    if (ip.Address.AddressFamily == AddressFamily.InterNetwork) addresses.Add(ip.Address.ToString());
            }
            return addresses.Count == 0 ? null : string.Join(", ", addresses.Distinct());
        }

        // ---------------------------------------------------------------- helpers

        private static string Wmi(string cls, string property)
        {
            using (var searcher = new ManagementObjectSearcher("SELECT " + property + " FROM " + cls))
            {
                foreach (ManagementBaseObject row in searcher.Get())
                {
                    object value = row[property];
                    if (value != null) return value.ToString();
                }
            }
            return null;
        }

        private static string Safe(Func<string> read)
        {
            try { return read(); }
            catch (Exception) { return null; }
        }

        private static T Safe<T>(Func<T> read, T fallback)
        {
            try { return read(); }
            catch (Exception) { return fallback; }
        }
    }

    /// <summary>Latest successful installs from the Windows Update Agent history.</summary>
    public class UpdateHistory
    {
        public string OsUpdate { get; private set; }
        public string DefenderUpdate { get; private set; }

        public static UpdateHistory Read()
        {
            Type type = Type.GetTypeFromProgID("Microsoft.Update.Session");
            if (type == null) return null;
            dynamic session = Activator.CreateInstance(type);
            dynamic searcher = session.CreateUpdateSearcher();
            int count = searcher.GetTotalHistoryCount();
            if (count <= 0) return null;

            dynamic history = searcher.QueryHistory(0, Math.Min(count, 300));
            DateTime osDate = DateTime.MinValue, defDate = DateTime.MinValue;
            string osTitle = null;
            bool osPending = false;

            foreach (dynamic entry in history)
            {
                int operation = entry.Operation;     // 1 = install
                int result = entry.ResultCode;       // 1 = in progress (awaiting restart), 2 = succeeded, 3 = succeeded with errors
                if (operation != 1 || result < 1 || result > 3) continue;

                string title = entry.Title ?? "";
                DateTime when = ((DateTime)entry.Date).ToLocalTime();

                if (title.IndexOf("Security Intelligence", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    if (result != 1 && when > defDate) defDate = when;
                }
                else if (IsOsPatch(title) && when > osDate)
                {
                    osDate = when;
                    osTitle = title;
                    osPending = result == 1;
                }
            }

            var history_ = new UpdateHistory();
            if (osTitle != null)
                history_.OsUpdate = Describe(osTitle) + (osPending ? " · installed, waiting for restart, " : " · installed ") +
                                    Format.Date(osDate, false) + " (" + Format.Ago(osDate) + ")";
            if (defDate > DateTime.MinValue)
                history_.DefenderUpdate = "Updated " + Format.Date(defDate) + " (" + Format.Ago(defDate) + ")";
            return history_.OsUpdate == null && history_.DefenderUpdate == null ? null : history_;
        }

        // Matches both title styles: "2026-11 Cumulative Update for Windows 11 ... (KB…)" and the
        // newer "2026-11 Security Update (KB…) (26100.1234)". Windows itself only: .NET, Office,
        // Defender and other product updates do not count as the OS patch level.
        private static readonly Regex OsPatch = new Regex(
            @"(Cumulative Update|Security Update|Servicing Stack Update|Quality Rollup)", RegexOptions.IgnoreCase);
        private static readonly Regex NotOs = new Regex(
            @"\.NET|Office|Microsoft 365|Defender|Malicious Software|Edge|SQL|Visual Studio|Silverlight|Teams",
            RegexOptions.IgnoreCase);

        private static bool IsOsPatch(string title)
        {
            return OsPatch.IsMatch(title) && !NotOs.IsMatch(title);
        }

        /// <summary>"2026-11 Cumulative Update for Windows 11 Version 24H2 for x64-based Systems (KB5050000)" → "KB5050000 · 2026-11 Cumulative Update".</summary>
        private static string Describe(string title)
        {
            Match kb = Regex.Match(title, @"KB\d+");
            Match month = Regex.Match(title, @"^\d{4}-\d{2}\s+.*?(Cumulative Update|Security Update|Quality Rollup|Servicing Stack Update)( Preview)?", RegexOptions.IgnoreCase);
            string name = month.Success ? month.Value.Trim() : title;
            if (name.Length > 70) name = name.Substring(0, 67) + "…";
            return kb.Success ? kb.Value + " · " + name : name;
        }
    }

    /// <summary>The handful of Azure Instance Metadata fields worth putting in a ticket.</summary>
    public class AzureVm
    {
        public string Name { get; set; }
        public string Size { get; set; }
        public string Location { get; set; }
        public string ResourceGroup { get; set; }
        public string VmId { get; set; }
        public string Image { get; set; }

        public static AzureVm TryFetch()
        {
            try
            {
                var request = (HttpWebRequest)WebRequest.Create(EndpointChecker.ImdsUrl);
                request.Proxy = null;              // IMDS must never go through a proxy
                request.Headers.Add("Metadata", "true");
                request.Timeout = 3000;
                using (var response = (HttpWebResponse)request.GetResponse())
                using (var reader = new StreamReader(response.GetResponseStream()))
                    return Parse(reader.ReadToEnd());
            }
            catch (Exception)
            {
                return null;
            }
        }

        public static AzureVm Parse(string json)
        {
            try
            {
                var root = new JavaScriptSerializer().DeserializeObject(json) as Dictionary<string, object>;
                if (root == null) return null;
                var compute = Get(root, "compute") as Dictionary<string, object>;
                if (compute == null) return null;

                var vm = new AzureVm
                {
                    Name = Get(compute, "name") as string,
                    Size = Get(compute, "vmSize") as string,
                    Location = Get(compute, "location") as string,
                    ResourceGroup = Get(compute, "resourceGroupName") as string,
                    VmId = Get(compute, "vmId") as string
                };

                var image = Get(Get(compute, "storageProfile") as Dictionary<string, object>, "imageReference") as Dictionary<string, object>;
                string offer = Get(image, "offer") as string;
                string sku = Get(image, "sku") as string;
                if (!string.IsNullOrEmpty(offer)) vm.Image = offer + (string.IsNullOrEmpty(sku) ? "" : " / " + sku);
                else if (Get(image, "id") is string) vm.Image = "Custom image";
                return vm;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static object Get(Dictionary<string, object> map, string key)
        {
            object value;
            return map != null && map.TryGetValue(key, out value) ? value : null;
        }
    }

    public static class Format
    {
        public static string Duration(TimeSpan span)
        {
            if (span.TotalMinutes < 1) return "less than a minute";
            var parts = new List<string>();
            if (span.Days > 0) parts.Add(span.Days + (span.Days == 1 ? " day" : " days"));
            if (span.Hours > 0) parts.Add(span.Hours + (span.Hours == 1 ? " hr" : " hrs"));
            if (span.Days == 0 && span.Minutes > 0) parts.Add(span.Minutes + " min");
            return string.Join(" ", parts);
        }

        public static string Ago(DateTime when)
        {
            TimeSpan span = DateTime.Now - when;
            if (span.TotalDays >= 2) return (int)span.TotalDays + " days ago";
            if (span.TotalDays >= 1) return "yesterday";
            if (span.TotalHours >= 1) return (int)span.TotalHours + " hrs ago";
            return "just now";
        }

        public static string Date(DateTime when, bool withTime = true)
        {
            return when.ToString(withTime ? "dd MMM yyyy HH:mm" : "dd MMM yyyy", CultureInfo.GetCultureInfo("en-GB"));
        }

        public static string Gb(ulong bytes)
        {
            return (bytes / 1073741824.0).ToString(bytes >= 10UL * 1073741824 ? "0" : "0.0") + " GB";
        }
    }

    internal static class NativeMethods
    {
        public const int SM_REMOTESESSION = 0x1000;

        [DllImport("user32.dll")]
        public static extern int GetSystemMetrics(int index);

        [DllImport("dwmapi.dll")]
        public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    }
}
