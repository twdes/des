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
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.Design;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Neo.IronLua;
using TecWare.DE.Networking;
using TecWare.DE.Stuff;

namespace TecWare.DE.Server
{
	#region -- class DECronEngine -------------------------------------------------------

	/// <summary></summary>
	internal sealed class DECronEngine : DEConfigLogItem, IDECronEngine
	{
		#region -- class CurrentRunningJob --------------------------------------------

		/// <summary>Currently, running job.</summary>
		private sealed class CurrentRunningJob
		{
			private readonly DECronEngine parent;
			private readonly ICronJobExecute job;
			private readonly ICronJobCancellation jobCancel;

			private readonly Task<bool> task;
			private readonly CancellationTokenSource cancellationTokenSource;

			public CurrentRunningJob(DECronEngine parent, ICronJobExecute job, CancellationToken cancellationToken)
			{
				this.parent = parent ?? throw new ArgumentNullException(nameof(parent));
				this.job = job ?? throw new ArgumentNullException(nameof(job));
				jobCancel = job as ICronJobCancellation;

				cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
				task = System.Threading.Tasks.Task.Factory
					.StartNew(Execute, cancellationTokenSource.Token)
					.ContinueWith(EndExecute);

				// automatic cancel
				if (jobCancel != null && jobCancel.RunTimeSlice.HasValue)
					cancellationTokenSource.CancelAfter(jobCancel.RunTimeSlice.Value);
			} // ctor

			private void Execute()
			{
				using var scope = new DECommonScope(job as IServiceProvider ?? parent, false, null);
				using (scope.Use())
				{
					job.RunJob(cancellationTokenSource.Token);
					scope.CommitAsync().AwaitTask();
				}
			} // proc Execute

			private bool EndExecute(Task t)
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
					return jobException == null;
				}
				catch (Exception e)
				{
					parent.Log.Except("JobFinish failed.", e);
					return false;
				}
			} // proc EndExecute

			public void Cancel()
			{
				cancellationTokenSource.Cancel();
				if (jobCancel != null && jobCancel.IsSupportCancelation)
					cancellationTokenSource.Cancel();
			} // proc Cancel

			public ICronJobExecute Job => job;
			public Task<bool> Task => task;
			public bool IsCancellationRequested => cancellationTokenSource.IsCancellationRequested;
		} // class CurrentRunningJob

		#endregion

		#region -- struct CronCacheItem -----------------------------------------------

		private struct CronCacheItem
		{
			public ICronJobItem Job;
			public DateTime? NextRun;
		} // struct CronCacheItem

		#endregion

		#region -- class CronItemCacheDescriptor --------------------------------------

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
				xml.WriteProperty("@lastRun", typeof(DateTime));
				xml.WriteProperty("@lastDuration", typeof(double));
				xml.WriteProperty("@failed", typeof(bool));
				xml.WriteEndType();
			} // proc WriteType

			public void WriteItem(DEListItemWriter xml, object item)
			{
				var c = (CronCacheItem)item;

				xml.WriteStartProperty("item");

				xml.WriteAttributeProperty("id", c.Job.UniqueName);
				xml.WriteAttributeProperty("displayname", c.Job.DisplayName);
				xml.WriteAttributeProperty("bound", c.Job.Bound.ToString());
				if (c.Job is ICronJobCancellation jobCancel)
				{
					xml.WriteAttributeProperty("supportsCancellation", jobCancel.IsSupportCancelation);
					if (jobCancel.RunTimeSlice.HasValue)
						xml.WriteAttributeProperty("runTimeSlice", jobCancel.RunTimeSlice.Value);
				}
				if (c.NextRun.HasValue)
					xml.WriteAttributeProperty("nextrun", c.NextRun.Value);

				if (c.Job is ICronJobInformation information)
				{
					if (information.LastRun.HasValue)
						xml.WriteAttributeProperty("lastRun", information.LastRun);
					xml.WriteAttributeProperty("lastDuration", information.LastDuration);
					xml.WriteAttributeProperty("failed", information.IsFailed);
				}

				xml.WriteEndProperty();
			} // proc WriteItem

			public static CronItemCacheDescriptor Instance { get; } = new CronItemCacheDescriptor();
		} // class CronItemCacheDescriptor

		#endregion

		#region -- class CronItemCacheController --------------------------------------

		private sealed class CronItemCacheController : IDEListController
		{
			private readonly DECronEngine owner;

			public CronItemCacheController(DECronEngine owner)
			{
				this.owner = owner ?? throw new ArgumentNullException(nameof(owner));
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
			public string SecurityToken => SecuritySys;

			public IEnumerable List => owner.cronItemCache;
		} // class CronItemCacheController 

		#endregion

		private readonly DEList<CurrentRunningJob> currentJobs;

		private readonly IDEListController cronItemCacheController;
		private readonly object cronItemCacheLock = new object();
		private CronCacheItem[] cronItemCache = null;

		private bool isCronIdleActive = false;

		#region -- Ctor/Dtor ----------------------------------------------------------

		public DECronEngine(IServiceProvider sp, string name)
			: base(sp, name)
		{
			cronItemCacheController = new CronItemCacheController(this);
			currentJobs = new DEList<CurrentRunningJob>(this, "tw_cron_running", "Cron running");

			PublishItem(currentJobs);
			PublishDebugInterface();
			PublishItem(new DEConfigItemPublicAction("resetFailedFlag") { DisplayName = "ResetFailed" });

			// Register Engine
			var sc = sp.GetService<IServiceContainer>(true);
			sc.AddService(typeof(IDECronEngine), this, false);

			// Register Server events
			Server.Queue.RegisterEvent(CancelJobs, DEServerEvent.Shutdown);
			Server.Queue.RegisterEvent(RefreshCronServices, DEServerEvent.Reconfiguration);
		} // ctor

		protected override void Dispose(bool disposing)
		{
			try
			{
				if (disposing)
				{
					CancelJobs();

					CronIdleActive = false;
					Server.Queue.CancelCommand(CancelJobs);
					Server.Queue.CancelCommand(RefreshCronServices);

					this.GetService<IServiceContainer>(false)?.RemoveService(typeof(IDECronEngine));

					currentJobs.Dispose();
					cronItemCacheController.Dispose();
				}
			}
			finally
			{
				base.Dispose(disposing);
			}
		} // proc Dispose

		#endregion

		#region -- Configuration ------------------------------------------------------

		private static void CollectCronJobItems(List<ICronJobItem> cronItems, DEConfigItem current)
		{
			if (current is ICronJobItem t)
				cronItems.Add(t);
			else if (current is ICronJobItems t2)
				cronItems.AddRange(t2.CronJobItems);
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
					CronIdleActive = true;

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
				CronIdleActive = false;
				lock (cronItemCacheLock)
					cronItemCache = null;
			}
		} // proc RefreshCronServices

		#endregion

		#region -- Last Run Time ------------------------------------------------------

		private void LoadNextRuntime()
		{
			// read persisted data
			using (var log = Log.GetScope(LogMsgType.Information))
			{
				try
				{
					log.WriteLine("Reread job table.");

					using (var sr = new StreamReader(NextRuntimeFile))
					{
						string line;
						while ((line = sr.ReadLine()) != null)
						{
							// # Displayname
							// id: zeit

							// Skip comment
							line = line.Trim();
							if (line.Length > 0 && line[0] == '#')
								continue;

							var iPos = line.IndexOf(' ');
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
									log.WriteLine("{0}: Is missing.", uniqueName);
							}
							catch (Exception e)
							{
								log.SetType(LogMsgType.Warning);
								log.WriteLine("[{0}] {1} at line: {2}", e.GetType().Name, e.Message, line);
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
			for (var i = 0; i < cronItemCache.Length; i++)
			{
				if (cronItemCache[i].NextRun.HasValue) // redo outstanding tasks
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
					for (var i = 0; i < cronItemCache.Length; i++)
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

		private void ClearNextRuntime(ICronJobItem job)
		{
			if (cronItemCache == null || job == null)
				return;

			lock (cronItemCacheLock)
			{
				var index = Array.FindIndex(cronItemCache, c => c.Job == job);
				if (index >= 0)
					cronItemCache[index].NextRun = null;
			}
		} // proc ClearNextRuntime

		private void UpdateNextRuntime(ICronJobItem job, DateTime next, bool save, bool force)
		{
			if (cronItemCache == null)
				return;

			lock (cronItemCacheLock)
			{
				var index = Array.FindIndex(cronItemCache, c => c.Job == job);
				if (index >= 0 && (force || !cronItemCache[index].NextRun.HasValue))
				{
					cronItemCache[index].NextRun = next;

					job.NotifyNextRun(next);
					if (save)
						SaveNextRuntime();
				}
			}
		} // func UpdateNextRuntime

		void IDECronEngine.UpdateNextRuntime(ICronJobItem job, DateTime? next)
		{
			if (job == null)
				throw new ArgumentNullException(nameof(job));

			var n = next ?? (job.Bound.IsEmpty ? DateTime.Now.AddHours(1) : job.Bound.GetNext(DateTime.Now));
			if (n < DateTime.Now)
				throw new ArgumentOutOfRangeException(nameof(next), n, "Timestamp must not be in the past.");

			UpdateNextRuntime(job, n, true, true);
		} // proc UpdateNextRuntime

		private string NextRuntimeFile => Path.ChangeExtension(LogFileName, "next");

		#endregion

		#region -- Job-Verwaltung -----------------------------------------------------

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
						for (var i = 0; i < cronItemCache.Length; i++)
						{
							ref var cronCacheItem = ref cronItemCache[i];
							if (cronCacheItem.NextRun.HasValue && cronCacheItem.NextRun.Value < now)
							{
								if (!StartJob(cronCacheItem.Job))
								{
									cronCacheItem.NextRun = cronCacheItem.NextRun.Value.Add(TimeSpan.FromSeconds(60)); // add 60sec if the job is busy
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

		public Task<bool> ExecuteJobAsync(ICronJobExecute job, CancellationToken cancellation)
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

				Log.Debug("jobstart: {0}", job.DisplayName);
				ClearNextRuntime(job as ICronJobItem);
				var currentJob = new CurrentRunningJob(this, job, cancellation);
				currentJobs.Add(currentJob);
				return currentJob.Task;
			}
		} // func ExecuteJobAsync

		private void FinishJob(CurrentRunningJob jobRunning, Exception jobException)
		{
			lock (cronItemCache)
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
						if (jobRunning.Job is DEConfigLogItem node)
						{
							if (jobException is AggregateException aggException)
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
						Log.Debug("jobfinish: {0}", name);
					
					// calculate next runtime
					if (jobBound != null && !jobBound.Bound.IsEmpty)
						UpdateNextRuntime(jobBound, jobBound.Bound.GetNext(DateTime.Now), true, false);
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
					cur.Cancel();
				}
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

		[LuaMember]
		public ICronJobItem GetJobItem(string jobId)
		{
			lock (cronItemCache)
			{
				if (cronItemCache != null)
				{
					for (var i = 0; i < cronItemCache.Length; i++)
					{
						if (String.Compare(cronItemCache[i].Job.UniqueName, jobId, StringComparison.OrdinalIgnoreCase) == 0)
							return cronItemCache[i].Job;
					}
				}
				return null;
			}
		} // func GetJobItem

		[LuaMember]
		public bool ResetNextRuntime(ICronJobItem job)
		{
			if (job != null && !job.Bound.IsEmpty)
			{
				UpdateNextRuntime(job, job.Bound.GetNext(DateTime.Now), true, false);
				return true;
			}
			else
				return false;
		} // func ResetNextRuntime

		[LuaMember]
		public IReadOnlyList<string> GetFailedJobs()
		{
			var failedIds = new List<string>();
			lock (cronItemCache)
			{
				if (cronItemCache != null)
				{
					for (var i = 0; i < cronItemCache.Length; i++)
					{
						if (cronItemCache[i].Job is ICronJobInformation cji && cji.IsFailed)
							failedIds.Add(cji.UniqueName);
					}
				}
			}
			return failedIds;
		} // func GetFailedJobs

		[DEConfigHttpAction("resetJobRuntime", IsSafeCall = true, SecurityToken = SecuritySys)]
		public XElement HttpResetNextRuntim(string id)
		{
			var job = GetJobItem(id);
			if (job == null)
				throw new HttpResponseException(HttpStatusCode.NotFound, "Job not found.");
			return new XElement("state",
				new XElement("reset", ResetNextRuntime(job))
			);
		} // func HttpResetNextRuntim

		[
		LuaMember,
		DEConfigHttpAction("resetFailedFlag", IsSafeCall = true, SecurityToken = SecuritySys)
		]
		public void ResetFailedState()
		{
			lock (cronItemCache)
			{
				if (cronItemCache != null)
				{
					for (var i = 0; i < cronItemCache.Length; i++)
					{
						if (cronItemCache[i].Job is ICronJobInformation cji)
							cji.ResetFailed();
					}
				}
			}
		} // proc HttpResetFailedState

		#endregion

		[
		PropertyName("tw_cron_isActive"),
		DisplayName("Active"),
		Description("Is true if the cron idle check is registered.")
		]
		public bool CronIdleActive
		{
			get { return isCronIdleActive; }
			set
			{
				Server.Queue.CancelCommand(CronIdle);
				if (value) // activate
				{
					Server.Queue.RegisterIdle(CronIdle);
					Log.Info("Cron idle registered.");
					isCronIdleActive = true;
				}
				else // deactivate
				{
					Log.Info("Cron idle cleared.");
					isCronIdleActive = false;
				}
				OnPropertyChanged(nameof(CronIdleActive));
			}
		} // prop CronIdleActive

		public override string Icon => "/images/clock.png";
	} // class DECronEngine

	#endregion

	#region -- class LuaCronJobItem -----------------------------------------------------

	/// <summary>Sime implementation of a lua based cron job.</summary>
	internal sealed class LuaCronJobItem : CronJobItem
	{
		#region -- Ctor/Dtor ----------------------------------------------------------------

		public LuaCronJobItem(IServiceProvider sp, string name)
			: base(sp, name)
		{
		} // ctor

		#endregion

		#region -- OnRunJob -----------------------------------------------------------------

		private CancellationToken cancellationToken = CancellationToken.None;

		protected override void OnRunJob(CancellationToken cancellation)
		{
			cancellationToken = cancellation;
			cancellation.Register(OnCancel);
			CallMember("Run", cancellationToken);
		} // proc OnRunJob

		private void OnCancel()
			=> CallMember("Cancel");

		public override bool IsSupportCancelation => Config.GetAttribute("supportsCancelation", false) || GetMemberValue("Cancel", rawGet: true) != null;

		#endregion

		public override string Icon => "/images/clock_run.png";
	} // class LuaCronJobItem

	#endregion

	#region -- class CronJobGroupBatch --------------------------------------------------

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
					if (!CronEngine.ExecuteJobAsync(c, cancellation).Result)
						OnSetFailed();
					Log.Info("{0}: finished.", c.DisplayName);
				}, true
			);
		} // proc RunJob

		public override string Icon => "/images/clock_data.png";
	} // class CronJobGroupBatch

	#endregion

	#region -- class CronJobGroupStart --------------------------------------------------

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
				var tasks = new List<Task<bool>>();
				Log.Info("Start tasks...");
				WalkChildren<ICronJobExecute>(
					c =>
					{
						Log.Info("{0}: Started...", c.DisplayName);
						tasks.Add(CronEngine.ExecuteJobAsync(c, cancellation));
					}, true, true);


				Task.WaitAll(tasks.ToArray(), cancellation);
				var failed = tasks.Count(t => !t.Result);
				if (failed > 0)
				{
					OnSetFailed();
					Log.Warn("{0} tasks finished (failed {1}).", tasks.Count,  failed);
				}
				else
					Log.Info("{0} tasks finished.", tasks.Count);
			}
		} // proc RunJob

		public override string Icon => "/images/clock_gearwheel.png";
	} // class CronJobGroupStart

	#endregion
}
