// Endpoint catalogue and speed test settings. The built-in copy of
// config/endpoints.json is embedded in the exe; a file of the same name next
// to the exe overrides it, so the list can change without a rebuild.
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Web.Script.Serialization;

namespace AvdHealthCheck
{
    public class AppConfig
    {
        public List<EndpointGroup> Groups { get; set; }
        public SpeedTestConfig SpeedTest { get; set; }
        public List<string> PublicIssuers { get; set; }
        public int? SlowResponseMs { get; set; }
        public PrismaConfig Prisma { get; set; }
        public TeamsConfig Teams { get; set; }
        public ClientAppConfig ClientApp { get; set; }
    }

    public class ClientAppConfig
    {
        public bool? Enabled { get; set; }
        public List<ClientRule> Rules { get; set; }   // empty = built-in rules (ClientDetector.DefaultRules)
    }

    public class PrismaConfig
    {
        // "PA Virtual Ethernet Adapter Secure" is the adapter the bank's Prisma Access Agent creates.
        public static readonly List<string> DefaultAdapterMatch = new List<string> { "PA Virtual Ethernet Adapter", "Prisma", "PANGP" };

        public bool? Enabled { get; set; }
        public string ServiceExe { get; set; }
        public List<string> AdapterMatch { get; set; }
    }

    public class TeamsConfig
    {
        public bool? Enabled { get; set; }
        public List<string> SlimCoreModes { get; set; }   // numeric VDI modes known to mean SlimCore, e.g. "5200"
        public List<string> WebRtcModes { get; set; }     // numeric VDI modes known to mean legacy WebRTC, e.g. "3100"
    }

    public class EndpointGroup
    {
        public string Name { get; set; }
        public List<EndpointDef> Endpoints { get; set; }
    }

    public class EndpointDef
    {
        public string Name { get; set; }
        public string Host { get; set; }
        public int? Port { get; set; }
        public string Type { get; set; }   // https | http | tcp | imds
        public string Path { get; set; }
        public bool? Required { get; set; }

        public string Kind { get { return (Type ?? "https").Trim().ToLowerInvariant(); } }

        public int EffectivePort
        {
            get
            {
                if (Port.HasValue && Port.Value > 0) return Port.Value;
                return Kind == "https" ? 443 : 80;
            }
        }

        public bool IsRequired { get { return !Required.HasValue || Required.Value; } }
    }

    public class SpeedTestConfig
    {
        public bool? Enabled { get; set; }
        public string Provider { get; set; }
        public string LatencyUrl { get; set; }
        public string DownloadUrl { get; set; }
        public string UploadUrl { get; set; }
        public int? UploadChunkBytes { get; set; }
        public int? Seconds { get; set; }
        public int? Streams { get; set; }
        public int? MaxDownloadMB { get; set; }
        public int? MaxUploadMB { get; set; }
        public List<SpeedProviderConfig> Providers { get; set; }

        /// <summary>Providers in the order to try. Older single-provider files (flat URLs) still work.</summary>
        public List<SpeedProviderConfig> EffectiveProviders()
        {
            if (Providers != null && Providers.Count > 0) return Providers;
            var single = new List<SpeedProviderConfig>();
            if (!string.IsNullOrWhiteSpace(DownloadUrl))
                single.Add(new SpeedProviderConfig
                {
                    Name = Provider, LatencyUrl = LatencyUrl, DownloadUrl = DownloadUrl,
                    UploadUrl = UploadUrl, UploadChunkBytes = UploadChunkBytes
                });
            return single;
        }
    }

    public class SpeedProviderConfig
    {
        public string Type { get; set; }               // "fixed" (default) | "librespeed"
        public string Name { get; set; }
        public string LatencyUrl { get; set; }
        public string DownloadUrl { get; set; }
        public string UploadUrl { get; set; }
        public int? UploadChunkBytes { get; set; }
        public string ServerList { get; set; }          // librespeed: JSON list of servers
        public List<string> PreferServers { get; set; } // librespeed: name fragments, in order of preference
        public List<string> FallbackServers { get; set; } // librespeed: base URLs if the list cannot be fetched

        public string Kind { get { return (Type ?? "fixed").Trim().ToLowerInvariant(); } }
    }

    public static class ConfigLoader
    {
        public const string FileName = "endpoints.json";

        /// <summary>Loads the override file if present and valid, otherwise the built-in list.</summary>
        public static AppConfig Load(out string source)
        {
            string external = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, FileName);
            if (File.Exists(external))
            {
                try
                {
                    AppConfig custom = Parse(File.ReadAllText(external));
                    source = "endpoints.json next to the exe";
                    return custom;
                }
                catch (Exception ex)
                {
                    source = "built-in list (endpoints.json next to the exe could not be read: " + ex.Message + ")";
                    return Parse(Resources.ReadText(FileName));
                }
            }

            source = "built-in list";
            return Parse(Resources.ReadText(FileName));
        }

        private static AppConfig Parse(string json)
        {
            var serializer = new JavaScriptSerializer();
            AppConfig config = serializer.Deserialize<AppConfig>(json);
            if (config == null || config.Groups == null || config.Groups.Count == 0)
                throw new InvalidDataException("no endpoint groups found");
            if (config.PublicIssuers == null) config.PublicIssuers = new List<string>();
            if (config.SpeedTest == null) config.SpeedTest = new SpeedTestConfig { Enabled = false };
            if (config.Prisma == null) config.Prisma = new PrismaConfig();
            if (config.Teams == null) config.Teams = new TeamsConfig();
            if (config.ClientApp == null) config.ClientApp = new ClientAppConfig();
            return config;
        }
    }

    public static class Resources
    {
        public static Stream Open(string name)
        {
            Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name);
            if (stream == null) throw new FileNotFoundException("Embedded resource missing: " + name);
            return stream;
        }

        public static string ReadText(string name)
        {
            using (var reader = new StreamReader(Open(name)))
                return reader.ReadToEnd();
        }
    }
}
