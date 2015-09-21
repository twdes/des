using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TecWare.DE.Server.Configuration
{
	#region -- class DEConfigSchemaAttribute --------------------------------------------

	///////////////////////////////////////////////////////////////////////////////
	/// <summary></summary>
	[AttributeUsage(AttributeTargets.Assembly)]
	public class DEConfigSchemaAttribute : Attribute
	{
		/// <summary>Marks a manifest-resource as a schema extension.</summary>
		/// <param name="baseType"></param>
		/// <param name="sResourceId"></param>
		public DEConfigSchemaAttribute(Type baseType, string resourceId)
		{
			this.BaseType = baseType;
			this.ResourceId = resourceId;
		} // ctor

		public Type BaseType { get; }
		public string ResourceId { get; }
	} // class DEConfigSchemaAttribute

	#endregion
}
