using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TecWare.DE.Server.Configuration;
using TecWare.DE.Stuff;

namespace TecWare.DE.Server
{
	[TestClass]
	public class ConfigurationTests
	{
		[TestMethod]
		public void ReplaceEnv()
		{
			var expected = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), @"..\test.txt");
      var actual = ProcsDE.GetEnvironmentPath(@"%executedirectory%\..\test.txt");
			Assert.AreEqual(expected, actual);
    }

		[TestMethod]
		public void LoadAssemblies()
		{
			var service = new SimpleServiceProvider();
			var cs = new DEConfigurationService(service, @"..\..\Files\01_Main.xml", new DE.Stuff.PropertyDictionary());
			service.Add(typeof(IDEConfigurationService), cs);

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
			Assert.AreEqual("3670016", a1.DefaultValue);
    }

		[TestMethod]
		public void MergeConfigurations()
		{
			var service = new SimpleServiceProvider();
			var cs = new DEConfigurationService(service, @"..\..\Files\01_Main.xml", new DE.Stuff.PropertyDictionary());
			service.Add(typeof(IDEConfigurationService), cs);
			cs.UpdateSchema(Assembly.LoadFile(Path.GetFullPath(@"..\..\..\Server\bin\Debug\DEServer.exe")));

			var x = cs.ParseConfiguration();

			Console.WriteLine(x.ToString());

			// tests

			var p = x.Element(DEConfigurationConstants.xnServer)?.Attribute("logpath")?.Value;
			Assert.IsTrue(Path.IsPathRooted(p));

			var c1 = x.Elements(DEConfigurationConstants.MainNamespace + "configLogItem").First();
			Assert.IsNotNull(c1);
			Assert.AreEqual("test 1", c1.Attribute("displayname")?.Value);
			Assert.AreEqual("neu", c1.Attribute("icon")?.Value);

			var l1 = c1.Element(DEConfigurationConstants.xnLog);
			Assert.IsNotNull(l1);
			Assert.AreEqual("4096", l1.Attribute("min")?.Value);
			Assert.AreEqual("8128", l1.Attribute("max")?.Value);

			var c2 = x.Elements(DEConfigurationConstants.MainNamespace + "configLogItem").Skip(1).First();
			Assert.IsNotNull(c2);
			Assert.AreEqual("test 2", c2.Attribute("displayname")?.Value);
			Assert.AreEqual("script1 script2 script3", c2.Attribute("script")?.Value);
      var l2 = c2.Element(DEConfigurationConstants.xnLog);
			Assert.IsNotNull(l2);
			Assert.AreEqual("4096", l2.Attribute("min")?.Value);
			Assert.AreEqual("8128", l2.Attribute("max")?.Value);
		}
	} // class ConfigurationTest
}
