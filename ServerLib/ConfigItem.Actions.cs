using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Net;
using System.Reflection;
using Neo.IronLua;
using TecWare.DE.Server.Http;
using TecWare.DE.Stuff;

namespace TecWare.DE.Server
{
	#region -- delegate DEConfigActionDelegate ------------------------------------------

	/// <summary></summary>
	/// <param name="item"></param>
	/// <param name="args"></param>
	/// <returns></returns>
	public delegate object DEConfigActionDelegate(DEConfigItem item, IDEHttpContext context);

	#endregion

	#region -- class DEConfigAction -----------------------------------------------------

	///////////////////////////////////////////////////////////////////////////////
	/// <summary></summary>
	public sealed class DEConfigAction
	{
		private string securityToken;
		private string description;
		private MethodInfo methodDescription;
		private DEConfigActionDelegate action;
		private bool isNativeCall;
		private bool isSafeCall;

		public DEConfigAction(string securityToken, string description, DEConfigActionDelegate action, bool isSafeCall, MethodInfo methodDescription)
		{
			this.securityToken = securityToken;
			this.description = description;
			this.action = action;
			this.isNativeCall = methodDescription == null ? false : Array.Exists(methodDescription.GetParameters(), p => p.ParameterType == typeof(IDEHttpContext));
			this.isSafeCall = isNativeCall ? false : isSafeCall;
			this.methodDescription = methodDescription;
		} // ctor

		public object Invoke(DEConfigItem item, IDEHttpContext context)
		{
			if (action != null)
				if (isNativeCall)
				{
					action(item, context);
					return DBNull.Value;
				}
				else
					return action(item, context);
			else
				return null;
		} // proc Invoke

		public string Description { get { return description; } }
		public MethodInfo MethodDescription { get { return methodDescription; } }
		public string SecurityToken { get { return securityToken; } }
		public bool IsSafeCall { get { return isSafeCall; } }

		public bool IsEmpty { get { return action == null; } }

		// -- Static --------------------------------------------------------------

		private static readonly DEConfigAction empty = new DEConfigAction(null, null, null, false, null);

		public static DEConfigAction Empty { get { return empty; } }
	} // class DEConfigAction

	#endregion

	#region -- class DEConfigItem -------------------------------------------------------

	public partial class DEConfigItem
	{
		#region -- struct ConfigAction ----------------------------------------------------

		///////////////////////////////////////////////////////////////////////////////
		/// <summary></summary>
		private struct ConfigAction
		{
			public string Description;
			public DEConfigHttpActionAttribute Attribute;
			public MethodInfo Method;
		} // struct ConfigAction

		#endregion

		#region -- class ConfigDescriptionCache -------------------------------------------

		///////////////////////////////////////////////////////////////////////////////
		/// <summary>Sichert die Aktionen die innerhalb einer Klasse gefunden wurden.</summary>
		private sealed class ConfigDescriptionCache
		{
			private ConfigDescriptionCache prev;
			private ConfigAction[] actions;
			private PropertyDescriptor[] properties;

			public ConfigDescriptionCache(ConfigDescriptionCache prev, Type type)
			{
				this.prev = prev;
				
				// Suche alle Actions
				actions =
					(
						from mi in type.GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.InvokeMethod)
						let attr = mi.GetCustomAttribute<DEConfigHttpActionAttribute>()
						let attrDesc = mi.GetCustomAttribute<DescriptionAttribute>()
						where attr != null && mi.DeclaringType == type
						select new ConfigAction { Description = attrDesc == null ? String.Empty : attrDesc.Description, Attribute = attr, Method = mi }
					).ToArray();

				// Suche alle Eigenschaften
				properties =
					(
						from pi in TypeDescriptor.GetProperties(type).Cast<PropertyDescriptor>()
						let name = (PropertyNameAttribute)pi.Attributes[typeof(PropertyNameAttribute)]
						where name != null
						select pi
					).ToArray();
			} // ctor

			public bool GetConfigAction(string sAction, out ConfigAction action)
			{
				// Suuche die Action
				if (actions != null)
				{
					int iIdx = Array.FindIndex(actions, cur => String.Compare(cur.Attribute.ActionName, sAction, true) == 0);
					if (iIdx >= 0)
					{
						action = actions[iIdx];
						return true;
					}
				}

				if (prev != null)
					return prev.GetConfigAction(sAction, out action);
				else
				{
					action = new ConfigAction();
					return false;
				}
			} // func GetConfigAction

			public ConfigAction[] Actions { get { return actions; } }
			public PropertyDescriptor[] Properties { get { return properties; } }
			public ConfigDescriptionCache Previous { get { return prev; } }

			// -- Static ------------------------------------------------------------

			private static Dictionary<Type, ConfigDescriptionCache> configDescriptors = new Dictionary<Type, ConfigDescriptionCache>();

			public static ConfigDescriptionCache Get(Type type)
			{
				if (!typeof(DEConfigItem).IsAssignableFrom(type))
					return null;

				lock (configDescriptors)
				{
					ConfigDescriptionCache ret;
					if (configDescriptors.TryGetValue(type, out ret))
						return ret;
					else
						return configDescriptors[type] = new ConfigDescriptionCache(Get(type.BaseType), type);
				}
			} // func Get

		} // class ConfigDescriptionCache

		#endregion

		#region -- class ConfigActionListDescriptor ---------------------------------------

		///////////////////////////////////////////////////////////////////////////////
		/// <summary></summary>
		private sealed class ConfigActionListDescriptor : IDEListDescriptor
		{
			private ConfigActionListDescriptor()
			{
			} // ctor

			public void WriteType(DEListTypeWriter xml)
			{
				xml.WriteStartType("argument");
				xml.WriteProperty("@name", typeof(string));
				xml.WriteProperty("@type", typeof(Type));
				xml.WriteProperty(".", typeof(string));
				xml.WriteEndType();
				xml.WriteStartType("action");
				xml.WriteProperty("@id", typeof(string));
				xml.WriteProperty("@description", typeof(string));
				xml.WriteProperty("@safecall", typeof(bool));
				xml.WriteProperty("@security", typeof(string));
				xml.WriteProperty("arguments", "argument[]");
				xml.WriteProperty("@return", typeof(string));
				xml.WriteEndType();
			} // proc WriteType

			public void WriteItem(DEListItemWriter xml, object item)
			{
				var caItem = (KeyValuePair<string, DEConfigAction>)item;
				var ca = caItem.Value;

				xml.WriteStartProperty("action");

				xml.WriteAttributeProperty("id", caItem.Key);
				xml.WriteAttributeProperty("description", ca.Description);
				xml.WriteAttributeProperty("safecall", ca.IsSafeCall.ToString());
				xml.WriteAttributeProperty("security", ca.SecurityToken);
				xml.WriteAttributeProperty("return", ca.MethodDescription.ReturnType.ToString());

				xml.WriteStartProperty("arguments");
				foreach (var p in ca.MethodDescription.GetParameters())
				{
					xml.WriteStartProperty("argument");
					xml.WriteAttributeProperty("name", p.Name);
					xml.WriteAttributeProperty("type", LuaType.GetType(p.ParameterType).AliasOrFullName);
					if (p.DefaultValue != null)
						xml.WriteValue(p.DefaultValue);
					xml.WriteEndProperty();
				}
				xml.WriteEndProperty();
				xml.WriteEndProperty();
			} // proc WriteItem

			private static readonly ConfigActionListDescriptor configActionListDescriptor = new ConfigActionListDescriptor();

			public static ConfigActionListDescriptor Instance { get { return configActionListDescriptor; } }
		} // class ConfigActionListDescriptor 

		#endregion

		#region -- class ConfigActionDictionary -------------------------------------------

		///////////////////////////////////////////////////////////////////////////////
		/// <summary></summary>
		private sealed class ConfigActionDictionary : DEListControllerBase, IEnumerable<KeyValuePair<string, DEConfigAction>>
		{
			private Dictionary<string, DEConfigAction> actions = new Dictionary<string, DEConfigAction>(EqualityComparer<string>.Default);

			#region -- Ctor/Dtor ------------------------------------------------------------

			public ConfigActionDictionary(DEConfigItem configItem)
				: base(configItem, ConfigActionListDescriptor.Instance, ActionsListId, "Actions")
			{
			} // ctor

			#endregion

			#region -- GetEnumerator --------------------------------------------------------

			public IEnumerator<KeyValuePair<string, DEConfigAction>> GetEnumerator()
			{
				return actions.GetEnumerator();
			} // func GetEnumerator

			System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() { return GetEnumerator(); }

			#endregion

			public void Clear()
			{
				using (EnterWriteLock())
					actions.Clear();
			} // proc Clear

			public bool Contains(string sActionId)
			{
				using (EnterReadLock())
					return actions.ContainsKey(sActionId);
			} // func Contains

			public override void OnBeforeList()
			{
				ConfigItem.CollectActions(); // Sammle alle Aktions
			} // proc OnBeforeList

			public DEConfigAction this[string sActionId]
			{
				get
				{
					using (EnterReadLock())
					{
						DEConfigAction action;
						return actions.TryGetValue(sActionId, out action) ? action : null;
					}
				}
				set
				{
					using (EnterWriteLock())
						actions[sActionId] = value;
				}
			} // prop this

			public override System.Collections.IEnumerable List { get { return this; } }
		} // class ConfigActionDictionary

		#endregion
		
		#region -- Actions, Attached Script -----------------------------------------------

		private void AttachedScriptCompiled(object sender, EventArgs e)
		{
			// Lösche alle bisher aufgelösten Aktionen und baue Sie neu wieder auf
			actions.Clear();
		} // proc AttachedScriptCompiled

		/// <summary>Sammelt alle für diesen Knoten vorhande Aktionen.</summary>
		protected virtual void CollectActions()
		{
			// Suche alle Type Actions
			CompileTypeActions(ConfigDescriptionCache.Get(GetType()));

			// Suche alle Lua Actions
			var table = GetActionTable();
			if (table != null)
			{
				foreach (var c in table)
				{
					string sAction = c.Key as string;
					if (sAction == null || actions.Contains(sAction))
						continue;

					actions[sAction] = CompileLuaAction(sAction, c.Value as LuaTable);
				}
			}
		} // proc CollectActions

		/// <summary>Führt eine Aktion aus.</summary>
		/// <param name="actionName">Name der Aktion</param>
		/// <param name="context">Parameter, die übergeben werden sollen.</param>
		/// <returns>Rückgabe</returns>
		public object InvokeAction(string actionName, IDEHttpContext context)
		{
			// Suche die Action im Cache
			DEConfigAction a;
			lock (actions)
			{
				a = actions[actionName];
				if (a == null) // Beziehungsweise erzeuge sie
				{
					a = CompileAction(actionName);
					if (a == null)
						a = DEConfigAction.Empty;
					actions[actionName] = a;
				}
			}

			// Führe die Aktion aus
			try
			{
				if (a == DEConfigAction.Empty)
					throw new HttpResponseException(HttpStatusCode.BadRequest, String.Format("Action {0} not found", actionName));

				context.DemandToken(a.SecurityToken);
				return a.Invoke(this, context);
			}
			catch (Exception e)
			{
				if (!a.IsSafeCall || context.IsOutputStarted || (e is HttpResponseException)) // Antwort kann nicht mehr gesendet werden
					throw;

				// Meldung protokollieren
				Log.LogMsg(LogMsgType.Error, e.GetMessageString());
				return CreateDefaultXmlReturn(false, e.Message);
			}
		} // func InvokeAction

		private DEConfigAction CompileTypeAction(string sAction)
		{
			ConfigDescriptionCache cac = ConfigDescriptionCache.Get(GetType());
			if (cac == null)
				return DEConfigAction.Empty;

			ConfigAction ca;
			if (cac.GetConfigAction(sAction, out ca))
				return CompileTypeAction(ref ca);
			else
				return DEConfigAction.Empty;
		} // func CompileTypeAction

		private void CompileTypeActions(ConfigDescriptionCache cac)
		{
			if (cac == null)
				return;

			// Vorgänger bearbeiten
			CompileTypeActions(cac.Previous);

			if (cac.Actions != null)
				for (int i = 0; i < cac.Actions.Length; i++)
				{
					if (!actions.Contains(cac.Actions[i].Attribute.ActionName))
						actions[cac.Actions[i].Attribute.ActionName] = CompileTypeAction(ref cac.Actions[i]);
				}
		} // proc CompileTypeActions

		private DEConfigAction CompileTypeAction(ref ConfigAction ca)
		{
			var exprLambda = CompileMethodAction(ca.Method);

			// Erzeuge die Action
			return new DEConfigAction(ca.Attribute.SecurityToken, ca.Description, exprLambda.Compile(), ca.Attribute.IsSafeCall, ca.Method);
		} // func CompileTypeAction

		private DEConfigAction CompileLuaAction(string sAction)
		{
			// Table mit den Aktions
			var table = GetActionTable();
			if (table == null)
				return DEConfigAction.Empty;

			// Erzeuge die Aktion
			return CompileLuaAction(sAction, table[sAction] as LuaTable);
		} // func CompileLuaAction

		private DEConfigAction CompileLuaAction(string sAction, LuaTable ca)
		{
			if (sAction == null || ca == null)
				return DEConfigAction.Empty;

			Delegate dlg = ca["Method"] as Delegate;
			if (dlg == null)
				return DEConfigAction.Empty;

			return new DEConfigAction(
				ca.GetOptionalValue<string>("Security", null),
				ca.GetOptionalValue<string>("Description", null),
				CompileMethodAction(dlg.Method, dlg).Compile(),
				ca.GetOptionalValue("SafeCall", true),
				dlg.Method
			);
		} // func CompileLuaAction

		protected virtual DEConfigAction CompileAction(string actionName)
		{
			DEConfigAction action;
			if ((action = CompileTypeAction(actionName)) != null && !action.IsEmpty)
				return action;
			else if ((action = CompileLuaAction(actionName)) != null && !action.IsEmpty)
				return action;
			else
				return DEConfigAction.Empty;
		} // proc CompileAction

		private Expression<DEConfigActionDelegate> CompileMethodAction(MethodInfo method, Delegate dlg = null)
		{
			var argThis = Expression.Parameter(typeof(DEConfigItem), "#this");
			var argCaller = Expression.Parameter(typeof(IDEHttpContext), "#arg");

			int parameterOffset;
			ParameterInfo[] parameterInfo;
			if (dlg != null)
			{
				var miInvoke = dlg.GetType().GetMethod("Invoke");
				var miMethod = dlg.Method;

				parameterInfo = miMethod.GetParameters();
				parameterOffset = parameterInfo.Length - miInvoke.GetParameters().Length;
			}
			else
			{
				parameterInfo = method.GetParameters();
				parameterOffset = 0;
			}

			// Gibt es einen InputStream-Parameter
			ParameterExpression inputStream = null;
			Expression exprGetExtraData = null;
			Expression exprFinallyExtraData = null;

			var inputStreamIndex = Array.FindIndex(parameterInfo, c => c.ParameterType == typeof(StreamWriter) || c.ParameterType == typeof(Stream));
			if (inputStreamIndex >= 0)
				throw new NotImplementedException();

			// Generiere das Parameter Mapping
			var exprParameter = CreateArgumentExpressions(argCaller, argThis, parameterOffset, parameterInfo, inputStream);

			// Erzeuge den Call auf die Action
			var exprCall = dlg == null ?
				(Expression)Expression.Call(Expression.Convert(argThis, method.DeclaringType), method, exprParameter) :
				(Expression)Expression.Invoke(Expression.Constant(dlg), exprParameter);

			if (inputStream != null)
				exprCall = Expression.Block(new ParameterExpression[] { inputStream }, exprGetExtraData, Expression.TryFinally(exprCall, exprFinallyExtraData));

			// Werte den Rückgabewert aus
			Expression<DEConfigActionDelegate> exprLambda;
			var returnValue = method.ReturnType == typeof(void) ? null : Expression.Variable(method.ReturnType, "#return");
			if (returnValue == null)
			{
				exprLambda = Expression.Lambda<DEConfigActionDelegate>(
					Expression.Block(exprCall, Expression.Default(typeof(object))),
					true, argThis, argCaller);
			}
			else
			{
				exprLambda = Expression.Lambda<DEConfigActionDelegate>(
					Expression.Block(exprCall),
					true, argThis, argCaller
				);
			}
			return exprLambda;
		} // func CompileMethodAction

		private Expression[] CreateArgumentExpressions(ParameterExpression arg, ParameterExpression argThis, int parameterInfoOffset, ParameterInfo[] parameterInfo, ParameterExpression extraData)
		{
			var r = new Expression[parameterInfo.Length - parameterInfoOffset];

			// Create the parameter
			for (int i = parameterInfoOffset; i < parameterInfo.Length; i++)
			{
				var p = parameterInfo[i];
				var typeTo = p.ParameterType;
				var typeCode = Type.GetTypeCode(typeTo);
				Expression exprGetParameter;

				if (typeTo == typeof(object)) // Keine Konvertierung
				{
					exprGetParameter = Expression.Convert(Expression.Call(arg, miGetProperty, Expression.Constant(p.Name), Expression.Default(typeof(string))), typeof(object));
				}
				else if (typeCode == TypeCode.Object) // Gibt keine Default-Werte, ermittle den entsprechenden TypeConverter
				{
					if (typeTo == typeof(IDEHttpContext))
					{
						exprGetParameter = Expression.Condition(
							Expression.TypeIs(arg, typeof(IDEHttpContext)),
							Expression.Convert(arg, typeof(IDEHttpContext)),
							Expression.Throw(Expression.New(typeof(ArgumentException).GetConstructor(new Type[] { typeof(string) }), Expression.Constant("NativeCall expects a IDEHttpContext argument.")), typeof(IDEHttpContext))
						);
					}
					else if (typeTo.IsAssignableFrom(GetType()))
						exprGetParameter = argThis;
					else if (typeTo == typeof(TextReader)) // Input-Datenstrom (Text)
					{
						if (extraData.Type != typeof(TextReader))
							throw new ArgumentException("Can not bind extra data to call.");
						exprGetParameter = extraData;
					}
					else if (typeTo == typeof(Stream)) // Input-Datenstrom (Binär)
					{
						if (extraData.Type != typeof(Stream))
							throw new ArgumentException("Can not bind extra data to call.");
						exprGetParameter = extraData;
					}
					else
					{
						var conv = TypeDescriptor.GetConverter(typeTo);

						exprGetParameter =
							Expression.Convert(
								Expression.Call(Expression.Constant(conv), miConvertFromInvariantString,
								Expression.Call(arg, miGetProperty, Expression.Constant(p.Name), Expression.Default(typeof(string)))
							),
							typeTo
						);
					}
				}
				else if (typeCode == TypeCode.String) // String gibt es nix zu tun
				{
					exprGetParameter = Expression.Call(arg, miGetProperty, Expression.Constant(p.Name), Expression.Constant(p.DefaultValue == DBNull.Value ? null : p.DefaultValue, typeof(string)));
				}
				else // Standardtype
				{
					// ToType - Konverter
					exprGetParameter = Expression.Call(FindConvertMethod(typeTo),
						Expression.Call(arg, miGetProperty, Expression.Constant(p.Name), Expression.Constant(Convert.ToString(p.DefaultValue, CultureInfo.InvariantCulture))),
						Expression.Property(null, piInvariantCulture)
				 );
				}

				r[i - parameterInfoOffset] = exprGetParameter;
			}

			return r;
		} // func CreateArgumentExpressions

		private static MethodInfo FindConvertMethod(Type typeTo)
		{
			foreach (MethodInfo mi in typeof(Convert).GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.InvokeMethod))
			{
				if (mi.ReturnType == typeTo)
				{
					ParameterInfo[] p = mi.GetParameters();
					if (p.Length == 2 && p[0].ParameterType == typeof(string) && p[1].ParameterType == typeof(IFormatProvider))
						return mi;
				}
			}
			throw new ArgumentException(String.Format("Kein Konverter für '{0}' gefunden.", typeTo));
		} // func FindConvertMethod

		private LuaTable GetActionTable()
		{
			return this.GetMemberValue(LuaActions, lRawGet: true) as LuaTable;
		} // func GetActionTable

		#endregion
	} // class DEConfigItem

	#endregion
}
