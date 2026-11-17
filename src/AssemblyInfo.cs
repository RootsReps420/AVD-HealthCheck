using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

// Without this attribute the in-box compiler produces an exe that runs in .NET 4.0
// compatibility mode, which disables TLS 1.2 by default. Declaring 4.8 turns on the
// modern defaults (TLS settings follow the OS).
[assembly: TargetFramework(".NETFramework,Version=v4.8", FrameworkDisplayName = ".NET Framework 4.8")]

[assembly: AssemblyTitle("AVD Health Check")]
[assembly: AssemblyDescription("Connectivity, device and speed checks for Azure Virtual Desktop session hosts")]
[assembly: AssemblyCompany("Lloyds Banking Group - Virtual Team")]
[assembly: AssemblyProduct("AVD Health Check")]
[assembly: AssemblyCopyright("Lloyds Banking Group")]
[assembly: AssemblyVersion("1.1.0.0")]
[assembly: AssemblyFileVersion("1.1.0.0")]
[assembly: ComVisible(false)]
