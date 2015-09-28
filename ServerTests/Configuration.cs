using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TecWare.DE.Server.Configuration;

namespace TecWare.DE.Server
{
	[TestClass]
	public class ConfigurationTests
	{
		[TestMethod]
		public void LoadAssemblies()
		{
			var cs = new DEConfigurationService(new SimpleServiceProvider(), @"..\..\Files\LoadAssemblies.xml");
			cs.UpdateSchema(Assembly.LoadFile(Path.GetFullPath(@"..\..\..\Server\bin\Debug\DEServer.exe")));

			var n = cs[DEConfigurationConstants.MainNamespace + "configLogItem"];
			Assert.IsNotNull(n);
			Assert.AreEqual("configLogItem", n.Name.LocalName);
			Assert.AreEqual(typeof(DEConfigLogItem), n.ClassType);

			foreach (var c in n.GetAttributes())
				Console.WriteLine($"Attribute[{c.IsPrimaryKey}]: {c.Name.LocalName} : {c.TypeName} [{c.Type}]");

			var a2 = n.GetAttributes().FirstOrDefault(c => c.Name == "script");
			Assert.IsNotNull(a2);
			Assert.IsTrue(a2.IsList);

			foreach (var c in n.GetElements())
				Console.WriteLine($"Element: {c.Name.LocalName}");

			var n2 = n.GetElements().FirstOrDefault(c => c.Name == DEConfigurationConstants.MainNamespace + "log");
      Assert.IsNotNull(n2);
			Assert.IsNull(n2.ClassType);
			Assert.IsNotNull(n2.Documentation);

			var a1 = n2.GetAttributes().FirstOrDefault(c => c.Name == "min");
			Assert.IsNotNull(a1);
			Assert.AreEqual(typeof(uint), a1.Type);
			Assert.AreEqual((uint)3670016, a1.DefaultValue);
    }
	} // class ConfigurationTest
}
