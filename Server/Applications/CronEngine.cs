using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel.Design;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TecWare.DE.Stuff;

namespace TecWare.DE.Server
{
	#region -- class DECronEngine -------------------------------------------------------

	///////////////////////////////////////////////////////////////////////////////
	/// <summary></summary>
	internal sealed class DECronEngine : DEConfigLogItem, IDECronEngine
	{
		#region -- class CurrentRunningJob ------------------------------------------------

		///////////////////////////////////////////////////////////////////////////////
		/// <summary>Currently, running job.</summary>
		private sealed class CurrentRunningJob
		{
			private DECronEngine parent;
			private ICronJobExecute job;
			private ICronJobCancellation jobCancel;

			private Task task;
			private CancellationTokenSource cancellationTokenSource;

			public CurrentRunningJob(DECronEngine parent, ICronJobExecute job, CancellationToken cancellationToken)
			{
				this.parent = parent;
				this.job = job;
				this.jobCancel = job as ICronJobCancellation;

				this.cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
				this.task = Task.Factory.StartNew(Execute, cancellationTokenSource.Token);
				this.task.ContinueWith(EndExecute);

				// automatic cancel
				if (jobCancel != null && jobCancel.RunTimeSlice.HasValue)
					cancellationTokenSource.CancelAfter(jobCancel.RunTimeSlice.Value);
			} // ctor

			private void Execute()
				=> job.RunJob(cancellationTokenSource.Token);

			private void EndExecute(Task t)
			{
				Exception jobException = null;
				try
				{
					t.Wait(cancellationTokenSource.Token);
				}
				catch (Exception e)
				{
					jobException = e;
				}
				try
				{
					parent.FinishJob(this, jobException);
				}
				catch (Exception e)
				{
					parent.Log.Except("JobFinish failed.", e);
				}
			} // proc EndExecute

			public void Cancel()
			{
				cancellationTokenSource.Cancel();
				if (jobCancel != null && jobCancel.IsSupportCancelation)
					cancellationTokenSource.Cancel();
			} // proc Cancel

			public ICronJobExecute Job => job;
			public Task Task => task;
			public bool IsCancellationRequested => cancellationTokenSource.IsCancellationRequested;
		} // class CurrentRunningJob

		#endregion

		#region -- struct CronCacheItem ---------------------------------------------------

		///////////////////////////////////////////////////////////////////////////////
		/// <summary></summary>
		private struct CronCacheItem
		{
			public ICronJobItem Job;
			public DateTime? NextRun;
		} // struct CronCacheItem

		#endregion

		#region -- class CronItemCacheDescriptor ------------------------------------------

		///////////////////////////////////////////////////////////////////////////////
		/// <summary></summary>
		private sealed class CronItemCacheDescriptor : IDEListDescriptor
		{
			private CronItemCacheDescriptor()
			{
			} // ctor

			public void WriteType(DEListTypeWriter xml)
			{
				xml.WriteStartType("item");
				xml.WriteProperty("@id", typeof(string));
				xml.WriteProperty("@displayname", typeof(string));
				xml.WriteProperty("@bound", typeof(string));
				xml.WriteProperty("@supportsCancellation", typeof(bool));
				xml.WriteProperty("@runTimeSlice", typeof(TimeSpan));
				xml.WriteProperty("@nextrun", typeof(DateTime));
				xml.WriteEndType();
			} // proc WriteType

			public void WriteItem(DEListItemWriter xml, object item)
			{
				var c = (CronCacheItem)item;

				xml.WriteAttributeProperty("id", c.Job.UniqueName);
				xml.WriteAttributeProperty("displayname", c.Job.DisplayName);
				xml.WriteAttributeProperty("bound", c.Job.Bound.ToString());
				var jobCancel = c.Job as ICronJobCancellation;
				if (jobCancel != null)
				{
					xml.WriteAttributeProperty("supportsCancellation", jobCancel.IsSupportCancelation);
					if (jobCancel.RunTimeSlice.HasValue)
						xml.WriteAttributeProperty("runTimeSlice", jobCancel.RunTimeSlice.Value);
				}
				if (c.NextRun.HasValue)
					xml.WriteAttributeProperty("nextrun", c.NextRun.Value);
			} // proc WriteItem

			public static CronItemCacheDescriptor Instance { get; } = new CronItemCacheDescriptor();
		} // class CronItemCacheDescriptor

		#endregion

		#region -- class CronItemCacheController ------------------------------------------

		///////////////////////////////////////////////////////////////////////////////
		/// <summary></summary>
		private sealed class CronItemCacheController : IDEListController
		{
			private DECronEngine owner;

			public CronItemCacheController(DECronEngine owner)
			{
				this.owner = owner;
				owner.RegisterList(Id, this, true);
			} // ctor

			public void Dispose()
			{
				owner.UnregisterList(this);
			} // proc Dispose

			public IDisposable EnterReadLock()
			{
				Monitor.Enter(owner.cronItemCacheLock);
				return new DisposableScope(() => Monitor.Exit(owner.cronItemCacheLock));
			} // proc EnterReadLock

			public IDisposable EnterWriteLock()
				=> EnterReadLock();

			public void OnBeforeList() { }

			public IDEListDescriptor Descriptor => CronItemCacheDescriptor.Instance;

			public string Id => "tw_cron_items";
			public string DisplayName => "Cron items";

			public IEnumerable List => owner.cronItemCache;
		} // class CronItemCacheController 

		#endregion

		private DEList<CurrentRunningJob> currentJobs;

		private IDEListController cronItemCacheController;
		private object cronItemCacheLock = new object();
		private CronCacheItem[] cronItemCache = null;

		private Action procCronIdle;
		private Action procCancelJobs;
		private Action procRefreshCronServices;

		#region -- Ctor/Dtor --------------------------------------------------------------

		public DECronEngine(IServiceProvider sp, string name)
			: base(sp, name)
		{
			this.cronItemCacheController = new CronItemCacheController(this);
			this.currentJobs = new DEList<CurrentRunningJob>(this, "tw_cron_running", "Cron running");
			
			this.procCronIdle = CronIdle;
			PublishItem(this.currentJobs);

			// Register Engine
			var sc = sp.GetService<IServiceContainer>(true);
			sc.AddService(typeof(IDECronEngine), this, false);

			// Register Server events
			Server.Queue.RegisterEvent(procCancelJobs = CancelJobs, DEServerEvent.Shutdown);
			Server.Queue.RegisterEvent(procRefreshCronServices = RefreshCronServices, DEServerEvent.Reconfiguration);
    } // ctor

		protected override void Dispose(bool disposing)
		{
			try
			{
				if (disposing)
				{
					CancelJobs();

					Server.Queue.CancelCommand(procCronIdle);
					Server.Queue.CancelCommand(procCancelJobs);
					Server.Queue.CancelCommand(procRefreshCronServices);

					this.GetService<IServiceContainer>(false)?.RemoveService(typeof(IDECronEngine));

					Procs.FreeAndNil(ref currentJobs);
					Procs.FreeAndNil(ref cronItemCacheController);
				}
			}
			finally
			{
				base.Dispose(disposing);
			}
		} // proc Dispose

		#endregion

		#region -- Configuration ----------------------------------------------------------

		private static void CollectCronJobItems(List<ICronJobItem> cronItems, DEConfigItem current)
		{
			if (current is ICronJobItem)
				cronItems.Add((ICronJobItem)current);
			else // No recursion, for nested cron jobs
			{
				foreach (var c in current.UnsafeChildren)
					CollectCronJobItems(cronItems, c);
			}
		} // proc CollectCronJobItems

		public void RefreshCronServices()
		{
			// Collect all cronjobs
			var cronItems = new List<ICronJobItem>();
			CollectCronJobItems(cronItems, this.GetService<DEServer>(true));

			Log.LogMsg(LogMsgType.Information, "CronJobs found: {0}", cronItems.Count);

			if (cronItems.Count > 0)
			{
				if (cronItemCache == null) // first initialization, start idle
					Server.Queue.RegisterIdle(procCronIdle);


				// Lies die Liste mit den zuletzt gelaufenen Zeiten und errechne den nächsten Start
				lock (cronItemCacheLock)
				{
					cronItemCache = new CronCacheItem[cronItems.Count];
					for (var i = 0; i < cronItemCache.Length; i++)
						cronItemCache[i].Job = cronItems[i];

					LoadNextRuntime();
				}
			}
			else if (cronItemCache != null && cronItems.Count == 0) // Items komplett entfernt
			{
				Server.Queue.CancelCommand(procCronIdle);
				lock (cronItemCacheLock)
					cronItemCache = null;
			}
		} // proc RefreshCronServices

		#endregion

		#region -- Last Run Time ----------------------------------------------------------

		private void LoadNextRuntime()
		{
			string line;

			// read persisted data
			using (var log = this.Log.GetScope(LogMsgType.Information))
			{
				try
				{
					log.WriteLine("Reread job table.");

					using (var sr = new StreamReader(NextRuntimeFile))
					{
						while ((line = sr.ReadLine()) != null)
						{
							// # Displayname
							// id: zeit

							// Skip comment
							line = line.Trim();
							if (line.Length > 0 && line[0] == '#')
								continue;

							int iPos = line.IndexOf(' ');
							if (iPos == -1)
								continue;

							// read line data
							var uniqueName = line.Substring(0, iPos).Trim();
							var timeStamp = line.Substring(iPos + 1).Trim();
							try
							{
								var nextStamp = DateTime.Parse(timeStamp);
								var index = Array.FindIndex(cronItemCache, c => String.Compare(c.Job.UniqueName, uniqueName, StringComparison.OrdinalIgnoreCase) == 0);
								if (index >= 0)
									cronItemCache[index].NextRun = nextStamp;
								else
									log.WriteLine("{0}: Nicht mehr gefunden.", uniqueName);
							}
							catch (Exception e)
							{
								log.SetType(LogMsgType.Warning);
								log.WriteLine("[{0}] {1} bei Zeile: {2}", e.GetType().Name, e.Message, line);
							}

						}
					}
				}
				catch (FileNotFoundException ex)
				{
					log.WriteLine(ex.Message);
					log.WriteLine("The schedule will reset to start.");
					log.SetType(LogMsgType.Warning, true);
				}
				catch (Exception ex)
				{
					log.WriteException(ex);
					log.SetType(LogMsgType.Warning, true);
				}
			}

			// recalculate times
			for (int i = 0; i < cronItemCache.Length; i++)
			{
				if (cronItemCache[i].NextRun.HasValue)
					continue;

				if (!cronItemCache[i].Job.Bound.IsEmpty)
				{
					var nextStamp = cronItemCache[i].Job.Bound.GetNext(DateTime.Now);
					cronItemCache[i].Job.NotifyNextRun(nextStamp);
					cronItemCache[i].NextRun = nextStamp;
        }
			}
		} // proc LoadNextRuntime

		private void SaveNextRuntime()
		{
			lock (cronItemCacheLock)
			{
				using (var sw = new StreamWriter(NextRuntimeFile))
				{
					for (int i = 0; i < cronItemCache.Length; i++)
					{
						if (cronItemCache[i].NextRun.HasValue)
						{
							sw.WriteLine("# {0}", cronItemCache[i].Job.DisplayName);
							sw.WriteLine("{0}: {1}", cronItemCache[i].Job.UniqueName, cronItemCache[i].NextRun.Value);
						}
					}
				}
			}
		} // proc SaveNextRuntime

		private string NextRuntimeFile => Path.ChangeExtension(this.LogFileName, "next");

		#endregion

		#region -- Job-Verwaltung ---------------------------------------------------------

		private void CronIdle()
		{
			// Check, if need to start something
			using (EnterReadLock())
			{
				lock (cronItemCache)
				{
					if (cronItemCache != null)
					{
						var now = DateTime.Now;
						for (int i = 0; i < cronItemCache.Length; i++)
						{
							if (cronItemCache[i].NextRun.Value < now)
							{
								if (!StartJob(cronItemCache[i].Job))
								{
									cronItemCache[i].NextRun = cronItemCache[i].NextRun.Value.Add(TimeSpan.FromSeconds(60)); // add 60sec if the job is busy
								}
							}
						}
					}
				}
			}
		} // proc CronIdle

		public bool StartJob(ICronJobExecute job)
		{
			// is the job currently running
			try
			{
				ExecuteJobAsync(job, CancellationToken.None);
				return true;
			}
			catch (InvalidOperationException)
			{
				return false;
			}
		} // proc StartJob

		public Task ExecuteJobAsync(ICronJobExecute job, CancellationToken cancellation)
		{
			using (currentJobs.EnterWriteLock())
			{
				if (currentJobs.FindIndex(c => job == c.Job) >= 0)
					throw new InvalidOperationException("Job is already running.");

				using (currentJobs.EnterReadLock())
				{
					foreach (var j in currentJobs)
						if (!job.CanRunParallelTo(j.Job))
							throw new InvalidOperationException(String.Format("Job is blocked (job: {0})", j.Job.DisplayName));
				}

				Log.Info("jobstart: {0}", job.DisplayName);
				var currentJob = new CurrentRunningJob(this, job, cancellation);
				currentJobs.Add(currentJob);
				return currentJob.Task;
			}
		} // func ExecuteJobAsync

		private void FinishJob(CurrentRunningJob jobRunning, Exception jobException)
		{
			lock(cronItemCache)
			{
				using (EnterReadLock())
				using (currentJobs.EnterWriteLock())
				{
					var jobBound = jobRunning.Job as ICronJobItem;

					// remove job from running jobs
					currentJobs.Remove(jobRunning);

					// generate log entry
					var name = jobBound == null ? "<unnamed>" : jobBound.DisplayName;
					if (jobException != null)
					{
						Log.Except(String.Format("jobfinish: {0}", name), jobException);
						var node = jobRunning.Job as DEConfigLogItem;
						if (node != null)
						{
							var aggException = jobException as AggregateException;
							if (aggException != null)
							{
								if (aggException.InnerException != null)
									node.Log.Except("Execution failed.", aggException.InnerException);
								foreach (var ex in aggException.InnerExceptions)
									node.Log.Except("Execution failed.", ex);
							}
							else
								node.Log.Except("Execution failed.", jobException);
						}
					}
					else
						Log.Info("jobfinish: {0}", name);

					// calculate next runtime
					if (jobBound != null && !jobBound.Bound.IsEmpty && cronItemCache != null)
					{
						var index = Array.FindIndex(cronItemCache, c => c.Job == jobBound);
						if (index >= 0)
						{
							DateTime dtNext = jobBound.Bound.GetNext(DateTime.Now);
							cronItemCache[index].NextRun = dtNext;
							jobBound.NotifyNextRun(dtNext);
							SaveNextRuntime();
						}
					}
				}
			}
		} // proc FinishJob

		public void CancelJob(ICronJobExecute job)
		{
			Task t = null;
			using (currentJobs.EnterReadLock())
			{
				var cur = currentJobs.FirstOrDefault(c => c.Job == job);
				if (cur != null)
				{
					t = cur.Task;
					cur.Cancel(); }
			}
			try { t.Wait(); }
			catch { }
		}// proc CancelJob

		public void CancelJobs()
		{
			var tasks = new List<Task>();
			using (currentJobs.EnterReadLock())
			{
				foreach (var c in currentJobs)
				{
					tasks.Add(c.Task);
					c.Cancel();
				}
			}
			Task.WaitAll(tasks.ToArray());
		} // proc CancelJobs

		#endregion

		public override string Icon { get { return "/images/clock.png"; } }
	} // class DECronEngine

	#endregion

	#region -- class LuaCronJobItem -----------------------------------------------------

	///////////////////////////////////////////////////////////////////////////////
	/// <summary></summary>
	internal sealed class LuaCronJobItem : CronJobItem
	{
		#region -- Ctor/Dtor ----------------------------------------------------------------

		public LuaCronJobItem(IServiceProvider sp, string sName)
			: base(sp, sName)
		{
		} // ctor

		#endregion

		#region -- OnRunJob -----------------------------------------------------------------

		private CancellationToken cancellationToken = CancellationToken.None;

		protected override void OnRunJob(CancellationToken cancellation)
		{
			cancellationToken = cancellation;
			cancellation.Register(OnCancel);
			CallMember("Run", cancellation);
		} // proc OnRunJob

		private void OnCancel()
		{
			CallMember("Cancel");
		} // proc OnCancel

		public override bool IsSupportCancelation => Config.GetAttribute("supportsCancelation", false) || this.GetMemberValue("Cancel", lRawGet: true) != null;

		#endregion

		public override string Icon => "/images/clock_run.png";
	} // class LuaCronJobItem

	#endregion

	#region -- class CronJobGroupBatch --------------------------------------------------

	///////////////////////////////////////////////////////////////////////////////
	/// <summary>Run a group of jobs as an batch.</summary>
	internal sealed class CronJobGroupBatch : CronJobItem
	{
		public CronJobGroupBatch(IServiceProvider sp, string name)
			: base(sp, name)
		{
		} // ctor

		protected override void OnRunJob(CancellationToken cancellation)
		{
			CheckCronEngine();

			this.WalkChildren<ICronJobExecute>(
				c =>
				{
					if (cancellation.IsCancellationRequested)
						return;

					if (StateRunning != null)
						StateRunning.Value = c.DisplayName;
					Log.Info("{0}: started...", c.DisplayName);
					CronEngine.ExecuteJobAsync(c, cancellation);
					Log.Info("{0}: finished.", c.DisplayName);
				}, true);
		} // proc RunJob

		public override string Icon => "/images/clock_data.png";
	} // class CronJobGroupBatch

	#endregion

	#region -- class CronJobGroupStart --------------------------------------------------

	///////////////////////////////////////////////////////////////////////////////
	/// <summary>Run a group of jobs parallel.</summary>
	internal sealed class CronJobGroupStart : CronJobItem
	{
		public CronJobGroupStart(IServiceProvider sp, string name)
			: base(sp, name)
		{
		} // ctor

		protected override void OnRunJob(CancellationToken cancellation)
		{
			CheckCronEngine();

			using (EnterReadLock())
			{
				var tasks = new List<Task>();
				Log.Info("Start tasks...");
				this.WalkChildren<ICronJobExecute>(
					c =>
					{
						Log.Info("{0}: Started...", c.DisplayName);
						tasks.Add(CronEngine.ExecuteJobAsync(c, cancellation));
					}, true, true);


				Task.WaitAll(tasks.ToArray(), cancellation);
				Log.Info("{0}: All tasks finished.");
			}
		} // proc RunJob

		public override string Icon => "/images/clock_gearwheel.png";
	} // class CronJobGroupStart

	#endregion
}
