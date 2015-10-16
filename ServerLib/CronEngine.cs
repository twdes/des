using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TecWare.DE.Server.Configuration;
using TecWare.DE.Server.Stuff;
using TecWare.DE.Stuff;

namespace TecWare.DE.Server
{
	#region -- interface IDECronEngine --------------------------------------------------

	///////////////////////////////////////////////////////////////////////////////
	/// <summary></summary>
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
		/// <returns></returns>
		Task ExecuteJobAsync(ICronJobExecute job, CancellationToken cancellation);
	} // interface IDECronEngine

	#endregion

	#region -- interface ICronJobExecute ------------------------------------------------

	///////////////////////////////////////////////////////////////////////////////
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

	#region -- interface ICronJobItem ---------------------------------------------------

	///////////////////////////////////////////////////////////////////////////////
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

	#region -- interface ICronJobCancellation -------------------------------------------

	///////////////////////////////////////////////////////////////////////////////
	/// <summary>Defines a timed job.</summary>
	public interface ICronJobCancellation : ICronJobExecute
	{
		/// <summary>Does the cronjob supports cancelation.</summary>
		bool IsSupportCancelation { get; }
		/// <summary></summary>
		TimeSpan? RunTimeSlice { get; }
	} // interface ICronJobCancellation

	#endregion

	#region -- class CronJobItem --------------------------------------------------------

	///////////////////////////////////////////////////////////////////////////////
	/// <summary>Basic implemenation of an cron item.</summary>
	public abstract class CronJobItem : DEConfigLogItem, ICronJobItem, ICronJobCancellation
	{
		public const string JobCategory = "Job";

		private Lazy<IDECronEngine> cronEngine;
		private CronBound cronBound = CronBound.Empty;
		private string[] runAfterJob = null;
		private TimeSpan? runTimeSlice = null;

		private SimpleConfigItemProperty<DateTime?> propertyNextRun = null;
		private SimpleConfigItemProperty<double> propertyLastTime = null;
		private SimpleConfigItemProperty<string> propertyIsRunning = null;

		#region -- Ctor/Dtor/Config -------------------------------------------------------

		public CronJobItem(IServiceProvider sp, string name)
			: base(sp, name)
		{
			this.cronEngine = new Lazy<IDECronEngine>(() => this.GetService<IDECronEngine>(false));

			propertyNextRun = new SimpleConfigItemProperty<DateTime?>(this, "tw_cron_nextrun", "Next run", JobCategory, "Zeitpunkt des nächsten durchlaufs.", "{0:t}", null);
			propertyLastTime = new SimpleConfigItemProperty<double>(this, "tw_cron_duration", "Last duration", JobCategory, "Zuletzt benötigte Zeit für den Durchlauf", "{0:N1} min", 0.0);
			propertyIsRunning = new SimpleConfigItemProperty<string>(this, "tw_cron_isrunning", "Is running", JobCategory, "Status der aktuellen Aufgabe.", null, "nein");

			PublishItem(new DEConfigItemPublicAction("jobstart") { DisplayName = "Start" });
		} // ctor

		protected override void Dispose(bool disposing)
		{
			if (disposing)
			{
				Procs.FreeAndNil(ref propertyNextRun);
				Procs.FreeAndNil(ref propertyLastTime);
				Procs.FreeAndNil(ref propertyIsRunning);
			}
			base.Dispose(disposing);
		} // proc Dispose

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

		#region -- Http ------------------------------------------------------------------

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

		#region -- Job-Verwaltung --------------------------------------------------------

		protected void CheckCronEngine()
		{
			if (CronEngine == null)
				throw new ArgumentException("No cron engine to run a job.");
		} // proc CheckCronEngine

		protected abstract void OnRunJob(CancellationToken cancellation);

		void ICronJobExecute.RunJob(CancellationToken cancellation)
		{
			var sw = Stopwatch.StartNew();
			propertyIsRunning.Value = "yes";
			try
			{
				OnRunJob(cancellation);
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
				var o = other as ICronJobItem;
				return o == null || runAfterJob.FirstOrDefault(c => ProcsDE.IsFilterEqual(o.UniqueName, c)) == null;
			}
		} // func ICronJobExecute.CanRunParallelTo

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
		protected async Task ExecuteJobAsync(ICronJobExecute job, CancellationToken cancellation)
		{
			if (CronEngine == null)
				throw new ArgumentException("No cron engine to run a job.");

			await CronEngine.ExecuteJobAsync(job, cancellation);
		} // proc ExecuteJobAsync

		#endregion

		/// <summary>Defines the name in the job-list.</summary>
		public string UniqueName => ConfigPath;
		/// <summary>Schedule for the job.</summary>
		public CronBound Bound => cronBound;
		/// <summary>Is cancellation supported. (default: false)</summary>
		public virtual bool IsSupportCancelation => RunTimeSlice.HasValue;

		public virtual TimeSpan? RunTimeSlice => runTimeSlice;
		/// <summary></summary>
		public IDECronEngine CronEngine => cronEngine.Value;

		/// <summary>Zugriff auf den Status.</summary>
		protected SimpleConfigItemProperty<string> StateRunning => propertyIsRunning;
	} // class CronJobItem

	#endregion
}
