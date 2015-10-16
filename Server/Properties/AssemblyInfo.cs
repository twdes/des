using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using TecWare.DE.Server;
using TecWare.DE.Server.Configuration;

[assembly: AssemblyTitle("Data Exchange Server")]
[assembly: AssemblyDescription("Modular service host for CPS infrastructure")]
[assembly: AssemblyCulture("")]		

[assembly: AssemblyDelaySign(false)]

[assembly: DEConfigurationSchema(typeof(DEServer), "Configuration.DEScore.xsd")]
[assembly: DEConfigurationSchema(typeof(DEServer), "Configuration.DESconfigItem.xsd")]
[assembly: DEConfigurationSchema(typeof(DEServer), "Configuration.DEScron.xsd")]
[assembly: DEConfigurationSchema(typeof(DEServer), "Configuration.DESprocess.xsd")]
[assembly: DEConfigurationSchema(typeof(DEServer), "DES.xsd")]
[assembly: Guid("d5e8e8f8-3e7b-4ffa-8ec3-1860e24402e5")]

[assembly: InternalsVisibleTo("ServerTests")]
