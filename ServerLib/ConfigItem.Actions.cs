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
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Microsoft.SqlServer.Server;
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
	/// <param name="log"></param>
	/// <returns></returns>
	public delegate object DEConfigActionDelegate(DEConfigItem item, IDEWebRequestScope context, LogMessageScopeProxy log);

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
		private readonly bool isAutoLog;

		/// <summary></summary>
		/// <param name="securityToken"></param>
		/// <param name="description"></param>
		/// <param name="action"></param>
		/// <param name="isSafeCall"></param>
		/// <param name="methodDescription"></param>
		/// <param name="isAutoLog"></param>
		public DEConfigAction(string securityToken, string description, DEConfigActionDelegate action, bool isSafeCall, MethodInfo methodDescription, bool isAutoLog)
		{
			this.securityToken = securityToken;
			this.description = description;
			this.action = action;

			if (methodDescription != null)
			{
				var parameterInfo = methodDescription.GetParameters();
				for (var i = 0; i < parameterInfo.Length; i++)
				{
					if (parameterInfo[i].ParameterType == typeof(IDEWebRequestScope))
						isNativeCall = true;
					else if (parameterInfo[i].ParameterType == typeof(LogMessageScopeProxy))
						isAutoLog = true;
				}
			}
		
			this.isSafeCall = !isNativeCall && isSafeCall;
			this.methodDescription = methodDescription;
			this.isAutoLog = isAutoLog;
		} // ctor

		/// <summary></summary>
		/// <param name="item"></param>
		/// <param name="context"></param>
		/// <param name="log"></param>
		/// <returns></returns>
		public object Invoke(DEConfigItem item, IDEWebRequestScope context, LogMessageScopeProxy log = null)
		{
			if (action != null)
			{
				if (isNativeCall)
				{
					action(item, context, log);
					return DBNull.Value;
				}
				else
					return action(item, context, log);
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
		/// <summary>Should this action create a log scope.</summary>
		public bool IsAutoLog => isAutoLog;

		/// <summary></summary>
		public bool IsEmpty => action == null;

		// -- Static ----------------------------------------------------------

		/// <summary></summary>
		public static DEConfigAction Empty { get; } = new DEConfigAction(null, null, null, false, null, false);
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
					if (c.Key is string actionId && !actions.Contains(actionId))
						actions[actionId] = CompileLuaAction(actionId, c.Value as LuaTable);
				}
			}
		} // proc CollectActions

		/// <summary>Führt eine Aktion aus.</summary>
		/// <param name="actionName">Name der Aktion</param>
		/// <param name="context">Parameter, die übergeben werden sollen.</param>
		/// <returns>Rückgabe</returns>
		public (bool, object) InvokeAction(string actionName, IDEWebRequestScope context)
		{
			// Lookup action within cache
			DEConfigAction a;
			lock (actions)
			{
				a = actions[actionName];
				if (a == null) // No cached action, create execution code
				{
					a = CompileAction(actionName);
					if (a == null)
						a = DEConfigAction.Empty;
					actions[actionName] = a;
				}
			}

			if (a == DEConfigAction.Empty)
				throw new HttpResponseException(HttpStatusCode.BadRequest, String.Format("Action {0} not found", actionName));

			// check security
			context.DemandToken(a.SecurityToken);

			// Execute action
			using (var log = a.IsAutoLog ? Log.CreateScope(LogMsgType.Information, false, true) : null)
			{
				try
				{
					using (context.Use())
					{
						log?.AutoFlush();
						return (true, a.Invoke(this, context, log)); // support for async actions is missing -> results in a InvokeAcionAsync
					}
				}
				catch (Exception e)
				{
					if (!a.IsSafeCall || context.IsOutputStarted || (e is HttpResponseException)) // Antwort kann nicht mehr gesendet werden
					{
						if (log != null)
							log.WriteException(e);
						throw;
					}

					// Write protocol
					if (e is ILuaUserRuntimeException userMessage)
					{
						if (log == null)
							Log.Warn(e);
						else
							log.WriteWarning(e);
						return (false, CreateDefaultReturn(context, DEHttpReturnState.User, userMessage.Message));
					}
					else
					{
						if (log == null)
							Log.Except(e);
						else
							log.WriteException(e);
						return (false, CreateDefaultReturn(context, DEHttpReturnState.Error, e.Message));
					}
				}
			}
		} // func InvokeAction

		private DEConfigAction CompileTypeAction(string actionName)
		{
			var cac = ConfigDescriptionCache.Get(GetType());
			if (cac == null)
				return DEConfigAction.Empty;

			return cac.GetConfigAction(actionName, out var ca)
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
					var actionName = cac.Actions[i].Attribute.ActionName;
					if (!actions.Contains(actionName))
						actions[actionName] = CompileTypeAction(ref cac.Actions[i]);
				}
			}
		} // proc CompileTypeActions

		private DEConfigAction CompileTypeAction(ref ConfigAction ca)
		{
			var exprLambda = CompileMethodAction(ca.Attribute.ActionName, ca.Method);

			// Erzeuge die Action
			return new DEConfigAction(ca.Attribute.SecurityToken, ca.Description, exprLambda.Compile(), ca.Attribute.IsSafeCall, ca.Method, ca.Attribute.IsAutoLog);
		} // func CompileTypeAction

		private DEConfigAction CompileLuaAction(string actionName)
		{
			// Table mit den Aktions
			var table = GetActionTable();
			if (table == null)
				return DEConfigAction.Empty;

			// Erzeuge die Aktion
			return CompileLuaAction(actionName, table[actionName] as LuaTable);
		} // func CompileLuaAction

		private DEConfigAction CompileLuaAction(string actionName, LuaTable ca)
		{
			if (actionName == null || ca == null)
				return DEConfigAction.Empty;

			if (!(ca["Method"] is Delegate dlg))
				return DEConfigAction.Empty;

			return new DEConfigAction(
				ca.GetOptionalValue<string>("Security", null),
				ca.GetOptionalValue<string>("Description", null),
				CompileMethodAction(actionName, dlg.Method, dlg, i => ca[i + 1]).Compile(),
				ca.GetOptionalValue("SafeCall", true),
				dlg.Method,
				ca.GetOptionalValue("AutoLog", false)
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

		private Expression<DEConfigActionDelegate> CompileMethodAction(string actionName, MethodInfo method, Delegate @delegate = null, Func<int, object> alternateParameterDescription = null)
		{
			var argThis = Expression.Parameter(typeof(DEConfigItem), "#this");
			var argCaller = Expression.Parameter(typeof(IDECommonScope), "#arg");
			var argLog = Expression.Parameter(typeof(LogMessageScopeProxy), "#log");

			ParameterInfo[] parameterInfo;
			int parameterOffset;

			// get the parameter information
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

			/* var inputStream = args.GetInputStream();
			 * try
			 * {
			 *	 var arg1
			 *	 var arg2
			 *	 if (log != null)
			 *		WriteActionStart(log, actionName, n, new object[] { arg1, arg2 });
			 *	 r = call(args);
			 *	 if (log != null)
			 *		WriteResult(log, e);
			 *	 return r;
			 * }
			 * finally
			 * {
			 *   inputStream.Dispose();
			 * }
			 */

			var methodBlock = new List<Expression>(16);
			var methodVariables = new List<ParameterExpression>(4);

			// generate parameter setting for the function and logging
			CreateArgumentExpressions(methodBlock, methodVariables, argCaller, argThis, argLog, parameterOffset, parameterInfo, alternateParameterDescription, out var inputStream);

			// generate start log message
			methodBlock.Add(Expression.IfThen(Expression.ReferenceNotEqual(argLog, Expression.Constant(null, argLog.Type)),
				Expression.Call(writeActionStartMethodInfo,
					argCaller,
					argLog,
					Expression.Constant(actionName),
					Expression.NewArrayInit(typeof(string), methodVariables.Select(c => Expression.Constant(c.Name[0] != '#' ? c.Name : null, typeof(string)))),
					Expression.NewArrayInit(typeof(object), methodVariables.Select(c => Expression.Convert(c, typeof(object))))
				)
			));

			// generate the call command
			var returnVariable = method.ReturnType == typeof(void) ? null : Expression.Variable(typeof(object), "#return");
			var exprCall = @delegate == null
				? (Expression)Expression.Call(Expression.Convert(argThis, method.DeclaringType), method, methodVariables)
				: Expression.Invoke(Expression.Constant(@delegate), methodVariables);
			if (returnVariable != null)
			{
				methodVariables.Add(returnVariable);
				methodBlock.Add(Expression.Assign(returnVariable, exprCall));

				// generate finish log message
				methodBlock.Add(Expression.IfThen(Expression.ReferenceNotEqual(argLog, Expression.Constant(null, argLog.Type)),
					Expression.Call(writeActionResultMethodInfo, argLog, returnVariable)
				));

				methodBlock.Add(returnVariable);
			}
			else
			{
				methodBlock.Add(exprCall);
				methodBlock.Add(Expression.Default(typeof(object)));
			}

			// input stream is requested
			if (inputStream != null)
			{
				var expr = Expression.Block(new ParameterExpression[] { inputStream },
					Expression.Assign(inputStream, CreateGetInputExpression(argCaller, inputStream.Type)),
					Expression.TryFinally(
						Expression.Block(typeof(object), methodVariables, methodBlock),
						Expression.Call(Expression.Convert(inputStream, typeof(IDisposable)), disposeMethodInfo)
					)
				);
				methodVariables.Add(inputStream);
				methodBlock.Clear();
				methodBlock.Add(expr);
			}

			// Create lamda with return value
			var exprLambda = Expression.Lambda<DEConfigActionDelegate>(
				Expression.Block(typeof(object), methodVariables, methodBlock),
				true, argThis, argCaller, argLog
			);
			return exprLambda;
		} // func CompileMethodAction

		private Expression CreateGetInputExpression(ParameterExpression argCaller, Type type)
		{
			if (type == typeof(Stream))
				return Expression.Call(GetWebScopeExpression(argCaller), getInputStreamMethodInfo);
			else if (type == typeof(TextReader))
				return Expression.Call(GetWebScopeExpression(argCaller), getInputTextReaderMethodInfo);
			else
				throw new ArgumentException("Invalid input stream type.");
		} // func CreateGetInputExpression

		private void CreateArgumentExpressions(List<Expression> methodBlock, List<ParameterExpression> methodVariables, ParameterExpression arg, ParameterExpression argThis, ParameterExpression argLog, int parameterOffset, ParameterInfo[] parameterInfo, Func<int, object> alternateParameterDescription, out ParameterExpression extraData)
		{
			extraData = null;

			// Create the parameter
			var l = parameterInfo.Length - parameterOffset;
			for (var i = 0; i < l; i++)
			{
				var currentParameter = parameterInfo[i + parameterOffset];
				var typeTo = currentParameter.ParameterType;
				var typeCode = Type.GetTypeCode(typeTo);

				var propertyDictionary = Expression.Convert(arg, typeof(IPropertyReadOnlyDictionary));

				// generate expressions
				Expression exprGetParameter;
				string emitParameterName;

				if (typeTo == typeof(IDEWebRequestScope))
				{
					exprGetParameter = GetWebScopeExpression(arg);
					emitParameterName = "#r";
				}
				else if (typeTo == typeof(LogMessageScopeProxy))
				{
					exprGetParameter = argLog;
					emitParameterName = "#log";
				}
				else if (typeTo.IsAssignableFrom(GetType()))
				{
					exprGetParameter = argThis;
					emitParameterName = "#this";
				}
				else if (typeTo == typeof(TextReader)) // Input-Datenstrom (Text)
				{
					if (extraData != null)
						throw new ArgumentException("Only one input parameter is allowed.");

					extraData = Expression.Variable(typeof(TextReader), "#input");
					exprGetParameter = extraData;
					emitParameterName = extraData.Name;
				}
				else if (typeTo == typeof(Stream)) // Input-Datenstrom (Binär)
				{
					if (extraData != null)
						throw new ArgumentException("Only one input parameter is allowed.");

					extraData = Expression.Variable(typeof(Stream), "#input");
					exprGetParameter = extraData;
					emitParameterName = extraData.Name;
				}
				else if (typeTo == typeof(LuaTable)) // input json/lson-object
				{
					exprGetParameter = Expression.Call(httpRequestGetTypeMethodInfo, GetWebScopeExpression(arg));
					emitParameterName = "#table";
				}
				else
				{
					CreateArgumentExpressionsByInfo(
						alternateParameterDescription?.Invoke(i),
						currentParameter,
						out var parameterName,
						out var parameterDefault
					);

					if (typeTo == typeof(object)) // Keine Konvertierung
					{
						exprGetParameter = Expression.Call(getPropertyObjectMethodInfo, propertyDictionary, parameterName, parameterDefault);
					}
					else if (typeCode == TypeCode.Object && !typeTo.IsValueType) // Gibt keine Default-Werte, ermittle den entsprechenden TypeConverter
					{
						var conv = TypeDescriptor.GetConverter(typeTo);

						exprGetParameter =
							Expression.Convert(
								Expression.Call(Expression.Constant(conv), convertFromInvariantStringMethodInfo,
								Expression.Call(getPropertyStringMethodInfo, propertyDictionary, parameterName, parameterDefault)
							),
							typeTo
						);
					}
					else if (typeCode == TypeCode.String) // String gibt es nix zu tun
					{
						exprGetParameter = Expression.Call(getPropertyStringMethodInfo, propertyDictionary, parameterName, parameterDefault);
					}
					else // Some type
					{
						// ToType - Konverter
						var miTarget = getPropertyGenericMethodInfo.MakeGenericMethod(typeTo);
						exprGetParameter = Expression.Call(miTarget, propertyDictionary, parameterName, parameterDefault);
					}

					emitParameterName = (string)parameterName.Value;
				}

				// add expression
				var v = Expression.Variable(exprGetParameter.Type, emitParameterName);
				methodVariables.Add(v);
				methodBlock.Add(Expression.Assign(v, exprGetParameter));
			}
		} // func CreateArgumentExpressions

		private static ConditionalExpression GetWebScopeExpression(ParameterExpression argCaller)
		{
			return Expression.Condition(
				Expression.TypeIs(argCaller, typeof(IDEWebRequestScope)),
				Expression.Convert(argCaller, typeof(IDEWebRequestScope)),
				Expression.Throw(Expression.New(typeof(ArgumentException).GetConstructor(new Type[] { typeof(string) }), Expression.Constant($"Call expects a {nameof(IDEWebRequestScope)} argument.")), typeof(IDEWebRequestScope))
			);
		} // func GetWebScopeExpression

		private static void CreateArgumentExpressionsByInfo(dynamic alternateParameterInfo, ParameterInfo parameterInfo, out ConstantExpression parameterName, out Expression parameterDefault)
		{
			var parameterNameString = (string)(alternateParameterInfo?.Name ?? parameterInfo.Name);
			var parameterDefaultValue = (object)(alternateParameterInfo?.Default ?? parameterInfo.DefaultValue);

			if (parameterNameString == null)
				throw new ArgumentNullException(nameof(parameterName));

			parameterName = Expression.Constant(parameterNameString, typeof(string));
			parameterDefault = parameterDefaultValue == DBNull.Value || parameterDefaultValue == null
				? (Expression)Expression.Default(parameterInfo.ParameterType) 
				: Expression.Constant(Procs.ChangeType(parameterDefaultValue, parameterInfo.ParameterType), parameterInfo.ParameterType);
		} // func CreateArgumentExpressionsByInfo

		private static string FormatParameter(object value, int maxLen, bool escape = true)
		{
			if (value == null)
				return "null";
			else if (value is string s)
			{
				if (s.Length > maxLen)
					s = s.Substring(0, maxLen - 3) + "...";

				if (escape)
					s = '"' + s + '"';

				return s;
			}
			else if (value is LuaTable t)
				return FormatParameter(t.ToLson(false), maxLen, false);
			else if (value is XElement x)
				return FormatParameter(x.ToString(SaveOptions.DisableFormatting), maxLen, false);
			else
				return FormatParameter(value.ChangeType<string>(), maxLen, false);
		} // func FormatParameter

		/// <summary></summary>
		/// <param name="r"></param>
		/// <param name="log"></param>
		/// <param name="actioName"></param>
		/// <param name="names"></param>
		/// <param name="arguments"></param>
		[EditorBrowsable(EditorBrowsableState.Advanced)]
		public static void WriteActionStart(IDECommonScope r, LogMessageScopeProxy log, string actioName, string[] names, object[] arguments)
		{
			// write user
			var userInfo = r.User?.Info;
			if (userInfo != null)
				log.Write("[").Write(userInfo.DisplayName).Write("] ");

			// write action an parameter
			log.Write(actioName);
			log.Write("(");
			for (var i = 0; i < names.Length; i++)
			{
				if (i > 0)
					log.Write(",");

				if (names[i] != null)
				{
					log.Write(names[i])
						.Write("=")
						.Write(FormatParameter(arguments[i], 20));
				}
			}
			log.WriteLine(")");
		} // proc WriteActionStart

		/// <summary></summary>
		/// <param name="log"></param>
		/// <param name="result"></param>
		[EditorBrowsable(EditorBrowsableState.Advanced)]
		public static void WriteActionResult(LogMessageScopeProxy log, object result)
		{
			log.NewLine();
			log.WriteLine("Result: " + FormatParameter(result, 200));
		} // WriteActionResult

		private LuaTable GetActionTable()
			=> this.GetMemberValue(LuaActions, rawGet: true) as LuaTable;

		#endregion
	} // class DEConfigItem

	#endregion
}
