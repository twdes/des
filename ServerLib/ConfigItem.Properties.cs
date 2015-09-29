using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using Neo.IronLua;
using TecWare.DE.Stuff;

namespace TecWare.DE.Server
{
	#region -- interface IDEConfigItemProperty ------------------------------------------

	///////////////////////////////////////////////////////////////////////////////
	/// <summary>Wird von Eigenschaften, die zum Viewer übermittelt werden sollen
	/// implementiert.</summary>
	[DEListTypeProperty("property")]
	public interface IDEConfigItemProperty
	{
		/// <summary>Wird ausgelöst, wenn sich der Wert geändert hat.</summary>
		event EventHandler ValueChanged;

		[DEListTypeProperty("@name")]
		string Name { get; }
		[DEListTypeProperty("@displayname")]
		string DisplayName { get; }
		[DEListTypeProperty("@category")]
		string Category { get; }
		[DEListTypeProperty("@description")]
		string Description { get; }
		[DEListTypeProperty("@format")]
		string Format { get; }
		[DEListTypeProperty("@type")]
		Type Type { get; }
		[DEListTypeProperty(".")]
		object Value { get; }
	} // interface IDEConfigItemProperty

	#endregion

	#region -- interface IDEConfigItemPropertyService -----------------------------------

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

	#region -- class PropertyNameAttribute ----------------------------------------------

	///////////////////////////////////////////////////////////////////////////////
	/// <summary>Markiert Eigschaften des Konfigurationsknotens für den Export.</summary>
	[AttributeUsage(AttributeTargets.Property)]
	public class PropertyNameAttribute : Attribute
	{
		private string sName;

		public PropertyNameAttribute(string sName)
		{
			this.sName = sName;
		} // ctor

		public string Name { get { return sName; } }
	} // class PropertyNameAttribute

	#endregion

	#region -- class FormatAttribute ----------------------------------------------------

	///////////////////////////////////////////////////////////////////////////////
	/// <summary>Formatdefinition für die exportierte Eigenschaft.</summary>
	[AttributeUsage(AttributeTargets.Property)]
	public class FormatAttribute : Attribute
	{
		private string sFormat;

		public FormatAttribute(string sFormat)
		{
			this.sFormat = sFormat;
		} // ctor

		public string Format { get { return sFormat; } }
	} // class FormatAttribute

	#endregion

	#region -- class SimpleConfigItemProperty -------------------------------------------

	///////////////////////////////////////////////////////////////////////////////
	/// <summary></summary>
	public class SimpleConfigItemProperty<T> : IDEConfigItemProperty, IDisposable
	{
		public event EventHandler ValueChanged;

		private IDEConfigItemPropertyService service;
		private T value;

		public SimpleConfigItemProperty(IServiceProvider sp, string name, string displayName, string category, string description, string format, T value)
		{
			this.service = sp.GetService<IDEConfigItemPropertyService>();
			
			this.Name = name;
			this.DisplayName = displayName;
			this.Category = category;
			this.Description = description;
			this.Format = format;

			this.value = value;

			if (service != null)
				service.RegisterProperty(this);
		} // ctor

		public void Dispose()
		{
			if (service != null)
				service.UnregisterProperty(Name);
		} // proc Dispose

		public string Name { get; }
		public string DisplayName { get; }
		public string Category { get; }
		public string Description { get; }
		public string Format { get; }

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

	#region -- class DEConfigItem -------------------------------------------------------

	///////////////////////////////////////////////////////////////////////////////
	/// <summary></summary>
	public partial class DEConfigItem : IDEConfigItemPropertyService
	{
		#region -- class ConfigItemProperty -----------------------------------------------

		///////////////////////////////////////////////////////////////////////////////
		/// <summary></summary>
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
					format != null ? format.Format : null,
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
			{
				if (ValueChangedEvent != null)
					ValueChangedEvent(this, EventArgs.Empty);
			} // proc ValueChangedHandler

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

		#region -- Verwaltung der Eigenschaften -------------------------------------------

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

		public void RegisterPropertyDescriptor(PropertyDescriptor propertyDescriptor)
			=> RegisterProperty(new ConfigItemProperty(this, propertyDescriptor));

		public void RegisterPropertyDescriptor(string name, string format, PropertyDescriptor propertyDescriptor)
			=> RegisterProperty(new ConfigItemProperty(this, name, format, propertyDescriptor));

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
			var property = sender as IDEConfigItemProperty;
			if (property != null)
				FireEvent("tw_properties", property.Name, new XElement("value", (string)Lua.RtConvertValue(property.Value, typeof(string))));
		} // proc ValueChangedHandler

		#endregion

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
