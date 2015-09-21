using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using TecWare.DE.Server;
using TecWare.DE.Server.Configuration;

[assembly: AssemblyTitle("Data Exchange Server")]
[assembly: AssemblyDescription("Modular service host for CPS infrastructure")]
[assembly: AssemblyCulture("")]		

[assembly: AssemblyDelaySign(false)]

[assembly: DEConfigSchema(typeof(DEServer), "DES.xsd")]
[assembly: Guid("d5e8e8f8-3e7b-4ffa-8ec3-1860e24402e5")]

[assembly: InternalsVisibleTo("ServerTests, PublicKey=002400000480000094000000060200000024000052534131000400000100010081DE14D9E2BE8C03379445B98864BC9938225C042DC5FA201574DFB8ED46D069BE575B1E64B3A5BA63048E925E51AE1A93A645229E0B7E0A7C2A42E709B7FC39C57D756F4C9880D4613E56907D6BF1425E00647AF876F73A368B9ADC86D6189F7DD9FDD87DD1BF1F511E6EFC01B14EE1DE78DD5A631D4206FC4DBD96B6BAFDD6")]
