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
