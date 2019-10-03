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
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Net;
using System.Reflection;
using Neo.IronLua;
using TecWare.DE.Networking;
using TecWare.DE.Server.Http;
using TecWare.DE.Stuff;

namespace TecWare.DE.Server
{
	#region -- delegate DEConfigActionDelegate ----------------------------------------

	/// <summary></summary>
	/// <param name="item"></param>
	/// <param name="context"></param>
	/// <returns></returns>
	public delegate object DEConfigActionDelegate(DEConfigItem item, IDEWebRequestScope context);

	#endregion

	#region -- class DEConfigAction ---------------------------------------------------

	/// <summary></summary>
	public sealed class DEConfigAction
	{
		private readonly string securityToken;
		private readonly string description;
		private readonly MethodInfo methodDescription;
		private readonly DEConfigActionDelegate action;
		private readonly bool isNativeCall;
		private readonly bool isSafeCall;

		/// <summary></summary>
		/// <param name="securityToken"></param>
		/// <param name="description"></param>
		/// <param name="action"></param>
		/// <param name="isSafeCall"></param>
		/// <param name="methodDescription"></param>
		public DEConfigAction(string securityToken, string description, DEConfigActionDelegate action, bool isSafeCall, MethodInfo methodDescription)
		{
			this.securityToken = securityToken;
			this.description = description;
			this.action = action;
			this.isNativeCall = methodDescription == null ? false : Array.Exists(methodDescription.GetParameters(), p => p.ParameterType == typeof(IDEWebRequestScope));
			this.isSafeCall = isNativeCall ? false : isSafeCall;
			this.methodDescription = methodDescription;
		} // ctor

		/// <summary></summary>
		/// <param name="item"></param>
		/// <param name="context"></param>
		/// <returns></returns>
		public object Invoke(DEConfigItem item, IDEWebRequestScope context)
		{
			if (action != null)
			{
				if (isNativeCall)
				{
					action(item, context);
					return DBNull.Value;
				}
				else
					return action(item, context);
			}
			else
				return null;
		} // proc Invoke

		/// <summary>Method name of the action.</summary>
		public string Name => methodDescription.Name;
		/// <summary>Description of the action.</summary>
		public string Description => description;
		/// <summary>Method description of the action.</summary>
		public MethodInfo MethodDescription => methodDescription;
		/// <summary>Security token, that can call the action.</summary>
		public string SecurityToken => securityToken;
		/// <summary>Is this action called in the safe mode.</summary>
		public bool IsSafeCall => isSafeCall;

		/// <summary></summary>
		public bool IsEmpty => action == null;

		// -- Static ----------------------------------------------------------

		/// <summary></summary>
		public static DEConfigAction Empty { get; } = new DEConfigAction(null, null, null, false, null);
	} // class DEConfigAction

	#endregion

	#region -- class DEConfigItem -----------------------------------------------------

	public partial class DEConfigItem
	{
		#region -- struct ConfigAction ------------------------------------------------

		private struct ConfigAction
		{
			public string Description;
			public DEConfigHttpActionAttribute Attribute;
			public MethodInfo Method;
		} // struct ConfigAction

		#endregion

		#region -- class ConfigDescriptionCache ---------------------------------------

		/// <summary>Sichert die Aktionen die innerhalb einer Klasse gefunden wurden.</summary>
		private sealed class ConfigDescriptionCache
		{
			private readonly ConfigDescriptionCache prev;
			private readonly ConfigAction[] actions;
			private readonly PropertyDescriptor[] properties;

			public ConfigDescriptionCache(ConfigDescriptionCache prev, Type type)
			{
				this.prev = prev;

				// Suche alle Actions
				actions =
					(
						from mi in type.GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.InvokeMethod)
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

			public bool GetConfigAction(string actionName, out ConfigAction action)
			{
				// Suuche die Action
				if (actions != null)
				{
					var idx = Array.FindIndex(actions, cur => String.Compare(cur.Attribute.ActionName, actionName, true) == 0);
					if (idx >= 0)
					{
						action = actions[idx];
						return true;
					}
				}

				if (prev != null)
					return prev.GetConfigAction(actionName, out action);
				else
				{
					action = new ConfigAction();
					return false;
				}
			} // func GetConfigAction

			public ConfigAction[] Actions => actions;
			public PropertyDescriptor[] Properties => properties;
			public ConfigDescriptionCache Previous => prev;

			// -- Static ------------------------------------------------------

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

		#region -- class ConfigActionListDescriptor -----------------------------------

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

			public static ConfigActionListDescriptor Instance => configActionListDescriptor;
		} // class ConfigActionListDescriptor 

		#endregion

		#region -- class ConfigActionDictionary ---------------------------------------

		private sealed class ConfigActionDictionary : DEListControllerBase, IEnumerable<KeyValuePair<string, DEConfigAction>>
		{
			private readonly Dictionary<string, DEConfigAction> actions = new Dictionary<string, DEConfigAction>(EqualityComparer<string>.Default);

			#region -- Ctor/Dtor ------------------------------------------------------

			public ConfigActionDictionary(DEConfigItem configItem)
				: base(configItem, ConfigActionListDescriptor.Instance, ActionsListId, "Actions")
			{
			} // ctor

			#endregion

			#region -- GetEnumerator --------------------------------------------------

			public IEnumerator<KeyValuePair<string, DEConfigAction>> GetEnumerator()
				=> actions.GetEnumerator();

			System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()
				=> GetEnumerator();

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
				=> ConfigItem.CollectActions(); // Sammle alle Aktions

			public DEConfigAction this[string sActionId]
			{
				get
				{
					using (EnterReadLock())
					{
						return actions.TryGetValue(sActionId, out var action) ? action : null;
					}
				}
				set
				{
					using (EnterWriteLock())
						actions[sActionId] = value;
				}
			} // prop this

			public override System.Collections.IEnumerable List => this;
		} // class ConfigActionDictionary

		#endregion

		#region -- Actions, Attached Script -------------------------------------------

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
		public (bool, object) InvokeAction(string actionName, IDEWebRequestScope context)
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
				using (context.Use())
					return (true, a.Invoke(this, context)); // support for async actions is missing -> results in a InvokeAcionAsync
			}
			catch (Exception e)
			{
				if (!a.IsSafeCall || context.IsOutputStarted || (e is HttpResponseException)) // Antwort kann nicht mehr gesendet werden
					throw;

				// Write protocol
				if (e is ILuaUserRuntimeException userMessage)
				{
					Log.LogMsg(LogMsgType.Warning, e.GetMessageString());
					return (false, CreateDefaultReturn(context, DEHttpReturnState.User, userMessage.Message));
				}
				else
				{
					Log.LogMsg(LogMsgType.Error, e.GetMessageString());
					return (false, CreateDefaultReturn(context, DEHttpReturnState.Error, e.Message));
				}
			}
		} // func InvokeAction

		private DEConfigAction CompileTypeAction(string sAction)
		{
			var cac = ConfigDescriptionCache.Get(GetType());
			if (cac == null)
				return DEConfigAction.Empty;

			return cac.GetConfigAction(sAction, out var ca)
				? CompileTypeAction(ref ca)
				: DEConfigAction.Empty;
		} // func CompileTypeAction

		private void CompileTypeActions(ConfigDescriptionCache cac)
		{
			if (cac == null)
				return;

			// Vorgänger bearbeiten
			CompileTypeActions(cac.Previous);

			if (cac.Actions != null)
			{
				for (var i = 0; i < cac.Actions.Length; i++)
				{
					if (!actions.Contains(cac.Actions[i].Attribute.ActionName))
						actions[cac.Actions[i].Attribute.ActionName] = CompileTypeAction(ref cac.Actions[i]);
				}
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

			if (!(ca["Method"] is Delegate dlg))
				return DEConfigAction.Empty;

			return new DEConfigAction(
				ca.GetOptionalValue<string>("Security", null),
				ca.GetOptionalValue<string>("Description", null),
				CompileMethodAction(dlg.Method, dlg, i => ca[i + 1]).Compile(),
				ca.GetOptionalValue("SafeCall", true),
				dlg.Method
			);
		} // func CompileLuaAction

		/// <summary></summary>
		/// <param name="actionName"></param>
		/// <returns></returns>
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

		private Expression<DEConfigActionDelegate> CompileMethodAction(MethodInfo method, Delegate @delegate = null, Func<int, object> alternateParameterDescription = null)
		{
			var argThis = Expression.Parameter(typeof(DEConfigItem), "#this");
			var argCaller = Expression.Parameter(typeof(IDEWebRequestScope), "#arg");

			ParameterInfo[] parameterInfo;
			var parameterOffset = 0;

			// ger the parameter information
			if (@delegate != null)
			{
				var miInvoke = @delegate.GetType().GetMethod("Invoke");

				parameterInfo = method.GetParameters();
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
				throw new NotImplementedException("configuration of extraData is currently not implemented.");

			// Generiere das Parameter Mapping
			var exprParameter = CreateArgumentExpressions(argCaller, argThis, parameterOffset, parameterInfo, alternateParameterDescription, inputStream);

			// Erzeuge den Call auf die Action
			var exprCall = @delegate == null ?
				(Expression)Expression.Call(Expression.Convert(argThis, method.DeclaringType), method, exprParameter) :
				(Expression)Expression.Invoke(Expression.Constant(@delegate), exprParameter);

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

		private Expression[] CreateArgumentExpressions(ParameterExpression arg, ParameterExpression argThis, int parameterOffset, ParameterInfo[] parameterInfo, Func<int, object> alternateParameterDescription, ParameterExpression extraData)
		{
			var r = new Expression[parameterInfo.Length - parameterOffset];

			// Create the parameter
			for (var i = 0; i < r.Length; i++)
			{
				var currentParameter = parameterInfo[i + parameterOffset];
				var typeTo = currentParameter.ParameterType;
				var typeCode = Type.GetTypeCode(typeTo);

				var propertyDictionary = Expression.Convert(arg, typeof(IPropertyReadOnlyDictionary));

				// generate expressions
				CreateArgumentExpressionsByInfo(alternateParameterDescription != null ? alternateParameterDescription(i) : null, currentParameter, out var parameterName, out var parameterDefault);

				Expression exprGetParameter;

				if (typeTo == typeof(object)) // Keine Konvertierung
				{
					exprGetParameter = Expression.Call(miGetPropertyObject, propertyDictionary, parameterName, parameterDefault);
				}
				else if (typeCode == TypeCode.Object && !typeTo.IsValueType) // Gibt keine Default-Werte, ermittle den entsprechenden TypeConverter
				{
					if (typeTo == typeof(IDEWebRequestScope))
					{
						exprGetParameter = Expression.Condition(
							Expression.TypeIs(arg, typeof(IDEWebRequestScope)),
							Expression.Convert(arg, typeof(IDEWebRequestScope)),
							Expression.Throw(Expression.New(typeof(ArgumentException).GetConstructor(new Type[] { typeof(string) }), Expression.Constant("NativeCall expects a IDEHttpContext argument.")), typeof(IDEWebRequestScope))
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
								Expression.Call(miGetPropertyString, propertyDictionary, parameterName, parameterDefault)
							),
							typeTo
						);
					}
				}
				else if (typeCode == TypeCode.String) // String gibt es nix zu tun
				{
					exprGetParameter = Expression.Call(miGetPropertyString, propertyDictionary, parameterName, parameterDefault);
				}
				else // Some type
				{
					// ToType - Konverter
					var miTarget = miGetPropertyGeneric.MakeGenericMethod(typeTo);
					exprGetParameter = Expression.Call(miTarget, propertyDictionary, parameterName, parameterDefault);
				}

				r[i] = exprGetParameter;
			}

			return r;
		} // func CreateArgumentExpressions

		private static void CreateArgumentExpressionsByInfo(dynamic alternateParameterInfo, ParameterInfo parameterInfo, out Expression parameterName, out Expression parameterDefault)
		{
			var parameterNameString = (string)(alternateParameterInfo?.Name ?? parameterInfo.Name);
			var parameterDefaultValue = (object)(alternateParameterInfo?.Default ?? parameterInfo.DefaultValue);

			if (parameterNameString == null)
				throw new ArgumentNullException("parameterName");

			parameterName = Expression.Constant(parameterNameString, typeof(string));
			parameterDefault =
				parameterDefaultValue == DBNull.Value || parameterDefaultValue == null ?
					(Expression)Expression.Default(parameterInfo.ParameterType) :
					Expression.Constant(Procs.ChangeType(parameterDefaultValue, parameterInfo.ParameterType), parameterInfo.ParameterType);
		} // func CreateArgumentExpressionsByInfo

		private LuaTable GetActionTable()
			=> this.GetMemberValue(LuaActions, rawGet: true) as LuaTable;

		#endregion
	} // class DEConfigItem

	#endregion
}
