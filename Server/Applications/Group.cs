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

		public override string Icon { get { return "/images/folder.png"; } }
	} // class DEGroup
}
