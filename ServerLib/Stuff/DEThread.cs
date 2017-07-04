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
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using TecWare.DE.Stuff;

namespace TecWare.DE.Server
{
	#region -- interface IDEThreadSource ------------------------------------------------

	/// <summary>Access to the base thread.</summary>
	public interface IDEThreadSource
	{
		DEThreadBase Thread { get; }
	} // interface IDEThreadSource

	#endregion

	#region -- class DEThreadContext ----------------------------------------------------

	/// <summary>Simple synchronization context, that post all tasks/actions back to this thread.</summary>
	public sealed class DEThreadContext : SynchronizationContext, IDEThreadSource
	{
		private readonly DEThreadBase thread;

		public DEThreadContext(DEThreadBase thread)
			=> this.thread = thread;

		public override SynchronizationContext CreateCopy()
			=> new DEThreadContext(thread);

		public override void Post(SendOrPostCallback d, object state)
			=> thread.Post(d, state);

		public override void Send(SendOrPostCallback d, object state)
			=> thread.Send(d, state);

		public DEThreadBase Thread => thread;
	} // class DEThreadContext

	#endregion

	#region -- interface IDEScope  ------------------------------------------------------

	public interface IDEScope : IServiceProvider, IDisposable
	{
		/// <summary>Use this scope as SynchronizationContext</summary>
		/// <returns></returns>
		IDisposable Use();

		/// <summary>Executes the Action within this scope.</summary>
		/// <param name="action"></param>
		void ExecuteWith(Action action);
		/// <summary>Executes the Function within this scope.</summary>
		/// <typeparam name="T"></typeparam>
		/// <param name="func"></param>
		/// <returns></returns>
		T ExecuteWith<T>(Func<T> func);
		
		/// <summary></summary>
		bool IsCurrentScope { get; }
	} // interface IDEScope


	#endregion

	#region -- class DEScope ------------------------------------------------------------

	/// <summary>That is hold within an synchronization context. Store for global states of an execution thread.</summary>
	public class DEScope : IServiceProvider, IDisposable
	{
		#region -- class DEScopeContext -------------------------------------------------

		/// <summary>Special thread context, that holds a currently running transaction.</summary>
		private sealed class DEScopeContext : SynchronizationContext, IDEThreadSource
		{
			private readonly DEScope scope;
			private readonly DEScopeContext parentScopeContext;
			private readonly SynchronizationContext parentContext;

			internal DEScopeContext(DEScope scope, SynchronizationContext currentContext)
			{
				this.scope = scope;
				this.parentScopeContext = currentContext as DEScopeContext;
				this.parentContext = parentScopeContext == null ? currentContext : parentScopeContext.parentContext;
			} // ctor

			public override int GetHashCode()
				=> scope.GetHashCode();

			public override bool Equals(object obj)
				=> Object.ReferenceEquals(this, obj) || (obj is DEScopeContext t ? t.scope == scope : false);

			public override SynchronizationContext CreateCopy()
				=> new DEScopeContext(scope, parentContext);

			private void ExecuteWithContext(object state)
			{
				var tuple = (Tuple<SendOrPostCallback, object>)state;
				ExecuteWith(tuple.Item1, tuple.Item2);
			} // proc PostWithContext

			public IDisposable Use()
			{
				var oldContext = Current;
				if (oldContext == this)
					return null;
				else
				{
					SetSynchronizationContext(this);
					return new DisposableScope(() => SetSynchronizationContext(oldContext));
				}
			} // func Use

			public void ExecuteWith(SendOrPostCallback callback, object state)
			{
				using (Use())
					callback(state);
			} // proc ExecuteWith

			public void ExecuteWith(Action action)
			{
				using (Use())
					action();
			} // proc ExecuteWith

			public T ExecuteWith<T>(Func<T> func)
			{
				using (Use())
					return func();
			} // proc ExecuteWith

			public override void Post(SendOrPostCallback d, object state)
			{
				var t = new Tuple<SendOrPostCallback, object>(d, state);
				if (parentContext != null)
					parentContext.Post(ExecuteWithContext, t);
				else
					ThreadPool.QueueUserWorkItem(ExecuteWithContext, t);
			} // proc Post

			public override void Send(SendOrPostCallback d, object state)
			{
				var t = new Tuple<SendOrPostCallback, object>(d, state);
				if (parentContext != null)
					parentContext.Send(ExecuteWithContext, t);
				else
					ExecuteWithContext(new Tuple<SendOrPostCallback, object>(d, state));
			} // proc Send

			public DEScope Scope => scope;
			public DEThreadBase Thread => (parentContext as IDEThreadSource)?.Thread;

			internal DEScopeContext ParentScopeContext => parentScopeContext;
			internal SynchronizationContext ParentSynchronizationContext => parentContext;
		} // class DEScopeContext

		#endregion

		private bool isDisposed = false;

		#region -- Ctor/Dtor ------------------------------------------------------------

		public DEScope()
		{
		} // ctor

		public void Dispose()
			=> Dispose(true);

		protected virtual void Dispose(bool disposing)
		{
			if (isDisposed)
				throw new ObjectDisposedException(nameof(DEScope));

			isDisposed = true;
		} // proc Dispose

		#endregion

		#region -- IServiceProvider -----------------------------------------------------

		public virtual object GetService(Type serviceType)
			=> serviceType.IsAssignableFrom(GetType()) ? this : null;

		#endregion

		#region -- Use, ExecuteWith -----------------------------------------------------

		public IDisposable Use()
			=> IsCurrentScope
				? null
				: new DEScopeContext(this, SynchronizationContext.Current).Use();

		public void ExecuteWith(Action action)
		{
			using (Use())
				action();
		} // func ExecuteWith

		public T ExecuteWith<T>(Func<T> func)
		{
			using (Use())
				return func();
		} // func ExecuteWith

		#endregion

		public bool IsDisposed => isDisposed;
		public bool IsCurrentScope => SynchronizationContext.Current is DEScopeContext t ? t.Scope == this : false;

		// -- Static --------------------------------------------------------------------

		/// <summary>Returns the current active scope.</summary>
		/// <param name="throwException"></param>
		/// <returns></returns>
		public static DEScope GetScope(bool throwException = false)
		{
			if (SynchronizationContext.Current is DEScopeContext t)
				return t.Scope;
			else if (throwException)
				throw new ArgumentNullException(nameof(GetScope), "No current scope set.");
			else
				return null;
		} // func GetScope

		/// <summary>Returns a global service of the current scope or a parent scope (service model).</summary>
		/// <typeparam name="T"></typeparam>
		/// <param name="throwException"></param>
		/// <returns></returns>
		public static T GetScopeService<T>(bool throwException = false)
			where T : class
		{
			var currentScope = SynchronizationContext.Current as DEScopeContext;
			var r = (T)null;
			while (currentScope != null)
			{
				if (!currentScope.Scope.IsDisposed)
					r = currentScope.Scope.GetService<T>(false);
				currentScope = currentScope.ParentScopeContext;
			}

			if (r == null && throwException)
				throw new ArgumentException($"Service {typeof(T).Name} is not implemented by the current scope.");

			return r;
		} // func GetScopeService
	} // class DEScope

	#endregion

	#region -- class DEThreadBase -------------------------------------------------------

	public abstract class DEThreadBase : IServiceProvider, IDisposable
	{
		public const string ThreadCategory = "Thread";

		private readonly Thread thread = null;
		private readonly DEThreadContext context;

		private readonly IServiceProvider sp;
		private readonly LoggerProxy log;
		private readonly SimpleConfigItemProperty<int> propertyRestarts;
		private readonly SimpleConfigItemProperty<string> propertyRunning;

		private readonly CancellationTokenSource threadCancellation;
		private readonly ManualResetEventSlim tasksFilled;

		private bool isDisposed = false;

		#region -- Ctor/Dtor ------------------------------------------------------------

		public DEThreadBase(IServiceProvider sp, string name, string categoryName = ThreadCategory)
		{
			this.sp = sp ?? throw new ArgumentNullException(nameof(sp));

			if (String.IsNullOrEmpty(name))
				throw new ArgumentNullException(nameof(name));

			// create the log information
			this.log = sp.LogProxy() ?? throw new ArgumentNullException("log", "Service provider must provide logging.");

			// register properties
			propertyRestarts = new SimpleConfigItemProperty<int>(sp,
				$"tw_thread_restarts_{name}",
				$"{name} - Restarts",
				categoryName,
				"The number of restarts of this thread.",
				"{0:N0}",
				0
			);
			propertyRunning = new SimpleConfigItemProperty<string>(sp,
				$"tw_thread_running_{name}",
				$"{name} - State",
				categoryName,
				"Is the thread still active.",
				null,
				"Läuft"
			);

			// start the thread
			thread = new Thread(Execute) { Name = name };
			SetThreadParameter(thread);

			this.context = new DEThreadContext(this);

			// start the thread
			using (var startedEvent = new ManualResetEventSlim(false))
			{
				threadCancellation = new CancellationTokenSource();
				tasksFilled = new ManualResetEventSlim(false);

				thread.Start(startedEvent);
				if (!startedEvent.Wait(3000))
					throw new Exception(String.Format("Could not start thread '{0}'.", name));
			}
		} // ctor

		protected virtual void SetThreadParameter(Thread thread)
		{
			thread.SetApartmentState(ApartmentState.STA);
			thread.IsBackground = true;
			thread.Priority = ThreadPriority.Normal;
		} // proc SetThreadParameter

		public void Dispose()
			=> Dispose(true);

		protected virtual void Dispose(bool disposing)
		{
			if (isDisposed)
				throw new ObjectDisposedException(nameof(DEThreadBase));

			isDisposed = true;
			threadCancellation.Cancel();
			if (disposing)
			{
				// stop the thread
				if (thread != Thread.CurrentThread && !thread.Join(3000))
					thread.Abort();

				// clear objects
				threadCancellation.Dispose();
				tasksFilled.Dispose();

				// Remove properties
				propertyRestarts.Dispose();
				propertyRunning.Dispose();
			}
		} // proc Dispose

		#endregion

		#region -- Message Loop ---------------------------------------------------------

		private void VerifyThreadAccess()
		{
			if (thread != Thread.CurrentThread)
				throw new InvalidOperationException($"Process of the queued task is only allowed in the same thread.(queue threadId {thread.ManagedThreadId}, caller thread id: {Thread.CurrentThread.ManagedThreadId})");
		} // proc VerifyThreadAccess

		internal void Post(SendOrPostCallback d, object state)
			=> EnqueueTask(d, state, null);

		internal void Send(SendOrPostCallback d, object state)
		{
			if (thread == Thread.CurrentThread)
				throw new InvalidOperationException($"Send can not be called from the same thread (Deadlock).");

			using (var waitHandle = new ManualResetEventSlim(false))
			{
				EnqueueTask(d, state, waitHandle);
				waitHandle.Wait();
			}
		} // proc Send

		protected abstract void EnqueueTask(SendOrPostCallback d, object state, ManualResetEventSlim waitHandle);

		protected abstract bool TryDequeueTask(CancellationToken cancellationToken, out SendOrPostCallback d, out object state, out ManualResetEventSlim wait);

		protected void ResetMessageLoopUnsafe()
		{
			if (!IsDisposed)
				tasksFilled.Reset();
		} // proc ResetMessageLoopUnsafe

		protected void PulseMessageLoop()
		{
			lock (tasksFilled)
				tasksFilled.Set();
		} // proc PulseMessageLoop

		protected void ProcessMessageLoopUnsafe(CancellationToken cancellationToken)
		{
			// if cancel, then run the loop, we avoid an TaskCanceledException her
			cancellationToken.Register(PulseMessageLoop);

			// process messages until cancel
			while (!cancellationToken.IsCancellationRequested && IsRunning)
			{
				// process queue
				while (TryDequeueTask(cancellationToken, out var d, out var state, out var wait))
				{
					try
					{
						d(state);
					}
					finally
					{
						if (wait != null)
							wait.Set();
					}
				}

				// wait for event
				if (!cancellationToken.IsCancellationRequested)
					tasksFilled.Wait();
			}
		} // proc ProcessMessageLoop

		/// <summary>Run the message loop until the task is completed.</summary>
		/// <param name="onCompletion"></param>
		public void ProcessMessageLoop(INotifyCompletion onCompletion)
		{
			using (var cancellationTokenSource = new CancellationTokenSource())
			{
				onCompletion.OnCompleted(cancellationTokenSource.Cancel);
				ProcessMessageLoop(cancellationTokenSource.Token);
			}
		} // proc ProcessMessageLoop

		/// <summary>Run the message loop until is canceled.</summary>
		/// <param name="cancellationToken"></param>
		public void ProcessMessageLoop(CancellationToken cancellationToken)
		{
			VerifyThreadAccess();
			ProcessMessageLoopUnsafe(cancellationToken);
		} // proc ProcessMessageLoop

		protected object MessageLoopSync => tasksFilled;

		#endregion

		#region -- Execute --------------------------------------------------------------

		private void Execute(object obj)
		{
			var lastExceptionTick = 0;	// last time an exception get caught
			var exceptionCount = 0;		// exception counter for the last window

			var threadStarted = (ManualResetEventSlim)obj;

			SynchronizationContext.SetSynchronizationContext(context);

			// mark start of the thread
			log.Start(thread.Name);
			RegisterThread(this);
			try
			{
				threadStarted.Set();
				threadStarted = null;
				do
				{
					void CancelThread()
					{
						log.Abort(Thread.CurrentThread.Name);
						if (propertyRunning != null)
							propertyRunning.Value = "Abgebrochen";
					} // proc CancelThread

					try
					{
						ProcessMessageLoop(threadCancellation.Token);
					}
					catch (TaskCanceledException)
					{
						CancelThread();
						return;
					}
					catch (ThreadAbortException)
					{
						CancelThread();
						return;
					}
					catch (Exception e)
					{
						if (!OnHandleException(e))
						{
							if (unchecked(Environment.TickCount - lastExceptionTick) > 500)
								exceptionCount = 0;

							lastExceptionTick = Environment.TickCount;
							exceptionCount++;

							if (exceptionCount > 3)
							{
								propertyRunning.Value = "Error";
								Thread.CurrentThread.Abort(); // more then 3 exeptions in 1500 ms is a clear fail
							}
							else
							{
								log.LogMsg(LogMsgType.Error,
									String.Format("Thread '{0}' restarted: {1}" + Environment.NewLine + Environment.NewLine +
										"{2}", thread.Name, exceptionCount, e.GetMessageString()));
								Thread.Sleep(100);
							}
						}
						else
							exceptionCount = 0;

						propertyRestarts.Value += 1;
					}
				} while (IsRunning);
			}
			catch (TaskCanceledException) { }
			catch (ThreadAbortException) { }
			finally
			{
				// stop the thread
				log.Stop(thread.Name);
				propertyRunning.Value = "Beendet";
				UnregisterThread(this);
				SynchronizationContext.SetSynchronizationContext(null);
				OnThreadFinished();
			}
		} // proc Execute

		protected virtual void OnThreadFinished() { }

		protected virtual bool OnHandleException(Exception e)
			=> false;

		#endregion

		public object GetService(Type serviceType)
			 => sp?.GetService(serviceType);

		/// <summary>Managed thread id</summary>
		public int ManagedThreadId => thread.ManagedThreadId;
		/// <summary>Name of the thread.</summary>
		public string Name => thread.Name;
		/// <summary>Current state of this thread.</summary>
		public bool IsRunning => !isDisposed && !threadCancellation.IsCancellationRequested;
		/// <summary></summary>
		public bool IsDisposed => isDisposed;
		/// <summary>Attached log context.</summary>
		public LoggerProxy Log => log;
		/// <summary>Root context of this thread.</summary>
		public DEThreadContext RootContext => context;
		/// <summary>Context of the thread</summary>
		public DEThreadContext CurrentContext
		{
			get
			{
				DEThreadContext r = null;
				ExecutionContext.Run(thread.ExecutionContext, s => r = SynchronizationContext.Current as DEThreadContext, null);
				return r ?? RootContext;
			}
		} // prop CurrentContext

		public CancellationToken CancellationToken => threadCancellation.Token;

		// -- Static --------------------------------------------------------------------

		/// <summary>Notifies changes on the thread list.</summary>
		public static event EventHandler ThreadListChanged;

		private static readonly List<DEThreadBase> runningDEThreads = new List<DEThreadBase>();

		private static int lastZombieCheck = 0;

		private static void RemoveZombies()
		{
			if (unchecked(Environment.TickCount - lastZombieCheck) > 500)
			{
				for (var i = runningDEThreads.Count - 1; i >= 0; i--)
				{
					if (runningDEThreads[i].thread.ThreadState == ThreadState.Stopped)
						runningDEThreads.RemoveAt(i);
				}

				lastZombieCheck = Environment.TickCount;
			}
		} // proc RemoveZombies
		
		private static void RegisterThread(DEThreadBase thread)
		{
			if (thread == null)
				throw new ArgumentNullException("thread");

			lock (runningDEThreads)
			{
				// remove zombies
				RemoveZombies(); 
				// append a de-managed thread
				runningDEThreads.Add(thread);

				ThreadListChanged?.Invoke(null, EventArgs.Empty);
			}
		} // proc RegisterThread

		public static DEThreadBase FromThread(Thread thread)
		{
			lock (runningDEThreads)
				return runningDEThreads.Find(cur => cur.thread == thread);
		} // func FromThread
		
		private static void UnregisterThread(DEThreadBase thread)
		{
			lock (runningDEThreads)
			{
				RemoveZombies(); // remove zombies
								 // remove de-managed thread
				runningDEThreads.Remove(thread);

				ThreadListChanged?.Invoke(null, EventArgs.Empty);
			}
		} // proc UnregisterThread

		public static DEThreadBase CurrentThread => SynchronizationContext.Current is DEThreadContext t ? t.Thread : null;

		public static int ThreadCount { get { lock (runningDEThreads) return runningDEThreads.Count; } }
		public static DEThreadBase[] ThreadList { get { lock (runningDEThreads) return runningDEThreads.ToArray(); } }
	} // class DEContextThread

	#endregion

	#region -- class DEThread -----------------------------------------------------------

	/// <summary>Thread to handle thread exceptions.</summary>
	/// <param name="e">Exception.</param>
	/// <returns><c>true</c>, if the exception is accepted.</returns>
	public delegate bool ThreadExceptionDelegate(Exception e);

	///////////////////////////////////////////////////////////////////////////////
	/// <summary>Safety thread that runs a delegate in a loop.</summary>
	public sealed class DEThread : DEThreadBase
	{
		#region -- struct CurrentTaskItem -----------------------------------------------

		private struct CurrentTaskItem
		{
			public SendOrPostCallback Callback;
			public object State;
			public ManualResetEventSlim Wait;
		} // struct CurrentTaskItem

		#endregion

		private readonly Queue<CurrentTaskItem> currentTasks = new Queue<CurrentTaskItem>();
		
		public DEThread(IServiceProvider sp, string name, Func<Task> action, string categoryName = ThreadCategory)
			: base(sp, name, categoryName)
		{
			if (action != null)
			{
				EnqueueTask(
				  s => ((Func<Task>)s).Invoke().GetAwaiter().OnCompleted(Dispose),
				  action,
				  null);
			}
		} // ctor

		protected override void SetThreadParameter(Thread thread)
		{
			base.SetThreadParameter(thread);
			thread.IsBackground = true;
		} // proc SetThreadParameter

		protected override void EnqueueTask(SendOrPostCallback d, object state, ManualResetEventSlim waitHandle)
		{
			lock (MessageLoopSync)
			{
				currentTasks.Enqueue(new CurrentTaskItem() { Callback = d, State = state, Wait = waitHandle });
				PulseMessageLoop();
			}
		} // proc EnqueueTask

		protected override bool TryDequeueTask(CancellationToken cancellationToken, out SendOrPostCallback d, out object state, out ManualResetEventSlim wait)
		{
			lock (MessageLoopSync)
			{
				if (currentTasks.Count == 0)
				{
					ResetMessageLoopUnsafe();
					d = null;
					state = null;
					wait = null;
					return false;
				}
				else
				{
					var currentTask = currentTasks.Dequeue();
					d = currentTask.Callback;
					state = currentTask.State;
					wait = currentTask.Wait;
					return true;
				}
			}
		} // proc TryDequeueTask

		protected override bool OnHandleException(Exception e) 
			=> HandleException?.Invoke(e) ?? false;
	
		public ThreadExceptionDelegate HandleException { get; set; }
	} // class DEThread

	#endregion
	
	#region -- class Threading ----------------------------------------------------------

	public static class Threading
	{
		public static void AwaitTask(this Task task)
		{
			if (SynchronizationContext.Current is IDEThreadSource c && c.Thread != null)
			{
				var awaiter = task.GetAwaiter();
				if (awaiter.IsCompleted)
					return;
				c.Thread.ProcessMessageLoop(awaiter);
			}
			else
				task.Wait();
		} // proc AwaitTask

		public static T AwaitTask<T>(this Task<T> task)
		{
			if (SynchronizationContext.Current is IDEThreadSource c && c.Thread != null)
			{
				var awaiter = task.GetAwaiter();
				c.Thread.ProcessMessageLoop(awaiter);
				return awaiter.GetResult();
			}
			else
				return task.Result;
		} // func AwaitTask
	} // class Threading

	#endregion
}
