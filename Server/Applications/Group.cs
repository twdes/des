using System;

namespace TecWare.DE.Server
{
	///////////////////////////////////////////////////////////////////////////////
	/// <summary></summary>
	internal sealed class DEGroup : DEConfigItem
	{
		public DEGroup(IServiceProvider sp, string name)
			: base(sp, name)
		{
		} // ctor

		protected override void OnBeginReadConfiguration(IDEConfigLoading config)
		{
			base.OnBeginReadConfiguration(config);

			((DEServer)Server).LoadExtensions(config, config.ConfigNew);
		} // proc OnBeginReadConfiguration

		public override string Icon { get { return "/images/folder.png"; } }
	} // class DEGroup
}
