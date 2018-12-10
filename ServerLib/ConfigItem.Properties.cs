#region -- copyright --
//
// Licensed under the EUPL, Version 1.1 or - as soon they will be approved by the
// European Commission - subsequent versions of the EUPL(the "Licence"); You may
// not use this work except in compliance with the Licence.
//
// You may obtain a copy of the Licence at:
// http://ec.europa.eu/idabc/eupl
//
// Unless required by applicable law or agreed to in writing, software distributed
// under the Licence is distributed on an "AS IS" basis, WITHOUT WARRANTIES OR
// CONDITIONS OF ANY KIND, either express or implied. See the Licence for the
// specific language governing permissions and limitations under the Licence.
//
#endregion
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using Neo.IronLua;
using TecWare.DE.Stuff;

namespace TecWare.DE.Server
{
	#region -- interface IDEConfigItemProperty ----------------------------------------

	/// <summary>Wird von Eigenschaften, die zum Viewer übermittelt werden sollen
	/// implementiert.</summary>
	[DEListTypeProperty("property")]
	public interface IDEConfigItemProperty
	{
		/// <summary>Wird ausgelöst, wenn sich der Wert geändert hat.</summary>
		event EventHandler ValueChanged;

		/// <summary></summary>
		[DEListTypeProperty("@name")]
		string Name { get; }
		/// <summary></summary>
		[DEListTypeProperty("@displayname")]
		string DisplayName { get; }
		/// <summary></summary>
		[DEListTypeProperty("@category")]
		string Category { get; }
		/// <summary></summary>
		[DEListTypeProperty("@description")]
		string Description { get; }
		/// <summary></summary>
		[DEListTypeProperty("@format")]
		string Format { get; }
		/// <summary></summary>
		[DEListTypeProperty("@type")]
		Type Type { get; }
		/// <summary></summary>
		[DEListTypeProperty(".")]
		object Value { get; }
	} // interface IDEConfigItemProperty

	#endregion

	#region -- interface IDEConfigItemPropertyService ---------------------------------

	/// <summary></summary>
	public interface IDEConfigItemPropertyService
	{
		/// <summary>Registriert eine .net Eigenschaft als DEServer Eigenschaft.</summary>
		/// <param name="propertyDescriptor"></param>
		void RegisterPropertyDescriptor(PropertyDescriptor propertyDescriptor);
		/// <summary>Registriert eine .net Eigenschaft als DEServer Eigenschaft.</summary>
		/// <param name="sName"></param>
		/// <param name="format"></param>
		/// <param name="propertyDescriptor"></param>
		void RegisterPropertyDescriptor(string sName, string format, PropertyDescriptor propertyDescriptor);
		/// <summary>Registriert eine Eigenschaft.</summary>
		/// <param name="property"></param>
		void RegisterProperty(IDEConfigItemProperty property);
		/// <summary>Entfernt die Eigenschaft aus der Auflistung.</summary>
		/// <param name="sId"></param>
		void UnregisterProperty(string sId);
	} // interface IDEConfigItemPropertyService

	#endregion

	#region -- class PropertyNameAttribute --------------------------------------------

	/// <summary>Markiert Eigschaften des Konfigurationsknotens für den Export.</summary>
	[AttributeUsage(AttributeTargets.Property)]
	public class PropertyNameAttribute : Attribute
	{
		private readonly string name;

		/// <summary></summary>
		/// <param name="name"></param>
		public PropertyNameAttribute(string name)
		{
			this.name = name;
		} // ctor

		/// <summary></summary>
		public string Name => name;
	} // class PropertyNameAttribute

	#endregion

	#region -- class FormatAttribute --------------------------------------------------

	/// <summary>Formatdefinition für die exportierte Eigenschaft.</summary>
	[AttributeUsage(AttributeTargets.Property)]
	public class FormatAttribute : Attribute
	{
		private readonly string format;

		/// <summary></summary>
		/// <param name="format"></param>
		public FormatAttribute(string format)
		{
			this.format = format;
		} // ctor

		/// <summary></summary>
		public string Format => format;
	} // class FormatAttribute

	#endregion

	#region -- class SimpleConfigItemProperty -----------------------------------------

	/// <summary></summary>
	public class SimpleConfigItemProperty<T> : IDEConfigItemProperty, IDisposable
	{
		/// <summary></summary>
		public event EventHandler ValueChanged;

		private readonly IDEConfigItemPropertyService service;
		private T value;

		/// <summary></summary>
		/// <param name="sp"></param>
		/// <param name="name"></param>
		/// <param name="displayName"></param>
		/// <param name="category"></param>
		/// <param name="description"></param>
		/// <param name="format"></param>
		/// <param name="value"></param>
		public SimpleConfigItemProperty(IServiceProvider sp, string name, string displayName, string category, string description, string format, T value)
		{
			this.service = sp.GetService<IDEConfigItemPropertyService>();

			if (String.IsNullOrEmpty(name))
				throw new ArgumentNullException("name");

			this.Name = name;
			this.DisplayName = displayName ?? name;
			this.Category = category ?? "Misc";
			this.Description = description;
			this.Format = format;

			this.value = value;

			if (service != null)
				service.RegisterProperty(this);
		} // ctor

		/// <summary></summary>
		public void Dispose()
		{
			if (service != null)
				service.UnregisterProperty(Name);
		} // proc Dispose

		/// <summary></summary>
		public string Name { get; }
		/// <summary></summary>
		public string DisplayName { get; }
		/// <summary></summary>
		public string Category { get; }
		/// <summary></summary>
		public string Description { get; }
		/// <summary></summary>
		public string Format { get; }

		/// <summary></summary>
		public T Value
		{
			get { return value; }
			set
			{
				if (!EqualityComparer<T>.Default.Equals(this.value, value))
				{
					this.value = value;
					ValueChanged?.Invoke(this, EventArgs.Empty);
				}
			}
		} // prop Value

		Type IDEConfigItemProperty.Type => typeof(T);
		object IDEConfigItemProperty.Value => value;
	} // class SimpleConfigItemProperty

	#endregion

	#region -- class DEConfigItem -----------------------------------------------------

	public partial class DEConfigItem : IDEConfigItemPropertyService
	{
		#region -- class ConfigItemProperty -------------------------------------------

		private sealed class ConfigItemProperty : IDEConfigItemProperty
		{
			private event EventHandler ValueChangedEvent;

			private DEConfigItem configItem;
			private PropertyDescriptor property;

			private EventHandler valueChangedHandler;

			public ConfigItemProperty(DEConfigItem configItem, PropertyDescriptor property)
			{
				var name = (PropertyNameAttribute)property.Attributes[typeof(PropertyNameAttribute)];
				var format = (FormatAttribute)property.Attributes[typeof(FormatAttribute)];

				Init(configItem,
					name != null && !String.IsNullOrEmpty(name.Name) ? Name = name.Name : property.ComponentType.Name + "_" + property.Name,
					format?.Format,
					property
				);
			} // ctor

			public ConfigItemProperty(DEConfigItem configItem, string name, string format, PropertyDescriptor property)
			{
				Init(configItem, name, format, property);
			} // ctor

			private void Init(DEConfigItem configItem, string name, string format, PropertyDescriptor property)
			{
				this.configItem = configItem;
				this.property = property;

				this.Name = name;
				this.Format = format;

				this.property = property;

				valueChangedHandler = ValueChangedHandler;
			} // proc Init

			private void ValueChangedHandler(object sender, EventArgs e)
				=> ValueChangedEvent?.Invoke(this, EventArgs.Empty);

			public string Name { get; private set; }
			public string DisplayName => property.DisplayName;
			public string Category => property.Category;
			public string Description => property.Description;
			public string Format { get; private set; }
			public Type Type => property.PropertyType;
			public object Value => property.GetValue(configItem);

			public event EventHandler ValueChanged
			{
				add
				{
					property.AddValueChanged(configItem, valueChangedHandler);
					ValueChangedEvent += value;
				}
				remove
				{
					ValueChangedEvent -= value;
					property.RemoveValueChanged(configItem, valueChangedHandler);
				}
			} // event ValueChanged
		} // class ConfigItemProperty

		#endregion

		private EventHandler valueChangedHandler;

		#region -- Verwaltung der Eigenschaften ---------------------------------------

		/// <summary>Initialisiert die Eigenschaften, an einem Knoten.</summary>
		private void InitTypeProperties()
		{
			valueChangedHandler = ValueChangedHandler;

			var cdc = ConfigDescriptionCache.Get(GetType());
			if (cdc != null)
			{
				foreach (var pi in cdc.Properties)
					RegisterProperty(new ConfigItemProperty(this, pi));
			}
		} // proc InitTypeProperties

		private int FindPropertyIndex(string name) => properties.FindIndex(p => String.Compare(p.Name, name, StringComparison.OrdinalIgnoreCase) == 0);

		/// <summary></summary>
		/// <param name="propertyDescriptor"></param>
		public void RegisterPropertyDescriptor(PropertyDescriptor propertyDescriptor)
			=> RegisterProperty(new ConfigItemProperty(this, propertyDescriptor));

		/// <summary></summary>
		/// <param name="name"></param>
		/// <param name="format"></param>
		/// <param name="propertyDescriptor"></param>
		public void RegisterPropertyDescriptor(string name, string format, PropertyDescriptor propertyDescriptor)
			=> RegisterProperty(new ConfigItemProperty(this, name, format, propertyDescriptor));

		/// <summary></summary>
		/// <typeparam name="T"></typeparam>
		/// <param name="name"></param>
		/// <param name="displayName"></param>
		/// <param name="category"></param>
		/// <param name="description"></param>
		/// <param name="format"></param>
		/// <param name="value"></param>
		/// <returns></returns>
		[LuaMember(nameof(RegisterProperty))]
		public IDEConfigItemProperty RegisterProperty<T>(string name, string displayName, string category, string description, string format, T value)
			=> new SimpleConfigItemProperty<T>(this, name, displayName ?? name, category ?? "Misc", description, format, value);

		/// <summary></summary>
		/// <param name="property"></param>
		public void RegisterProperty(IDEConfigItemProperty property)
		{
			using (properties.EnterWriteLock())
			{
				if (FindPropertyIndex(property.Name) == -1)
				{
					property.ValueChanged += valueChangedHandler;
					properties.Add(property);
				}
			}
		} // proc RegisterProperty

		/// <summary></summary>
		/// <param name="name"></param>
		public void UnregisterProperty(string name)
		{
			using (properties.EnterWriteLock())
			{
				var index = FindPropertyIndex(name);
				if (index != -1)
				{
					properties[index].ValueChanged -= valueChangedHandler;
					properties.RemoveAt(index);
				}
			}
		} // proc UnregisterProperty

		private void ValueChangedHandler(object sender, EventArgs e)
		{
			if (sender is IDEConfigItemProperty property)
				FireSysEvent("tw_properties", property.Name, new XElement("value", (string)Lua.RtConvertValue(property.Value, typeof(string))));
		} // proc ValueChangedHandler

		#endregion

		/// <summary></summary>
		/// <typeparam name="T"></typeparam>
		/// <param name="value"></param>
		/// <param name="newValue"></param>
		/// <param name="sPropertyName"></param>
		protected void SetProperty<T>(ref T value, T newValue, [CallerMemberName] string sPropertyName = null)
		{
			if (!EqualityComparer<T>.Default.Equals(value, newValue))
			{
				value = newValue;
				OnPropertyChanged(sPropertyName);
			}
		} // proc SetProperty
	} // class DEConfigItem

	#endregion
}
