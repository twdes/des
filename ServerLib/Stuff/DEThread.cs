using System;
using System.Threading;
using System.Collections.Generic;
using System.Threading.Tasks;
using TecWare.DE.Stuff;

namespace TecWare.DE.Server
{
	#region -- class DEThreadBase -------------------------------------------------------

	/////////////////////////////////////////////////////////////////////////////
	/// <summary>Basisklasse für alle Threads in der Anwendung. Es wird eine 
	/// Log-Datei verwaltet und er startet im Fehlerfall automatisch wieder
	/// neu.</summary>
	public abstract class DEThreadBase : IServiceProvider, IDisposable
	{
		public const string ThreadCategory = "Thread";

		private Thread thread = null;
		private ManualResetEventSlim startedEvent;
		private ManualResetEventSlim stoppingEvent;

		private IServiceProvider sp;
		private LoggerProxy log;
		private SimpleConfigItemProperty<int> propertyRestarts = null;
		private SimpleConfigItemProperty<string> propertyRunning = null;

		#region -- Ctor/Dtor --------------------------------------------------------------

		public DEThreadBase(IServiceProvider sp, string name, string categoryName = ThreadCategory)
		{
			this.sp = sp;

			if (String.IsNullOrEmpty(name))
				throw new ArgumentNullException("name");

			// create the log information
			this.log = sp.LogProxy();
			if (log == null)
				throw new ArgumentNullException("log", "Service provider must provide logging.");

			propertyRestarts = new SimpleConfigItemProperty<int>(sp,
				$"tw_thread_restarts_{name}",
				$"{name} - Neustarts",
				categoryName,
				"Anzahl der Neustarts des Threads.",
				"{0:N0}",
				0
			);
			propertyRunning = new SimpleConfigItemProperty<string>(sp,
				$"tw_thread_running_{name}",
				$"{name} - Aktiv",
				categoryName,
				"Ist der Thread noch aktiv.",
				null,
				"Läuft"
			);
		} // ctor

		protected void StartThread(string name, ThreadPriority priority = ThreadPriority.Normal, bool isBackground = false, ApartmentState apartmentState = ApartmentState.MTA)
		{
			// create the thread
			thread = new Thread(Execute);
			thread.SetApartmentState(apartmentState);
			thread.IsBackground = isBackground;
			thread.Priority = priority;
			thread.Name = name;

			// start the thread
			startedEvent = new ManualResetEventSlim(false);
			stoppingEvent = new ManualResetEventSlim(false);
			thread.Start();
			if (!startedEvent.Wait(3000))
				throw new Exception(String.Format("Could not start thread '{0}'.", name));

			Procs.FreeAndNil(ref startedEvent);
		} // proc StartThread

		~DEThreadBase()
		{
			Dispose(false);
		} // dtor

		public void Dispose()
		{
			GC.SuppressFinalize(this);
			Dispose(true);
		} // proc Dispose

		protected virtual void Dispose(bool disposing)
		{
			stoppingEvent?.Set();
			if (disposing)
			{
				// stop the thread
				if (!thread.Join(3000))
					thread.Abort();
				thread = null;

				// clear objects
				Procs.FreeAndNil(ref stoppingEvent);

				// Remove properties
				Procs.FreeAndNil(ref propertyRestarts);
				Procs.FreeAndNil(ref propertyRunning);
			}
		} // proc Dispose


		#endregion

		#region -- Execute ----------------------------------------------------------------

		private void Execute()
		{
			var lastExceptionTick = 0;    // Wann wurde die Letzte Exeption abgefangen
			var exceptionCount = 0;   // Wieviele wurde im letzten Zeitfenster gefangen
			
			// mark start of the thread
			log.Start(thread.Name);
			RegisterThread(this);
			try
			{
				startedEvent?.Set();
				do
				{
					try
					{
						ExecuteLoop();
					}
					catch (ThreadAbortException)
					{
						log.Abort(Thread.CurrentThread.Name);
						propertyRunning.Value = "Abgebrochen";
						UnregisterThread();
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
								propertyRunning.Value = "Fehlerhaft";
								Thread.CurrentThread.Abort(); // mehr als 3 Exeption in 1500 ms ist fatal
							}
							else
							{
								log.LogMsg(LogMsgType.Error,
									String.Format("Thread '{0}' neugestartet: {1}" + Environment.NewLine + Environment.NewLine +
										"{2}", thread.Name, exceptionCount, e.GetMessageString()));
								Thread.Sleep(100);
							}
						}
						else
							exceptionCount = 0;

						propertyRestarts.Value += 1;
					}
				} while (IsRunning);

				// stop the thread
				log.Stop(thread.Name);
				propertyRunning.Value = "Beendet";
				UnregisterThread(this);
			}
			catch (ThreadAbortException)
			{
				UnregisterThread(this);
			}
		} // proc Execute

		protected abstract void ExecuteLoop();

		protected virtual bool OnHandleException(Exception e)
		{
			return false;
		} // proc OnHandleException

		#endregion

		#region -- IServiceProvider members -----------------------------------------------

		public virtual object GetService(Type serviceType) => sp?.GetService(serviceType);

		#endregion

		/// <summary>Wait for stop.</summary>
		/// <param name="millisecondsTimeout"></param>
		/// <returns></returns>
		public bool WaitFinish(int millisecondsTimeout = -1) => stoppingEvent.Wait(millisecondsTimeout);
		/// <summary>Gets a WaitHandle for the stop event.</summary>
		public ManualResetEventSlim StoppingEvent => stoppingEvent;

		/// <summary></summary>
		public int ManagedThreadId => thread?.ManagedThreadId ?? -1;
		/// <summary>Name des Threads.</summary>
		public string Name => thread.Name;
		/// <summary>Status des Threads.</summary>
		public bool IsRunning => stoppingEvent != null && !stoppingEvent.IsSet;
		/// <summary>Attached log file.</summary>
		public LoggerProxy Log => log;

		// -- Static --------------------------------------------------------------

		/// <summary>Notifies changes on the thread list.</summary>
		public static event EventHandler ThreadListChanged;

		private static readonly List<Thread> runningThreads = new List<Thread>();
		private static readonly List<DEThreadBase> runningDEThreads = new List<DEThreadBase>();

		private static int lastZombieCheck = 0;

		private static void RemoveZombies()
		{
			if (unchecked(Environment.TickCount - lastZombieCheck) > 500)
			{
				for (var i = runningThreads.Count - 1; i >= 0; i--)
				{
					if (runningThreads[i].ThreadState == ThreadState.Stopped)
						runningThreads.RemoveAt(i);
				}

				lastZombieCheck = Environment.TickCount;
			}
		} // proc RemoveZombies

		public static void RegisterThread() => AddRunningThread(Thread.CurrentThread);

		private static void AddRunningThread(Thread thread)
		{
			lock (runningThreads)
			{
				RemoveZombies(); // remove zombies

				// add the thread
				if (runningThreads.IndexOf(thread) == -1)
				{
					runningThreads.Add(thread);

					// Notify list change
					ThreadListChanged?.Invoke(null, EventArgs.Empty);
				}
			}
		} // proc AddRunningThread

		private static void RegisterThread(DEThreadBase thread)
		{
			if (thread == null)
				throw new ArgumentNullException("thread");

			lock (runningThreads)
			{
				// append a de-managed thread
				runningDEThreads.Add(thread);
				AddRunningThread(thread.thread);
			}
		} // proc RegisterThread

		public static DEThreadBase FromThread(Thread thread)
		{
			lock (runningThreads)
				return runningDEThreads.Find(cur => cur.thread == thread);
		} // func FromThread

		public static void UnregisterThread() => UnregisterRunningThread(Thread.CurrentThread);

		private static void UnregisterRunningThread(Thread thread)
		{
			lock (runningThreads)
			{
				RemoveZombies(); // remove zombies

				// remove the thread
				runningThreads.Remove(Thread.CurrentThread);

				// Notify list change
				if (ThreadListChanged != null)
					ThreadListChanged(null, EventArgs.Empty);
			}
		} // proc UnregisterRunningThread

		private static void UnregisterThread(DEThreadBase thread)
		{
			lock (runningThreads)
			{
				// remove de-managed thread
				runningDEThreads.Remove(thread);
				UnregisterRunningThread(thread.thread);
			}
		} // proc UnregisterThread

		public static DEThreadBase CurrentThread { get { return FromThread(Thread.CurrentThread); } }

		public static int ThreadCount { get { lock (runningThreads) return runningThreads.Count; } }
		public static Thread[] ThreadList { get { lock (runningThreads) return runningThreads.ToArray(); } }
	} // class class DEThreadBase

	#endregion

	#region -- class DEThreadLoop -------------------------------------------------------

	///////////////////////////////////////////////////////////////////////////////
	/// <summary>Safety thread that implements a scheduler for async task queue.</summary>
	public class DEThreadLoop : DEThreadBase
	{
		#region -- class ThreadTaskScheduler ----------------------------------------------

		///////////////////////////////////////////////////////////////////////////////
		/// <summary></summary>
		private sealed class ThreadTaskScheduler : TaskScheduler
		{
			private DEThreadLoop thread;
			private readonly LinkedList<Task> tasks = new LinkedList<Task>();
			private readonly ManualResetEventSlim taskFilled = new ManualResetEventSlim(false);

			#region -- Ctor/Dtor ------------------------------------------------------------

			public ThreadTaskScheduler(DEThreadLoop thread)
			{
				this.thread = thread;
			} // ctor

			#endregion

			#region -- Scheduler ------------------------------------------------------------

			public void Clear()
			{
				lock(tasks)
				{
					tasks.Clear();
					taskFilled.Dispose();
				}
			} // proc Clear

			protected override IEnumerable<Task> GetScheduledTasks()
			{
				var lockToken = false;
				try
				{
					Monitor.TryEnter(tasks, ref lockToken);
					if (lockToken)
					{
						var r = new Task[tasks.Count];
						tasks.CopyTo(r, 0);
						return r;
					}
					else
						throw new NotSupportedException();
				}
				finally
				{
					if (lockToken)
						Monitor.Exit(tasks);
				}
			} // func GetScheduledTasks

			protected override void QueueTask(Task task)
			{
				lock(tasks)
				{
					tasks.AddLast(task);
					taskFilled.Set();
				}
			} // proc QueueTask

			private Task DequueTask()
			{
				lock (tasks)
				{
					if (tasks.Count == 0)
						return null;

					var t = tasks.First.Value;
					tasks.RemoveFirst();

					if (tasks.Count == 0)
						taskFilled.Reset();

					return t;
				}
			} // func DequueTask


			protected override bool TryDequeue(Task task)
			{
				lock (tasks)
				{
					var r = tasks.Remove(task);
					if (r && tasks.Count == 0)
						taskFilled.Reset();
					return r;
				}
			} // func TryDequeue

			protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued)
			{
				lock(tasks)
				{
					if (taskWasPreviouslyQueued)
						tasks.Remove(task);

					QueueTask(task);
				}
				return false;
			} // func TryExecuteTaskInline

			#endregion

			#region -- ExecuteLoop ----------------------------------------------------------

			public void ExecuteLoop(int timeout)
			{
				var timeStart = Environment.TickCount;
				while (timeout == -1 || unchecked(Environment.TickCount - timeStart) < timeout)
				{
					var task = DequueTask();
					if (task != null)
						base.TryExecuteTask(task);
					else
						break;
				}
			} // proc Execute

			#endregion

			/// <summary>Is <c>true</c>, as long there are items in the queue.</summary>
			public ManualResetEventSlim FilledEvent => taskFilled;
		} // class ThreadTaskScheduler

		#endregion

		private readonly ThreadTaskScheduler scheduler;
		private readonly TaskFactory factory;
		private readonly CancellationTokenSource cancellationSource;

		public DEThreadLoop(IServiceProvider sp, string name, string categoryName = ThreadCategory, ThreadPriority priority = ThreadPriority.Normal)
			: base(sp, name, categoryName)
		{
			this.cancellationSource = new CancellationTokenSource();
			this.scheduler = new ThreadTaskScheduler(this);
      this.factory = new TaskFactory(cancellationSource.Token, TaskCreationOptions.AttachedToParent, TaskContinuationOptions.AttachedToParent, scheduler);

			StartThread(name, priority, true, ApartmentState.STA);
    } // ctor

		protected override void Dispose(bool disposing)
		{
			if (disposing)
			{
				cancellationSource.Cancel();
				scheduler.Clear();
			}
			base.Dispose(disposing);
		} // proc Dispose

		protected void ExecuteScheduler(int timeout) => scheduler.ExecuteLoop(timeout);
		
		protected override void ExecuteLoop()
		{
			ExecuteScheduler(-1);
			WaitHandle.WaitAny(
				new WaitHandle[]
				{
					StoppingEvent.WaitHandle,
					FilledEventHandle
				}
			);
		} // proc ExecuteLoop

		public TaskFactory Factory => factory;

		/// <summary>Waits for items in the queue.</summary>
		protected WaitHandle FilledEventHandle => scheduler.FilledEvent.WaitHandle;
	} // class DEThreadLoop

	#endregion

	#region -- class DEThread -----------------------------------------------------------

	/// <summary>Thread delegate</summary>
	public delegate void ThreadDelegate();
	/// <summary>Thread to handle thread exceptions.</summary>
	/// <param name="e">Exception.</param>
	/// <returns><c>true</c>, if the exception is accepted.</returns>
	public delegate bool ThreadExceptionDelegate(Exception e);

	///////////////////////////////////////////////////////////////////////////////
	/// <summary>Safety thread that runs a delegate in a loop.</summary>
	public sealed class DEThread : DEThreadBase
	{
		private readonly ThreadDelegate action;

		public DEThread(IServiceProvider sp, string name, ThreadDelegate action, string categoryName = ThreadCategory, ThreadPriority priority = ThreadPriority.Normal, bool isBackground = false, ApartmentState apartmentState = ApartmentState.MTA)
			:base(sp, name, categoryName)
		{
			this.action = action;

			StartThread(name, priority, isBackground, apartmentState);
    } // ctor

		protected override void ExecuteLoop() => action();

		protected override bool OnHandleException(Exception e) => HandleException?.Invoke(e) ?? false;

		public ThreadExceptionDelegate HandleException { get; set; }
	} // class DEThread

	#endregion

	#region -- class DEThreadList -------------------------------------------------------

	///////////////////////////////////////////////////////////////////////////////
	/// <summary></summary>
	public sealed class DEThreadList : IDisposable
  {
    private IServiceProvider sp;
    private string name;
    private string categoryName;
    private ThreadDelegate threadStart;

    private List<DEThread> threads = new List<DEThread>();

		#region -- Ctor/Dtor --------------------------------------------------------------

		public DEThreadList(IServiceProvider sp, string categoryName, string name, ThreadDelegate threadStart)
    {
      if (sp == null)
        throw new ArgumentNullException("sp");
      if (String.IsNullOrEmpty(categoryName))
        throw new ArgumentNullException("categoryName");
      if (String.IsNullOrEmpty(name))
        throw new ArgumentNullException("name");
      if (threadStart == null)
        throw new ArgumentNullException();

      this.sp = sp;
      this.categoryName = categoryName;
      this.name = name;

      this.threadStart = threadStart;
    } // ctor

    public void Dispose()
    {
      lock (threads)
			{
				while (threads.Count > 0)
				{
					threads[0].Dispose();
					threads.RemoveAt(0);
				}
			}
    } // proc Dispose

		#endregion

		public int Count
    {
      get { lock (threads) return threads.Count; }
      set
      {
        value = Math.Min(Math.Max(1, value), 1000);
        lock (threads)
        {
          while (value < threads.Count) // Zerstöre Threads
          {
            threads[threads.Count - 1].Dispose();
            threads.RemoveAt(threads.Count - 1);
          }
          while (value > threads.Count) // Erzeuge Worker
          {
						var currentName = name + threads.Count.ToString("000");
						threads.Add(new DEThread(sp, currentName, threadStart, categoryName));
          }
        }
      }
    } // prop Count
  } // class DEThreadList

	#endregion
}
