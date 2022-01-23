﻿#region -- copyright --
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
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Neo.IronLua;
using TecWare.DE.Server.Configuration;
using TecWare.DE.Server.Stuff;
using TecWare.DE.Stuff;

namespace TecWare.DE.Server
{
	#region -- interface IDECronEngine ------------------------------------------------

	/// <summary>Access the job engine.</summary>
	public interface IDECronEngine
	{
		/// <summary>Schedules a job to execute.</summary>
		/// <param name="job">Aufgabe die sofort gestart werden soll.</param>
		/// <returns><c>true</c>, if the job could be scheduled or <c>false</c> if the job is in process.</returns>
		bool StartJob(ICronJobExecute job);
		/// <summary></summary>
		/// <param name="job"></param>
		void CancelJob(ICronJobExecute job);

		/// <summary>Executes a job.</summary>
		/// <param name="job">Job, to execute.</param>
		/// <param name="cancellation">Cancellation token.</param>
		/// <returns><c>true</c>, if the job finished succesfull.</returns>
		Task<bool> ExecuteJobAsync(ICronJobExecute job, CancellationToken cancellation);

		/// <summary>Update Timestamp for an job.</summary>
		/// <param name="job"></param>
		/// <param name="next"></param>
		void UpdateNextRuntime(ICronJobItem job, DateTime? next);
	} // interface IDECronEngine

	#endregion

	#region -- interface ICronJobExecute ----------------------------------------------

	/// <summary>Basic interface for a job.</summary>
	public interface ICronJobExecute
	{
		/// <summary>Starts the job, do not call direct.</summary>
		void RunJob(CancellationToken cancellation);
		/// <summary>Ask if the current job, can run parallel to the other job.</summary>
		/// <param name="other"></param>
		/// <returns></returns>
		bool CanRunParallelTo(ICronJobExecute other);

		/// <summary>Displayname of the job.</summary>
		string DisplayName { get; }
	} // interface ICronJobExecute

	#endregion

	#region -- interface ICronJobItem -------------------------------------------------

	/// <summary>Defines a timed job.</summary>
	public interface ICronJobItem : ICronJobExecute
	{
		/// <summary>Notifies about the next time border.</summary>
		/// <param name="dt">Scheduled time.</param>
		void NotifyNextRun(DateTime dt);
		
		/// <summary>Time borders for the schedule of the job.</summary>
		CronBound Bound { get; }
		/// <summary>Unique name of the job, e.g. ConfigPath, Guid</summary>
		string UniqueName { get; }
	} // interface CronJobItem

	#endregion

	#region -- interface ICronJobItems ------------------------------------------------

	/// <summary>Allows a config item to return more than one cron item.</summary>
	public interface ICronJobItems
	{
		/// <summary>List of cron-items on this config item.</summary>
		IEnumerable<ICronJobItem> CronJobItems { get; }
	} // interface ICronJobItems

	#endregion

	#region -- interface ICronJobCancellation -----------------------------------------

	/// <summary>Defines a timed job.</summary>
	public interface ICronJobCancellation : ICronJobExecute
	{
		/// <summary>Does the cronjob supports cancelation.</summary>
		bool IsSupportCancelation { get; }
		/// <summary></summary>
		TimeSpan? RunTimeSlice { get; }
	} // interface ICronJobCancellation

	#endregion

	#region -- interface ICronJobInformation ------------------------------------------

	/// <summary>Information about the cron state.</summary>
	public interface ICronJobInformation : ICronJobItem
	{
		/// <summary>Reset the failed flag.</summary>
		void ResetFailed();

		/// <summary></summary>
		DateTime? LastRun { get; }
		/// <summary></summary>
		double LastDuration { get; }

		/// <summary>Was the job failed.</summary>
		bool IsFailed { get; }
	} // interface ICronJobInformation

	#endregion

	#region -- class CronJobItem ------------------------------------------------------

	/// <summary>Basic implemenation of an cron item.</summary>
	public abstract class CronJobItem : DEConfigLogItem, ICronJobItem, ICronJobCancellation, ICronJobInformation
	{
		/// <summary></summary>
		public const string JobCategory = "Job";

		private readonly Lazy<IDECronEngine> cronEngine;
		private CronBound cronBound = CronBound.Empty;
		private string[] runAfterJob = null;
		private TimeSpan? runTimeSlice = null;

		private readonly SimpleConfigItemProperty<DateTime?> propertyLastRun;
		private readonly SimpleConfigItemProperty<DateTime?> propertyNextRun;
		private readonly SimpleConfigItemProperty<double> propertyLastTime;
		private readonly SimpleConfigItemProperty<string> propertyIsRunning;
		private readonly SimpleConfigItemProperty<bool> propertyIsFailed;

		#region -- Ctor/Dtor/Config ---------------------------------------------------

		/// <summary></summary>
		/// <param name="sp"></param>
		/// <param name="name"></param>
		public CronJobItem(IServiceProvider sp, string name)
			: base(sp, name)
		{
			this.cronEngine = new Lazy<IDECronEngine>(() => this.GetService<IDECronEngine>(false));

			propertyLastRun = new SimpleConfigItemProperty<DateTime?>(this, "tw_cron_lastrun", "Last run", JobCategory, "Last time the job was executed", "{0:t}", null);
			propertyNextRun = new SimpleConfigItemProperty<DateTime?>(this, "tw_cron_nextrun", "Next run", JobCategory, "Time the job is scheduled for the next run.", "{0:t}", null);
			propertyLastTime = new SimpleConfigItemProperty<double>(this, "tw_cron_duration", "Last duration", JobCategory, "Zuletzt benötigte Zeit für den Durchlauf", "{0:N1} min", 0.0);
			propertyIsRunning = new SimpleConfigItemProperty<string>(this, "tw_cron_isrunning", "Is running", JobCategory, "Status der aktuellen Aufgabe.", null, "nein");
			propertyIsFailed = new SimpleConfigItemProperty<bool>(this, "tw_cron_isfailed", "Is failed", JobCategory, "Is the job failed.", null, false);

			PublishItem(new DEConfigItemPublicAction("jobstart") { DisplayName = "Start" });
		} // ctor

		/// <summary></summary>
		/// <param name="disposing"></param>
		protected override void Dispose(bool disposing)
		{
			if (disposing)
			{
				propertyLastRun?.Dispose();
				propertyNextRun?.Dispose();
				propertyLastTime?.Dispose();
				propertyIsRunning?.Dispose();
			}
			base.Dispose(disposing);
		} // proc Dispose

		/// <summary></summary>
		/// <param name="config"></param>
		protected override void OnBeginReadConfiguration(IDEConfigLoading config)
		{
			base.OnBeginReadConfiguration(config);

			// read the border
			cronBound = new CronBound(config.ConfigNew.GetAttribute("bound", String.Empty));
			var attr= config.ConfigNew.Attribute("runTimeSlice");
			runTimeSlice = attr == null ? null : new TimeSpan?(TimeSpan.Parse(attr.Value));

			// initialize run after
			runAfterJob = config.ConfigNew.Elements(DEConfigurationConstants.xnCronRunAfter)
				.Where(c => !String.IsNullOrEmpty(c.Value))
				.Select(c => c.Value.Trim()).ToArray();

			if (runAfterJob.Length == 0)
				runAfterJob = null;
		} // proc OnBeginReadConfiguration

		#endregion

		#region -- Http ---------------------------------------------------------------

		/// <summary></summary>
		[LuaMember("StartJob")]
		public void LuaStartJob()
			=> HttpStartAction();

		[
		DEConfigHttpAction("jobstart", SecurityToken = SecuritySys),
		Description("Schedule the job as soon as possible.")
		]
		private void HttpStartAction()
		{
			CheckCronEngine();
			if (!CronEngine.StartJob(this))
				throw new ArgumentException("Can not start job (it is running).");
		} // func HttpStartAction

		#endregion

		#region -- Job-Verwaltung -----------------------------------------------------

		/// <summary></summary>
		protected void CheckCronEngine()
		{
			if (CronEngine == null)
				throw new ArgumentException("No cron engine to run a job.");
		} // proc CheckCronEngine

		/// <summary></summary>
		/// <param name="cancellation"></param>
		protected abstract void OnRunJob(CancellationToken cancellation);

		void ICronJobExecute.RunJob(CancellationToken cancellation)
		{
			var sw = Stopwatch.StartNew();
			propertyIsRunning.Value = "yes";
			propertyLastRun.Value = DateTime.Now;
			try
			{
				OnRunJob(cancellation);
			}
			catch
			{
				OnSetFailed();
				throw;
			}
			finally
			{
				propertyIsRunning.Value = "no";
				propertyLastTime.Value = sw.Elapsed.TotalMinutes;
			}
		} // proc ICronJobExecute.RunJob

		bool ICronJobExecute.CanRunParallelTo(ICronJobExecute other)
		{
			if (other == this) // do not run parallel the same job
				return false;

			if (runAfterJob == null)
				return true;
			else
			{
				return !(other is ICronJobItem o) || runAfterJob.FirstOrDefault(c => Procs.IsFilterEqual(o.UniqueName, c)) == null || CanRunParallelTo(o);
			}
		} // func ICronJobExecute.CanRunParallelTo

		/// <summary></summary>
		/// <param name="o"></param>
		/// <returns></returns>
		protected virtual bool CanRunParallelTo(ICronJobItem o)
			=> true;

		void ICronJobItem.NotifyNextRun(DateTime dt)
		{
			// set the next time
			if (propertyNextRun != null)
				propertyNextRun.Value = dt;

			// update for the cron job hidden children
			WalkChildren<ICronJobItem>(c => c.NotifyNextRun(dt));
		} // ICronJobItem.NotifyNextRun

		/// <summary>Start the job.</summary>
		public Task ExecuteAsync(CancellationToken cancellation)
			=> ExecuteJobAsync(this, cancellation);

		/// <summary>Execute more than one job.</summary>
		/// <param name="job"></param>
		/// <param name="cancellation"></param>
		protected Task<bool> ExecuteJobAsync(ICronJobExecute job, CancellationToken cancellation)
		{
			CheckCronEngine();

			return CronEngine.ExecuteJobAsync(job, cancellation);
		} // proc ExecuteJobAsync

		/// <summary>Is called if the job is failed.</summary>
		protected virtual void OnSetFailed()
			=> propertyIsFailed.Value = true;

		/// <summary>Is called to reset the failed state.</summary>
		protected virtual void OnResetFailed()
			=> propertyIsFailed.Value = false;

		void ICronJobInformation.ResetFailed()
			=> OnResetFailed();

		#endregion

		/// <summary>Defines the name in the job-list.</summary>
		public string UniqueName => ConfigPath;
		/// <summary>Schedule for the job.</summary>
		public CronBound Bound => cronBound;
		/// <summary>Is cancellation supported. (default: false)</summary>
		public virtual bool IsSupportCancelation => RunTimeSlice.HasValue;

		/// <summary>Maximal duration fo the task.</summary>
		public virtual TimeSpan? RunTimeSlice => runTimeSlice;
		/// <summary>Access to the cron-runtime.</summary>
		public IDECronEngine CronEngine => cronEngine.Value;

		DateTime? ICronJobInformation.LastRun => propertyLastRun.Value;
		double ICronJobInformation.LastDuration => propertyLastTime.Value;
		bool ICronJobInformation.IsFailed => propertyIsFailed.Value;

		/// <summary>Zugriff auf den Status.</summary>
		protected SimpleConfigItemProperty<string> StateRunning => propertyIsRunning;
	} // class CronJobItem

	#endregion
}
