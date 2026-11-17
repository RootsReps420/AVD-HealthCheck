// Which app the user connected with: Windows App (wanted), the legacy Remote Desktop app,
// Remote Desktop Connection (mstsc), or the browser (web client).
//
// Windows does not record "client type" on the session host (AVD only logs it centrally,
// in the WVDConnections table). What the session host does get is what the RDP client
// reports about itself: its install folder, build number, and device name. The install
// folder is the strongest signal, so the rules below match on it first. The rules live
// in endpoints.json and the raw values are always shown, so a client that is not
// recognised can be added without a rebuild.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace AvdHealthCheck
{
    /// <summary>What the connected RDP client reported, read with WTSQuerySessionInformation.</summary>
    public class ClientSession
    {
        public bool Remote { get; private set; }
        public int Protocol { get; private set; }          // 0 console, 2 RDP
        public string Name { get; private set; }           // the user's device name
        public string Directory { get; private set; }      // the client's install folder / control path
        public uint Build { get; private set; }
        public ushort ProductId { get; private set; }
        public string Address { get; private set; }

        private const int WTSClientBuildNumber = 9, WTSClientName = 10, WTSClientDirectory = 11,
                          WTSClientProductId = 12, WTSClientAddress = 14, WTSClientProtocolType = 16;
        private static readonly IntPtr CurrentServer = IntPtr.Zero;
        private const int CurrentSession = -1;

        public static ClientSession Read()
        {
            var s = new ClientSession();
            s.Protocol = QueryUShort(WTSClientProtocolType);
            s.Remote = s.Protocol == 2 || NativeMethods.GetSystemMetrics(NativeMethods.SM_REMOTESESSION) != 0;
            s.Name = QueryString(WTSClientName);
            s.Directory = QueryString(WTSClientDirectory);
            s.Build = QueryUInt(WTSClientBuildNumber);
            s.ProductId = (ushort)QueryUShort(WTSClientProductId);
            s.Address = QueryAddress();
            return s;
        }

        /// <summary>The raw values, for the report and for writing new rules.</summary>
        public string Raw()
        {
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(Name)) parts.Add("device " + Name);
            if (Build > 0) parts.Add("build " + Build);
            if (!string.IsNullOrEmpty(Directory)) parts.Add("client path " + Directory);
            if (ProductId > 0) parts.Add("product " + ProductId);
            return parts.Count == 0 ? "the client reported no details" : string.Join(" · ", parts);
        }

        /// <summary>Build and device only, for a client that was recognised.</summary>
        public string Short()
        {
            var parts = new List<string>();
            if (Build > 0) parts.Add("build " + Build);
            if (!string.IsNullOrEmpty(Name)) parts.Add("from " + Name);
            return string.Join(" · ", parts);
        }

        // ---------------------------------------------------------------- WTS interop

        [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool WTSQuerySessionInformationW(IntPtr server, int session, int infoClass, out IntPtr buffer, out int bytes);

        [DllImport("wtsapi32.dll")]
        private static extern void WTSFreeMemory(IntPtr memory);

        private static string QueryString(int infoClass)
        {
            IntPtr buffer; int bytes;
            if (!WTSQuerySessionInformationW(CurrentServer, CurrentSession, infoClass, out buffer, out bytes) || buffer == IntPtr.Zero) return null;
            try { string value = Marshal.PtrToStringUni(buffer); return string.IsNullOrWhiteSpace(value) ? null : value.Trim(); }
            finally { WTSFreeMemory(buffer); }
        }

        private static int QueryUShort(int infoClass)
        {
            IntPtr buffer; int bytes;
            if (!WTSQuerySessionInformationW(CurrentServer, CurrentSession, infoClass, out buffer, out bytes) || buffer == IntPtr.Zero) return 0;
            try { return bytes >= 2 ? (ushort)Marshal.ReadInt16(buffer) : 0; }
            finally { WTSFreeMemory(buffer); }
        }

        private static uint QueryUInt(int infoClass)
        {
            IntPtr buffer; int bytes;
            if (!WTSQuerySessionInformationW(CurrentServer, CurrentSession, infoClass, out buffer, out bytes) || buffer == IntPtr.Zero) return 0;
            try { return bytes >= 4 ? (uint)Marshal.ReadInt32(buffer) : 0; }
            finally { WTSFreeMemory(buffer); }
        }

        // WTS_CLIENT_ADDRESS: DWORD AddressFamily; BYTE Address[20]. IPv4 sits in bytes 2..5.
        private static string QueryAddress()
        {
            IntPtr buffer; int bytes;
            if (!WTSQuerySessionInformationW(CurrentServer, CurrentSession, WTSClientAddress, out buffer, out bytes) || buffer == IntPtr.Zero) return null;
            try
            {
                if (bytes < 24 || Marshal.ReadInt32(buffer) != 2) return null; // AF_INET only
                byte[] ip = new byte[4];
                for (int i = 0; i < 4; i++) ip[i] = Marshal.ReadByte(buffer, 4 + 2 + i);
                return ip.All(b => b == 0) ? null : string.Join(".", ip);
            }
            finally { WTSFreeMemory(buffer); }
        }
    }

    public class ClientReport
    {
        public AgentResult Row { get; set; }
        public string App { get; set; }
        public string Summary { get; set; }   // one line for the report summary
    }

    public static class ClientDetector
    {
        /// <summary>Built-in rules, used when endpoints.json has none. First match wins.</summary>
        public static readonly List<ClientRule> DefaultRules = new List<ClientRule>
        {
            // Windows App is the Store package MicrosoftCorporationII.Windows365 (it carries an msrdc sub-folder, so it must come first).
            new ClientRule { Field = "directory", Match = "Windows365", App = "Windows App", State = "pass" },
            // The browser client. Matched on the client path only: device names can contain "web".
            new ClientRule { Field = "directory", Match = "html", App = "Web client (browser)", State = "warn",
                             Advice = "Degraded experience: limited redirection (no local drives or USB devices, PDF-only printing) and no Teams media optimisation. Use Windows App." },
            new ClientRule { Field = "directory", Match = "web", App = "Web client (browser)", State = "warn",
                             Advice = "Degraded experience: limited redirection (no local drives or USB devices, PDF-only printing) and no Teams media optimisation. Use Windows App." },
            // The old Remote Desktop Store app (Microsoft.RemoteDesktop) also lives under WindowsApps.
            new ClientRule { Field = "directory", Match = "Microsoft.RemoteDesktop", App = "Remote Desktop Store app (legacy)", State = "warn",
                             Advice = "Old and outdated client, replaced by Windows App. Move the user to Windows App." },
            new ClientRule { Field = "directory", Match = "Remote Desktop", App = "Remote Desktop app (legacy)", State = "warn",
                             Advice = "Old and outdated client, replaced by Windows App. Move the user to Windows App." },
            new ClientRule { Field = "directory", Match = "msrdc", App = "Remote Desktop app (legacy)", State = "warn",
                             Advice = "Old and outdated client, replaced by Windows App. Move the user to Windows App." },
            new ClientRule { Field = "directory", Match = "mstscax", App = "Remote Desktop Connection (mstsc)", State = "warn",
                             Advice = "Not the supported way into AVD. Move the user to Windows App." }
        };

        public static ClientReport Detect(List<ClientRule> rules)
        {
            ClientSession session = ClientSession.Read();
            var report = new ClientReport();

            if (!session.Remote)
            {
                report.App = "Not a remote session";
                report.Row = new AgentResult(CheckState.Warn, "N/A · this is the local console, not an AVD session");
                report.Summary = "not a remote session";
                return report;
            }

            ClientRule rule = (rules == null || rules.Count == 0 ? DefaultRules : rules).FirstOrDefault(r => r.Matches(session));
            if (rule == null)
            {
                report.App = "Unrecognised client";
                report.Row = new AgentResult(CheckState.Warn, "Unrecognised client · " + session.Raw() +
                                             " · add a rule under clientApp in endpoints.json");
                report.Summary = "unrecognised (" + session.Raw() + ")";
                return report;
            }

            report.App = rule.App;
            CheckState state = rule.Kind;
            string extra = session.Short();
            string detail = rule.App + (string.IsNullOrEmpty(extra) ? "" : " · " + extra) +
                            (string.IsNullOrEmpty(rule.Advice) ? "" : " · " + rule.Advice);
            report.Row = new AgentResult(state, detail);
            report.Summary = rule.App + (session.Build > 0 ? " (build " + session.Build + ")" : "") +
                             (state == CheckState.Pass ? "" : " · " + (rule.Advice ?? "not the recommended client"));
            return report;
        }

        /// <summary>The user's device name, for the This device card.</summary>
        public static string ClientDevice()
        {
            ClientSession s = ClientSession.Read();
            if (!s.Remote) return "Local console";
            return (s.Name ?? "Unknown") + (s.Address != null ? " (" + s.Address + ")" : "");
        }
    }

    public class ClientRule
    {
        public string Field { get; set; }   // directory | name | build | any
        public string Match { get; set; }   // case-insensitive "contains"
        public string App { get; set; }
        public string State { get; set; }   // pass | warn | fail
        public string Advice { get; set; }

        public CheckState Kind
        {
            get
            {
                switch ((State ?? "warn").Trim().ToLowerInvariant())
                {
                    case "pass": return CheckState.Pass;
                    case "fail": return CheckState.Fail;
                    default: return CheckState.Warn;
                }
            }
        }

        public bool Matches(ClientSession s)
        {
            if (string.IsNullOrEmpty(Match)) return false;
            string field = (Field ?? "any").Trim().ToLowerInvariant();
            string haystack =
                field == "directory" ? s.Directory :
                field == "name" ? s.Name :
                field == "build" ? s.Build.ToString() :
                (s.Directory ?? "") + " " + (s.Name ?? "");
            return haystack != null && haystack.IndexOf(Match, StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
