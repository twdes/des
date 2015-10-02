using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace TecWare.DE.Server
{
	/////////////////////////////////////////////////////////////////////////////////
	///// <summary></summary>
	//internal sealed class CronEngine : DEConfigLogItem//, IDECronEngine
	//{
		//#region -- class CurrentRunningJob ------------------------------------------------

		/////////////////////////////////////////////////////////////////////////////////
		///// <summary>Aktuell laufeneder Job</summary>
		//private sealed class CurrentRunningJob
		//{
		//	private CronEngine parent;
		//	private ICronJobExecute job;
		//	private ICronJobCancelItem jobCancel;

		//	private DateTime? dtCancelAfter;
		//	private Action procRunJob;
		//	private IAsyncResult arRunJob;

		//	public CurrentRunningJob(CronEngine parent, ICronJobExecute job)
		//	{
		//		this.parent = parent;
		//		this.job = job;
		//		this.jobCancel = job as ICronJobCancelItem;

		//		// Soll der Job automatisch abgebrochen werden
		//		if (jobCancel != null && jobCancel.IsSupportCancelation && jobCancel.Duration.HasValue)
		//			dtCancelAfter = DateTime.Now.Add(jobCancel.Duration.Value);

		//		// Starte die Aufgabe in einem hintergrundthread
		//		procRunJob = new Action(job.RunJob);
		//		arRunJob = procRunJob.BeginInvoke(EndJob, null);
		//	} // ctor

		//	private void EndJob(IAsyncResult ar)
		//	{
		//		Exception jobException = null;
		//		try
		//		{
		//			procRunJob.EndInvoke(arRunJob);
		//		}
		//		catch (Exception e)
		//		{
		//			jobException = e;
		//		}
		//		try
		//		{
		//			parent.FinishJob(this, jobException);
		//		}
		//		catch (Exception e)
		//		{
		//			parent.Log.LogMsg(LogMsgType.Error, "JobFinish fehlerhaft beendet.\n\n{0}", e.GetMessageString());
		//		}
		//	} // func EndJob

		//	public void Cancel()
		//	{
		//		if (jobCancel != null && jobCancel.IsSupportCancelation)
		//			jobCancel.Cancel();
		//	} // proc Cancel

		//	public void CheckAutoCancel()
		//	{
		//		if (dtCancelAfter.HasValue && DateTime.Now > dtCancelAfter.Value)
		//			jobCancel.Cancel();
		//	} // func CheckAutoCancel

		//	public ICronJobExecute Job { get { return job; } }
		//	public DateTime? CancelAfter { get { return dtCancelAfter; } }
		//} // class CurrentRunningJob

		//#endregion

		//private object currentJobsLock = new object();
		//private List<CurrentRunningJob> currentJobs = new List<CurrentRunningJob>();
		//private ICronJobItem[] cronItemCache = null;
		//private DateTime?[] nextJobRun = null;
		//private object nextJobRunWriterLock = new object();
		//private Action procCronStart = null;
		//private int iLastIdleCheck = Environment.TickCount;

		//#region -- Ctor/Dtor --------------------------------------------------------------

		//public CronEngine(IServiceProvider sp, string sName)
		//	: base(sp, sName)
		//{
		//} // ctor

		//#endregion

		//#region -- Configuration ----------------------------------------------------------

		//private static void CollectCronJobItems(List<ICronJobItem> cronItems, DEConfigItem cur)
		//{
		//	if (cur is ICronJobItem)
		//		cronItems.Add((ICronJobItem)cur);
		//	else // Keine Rekursion, wenn ein CronJob Eintrag gefunden wurde
		//	{
		//		foreach (DEConfigItem c in cur.UnsafeChildren)
		//			CollectCronJobItems(cronItems, c);
		//	}
		//} // proc CollectCronJobItems

		//public void RefreshCronServices()
		//{
		//	// Prüfe, ob es CronJobs gibt
		//	List<ICronJobItem> cronItems = new List<ICronJobItem>();
		//	CollectCronJobItems(cronItems, this.GetService<DEServer>(true));

		//	Log.LogMsg(LogMsgType.Information, "CronJoin found: {0}", cronItems.Count);
		//	if (cronItems.Count > 0)
		//	{
		//		if (cronItemCache == null) // Erste item aufgenommen
		//		{
		//			IDEServerQueue queue = this.GetService<IDEServerQueue>(true);
		//			if (procCronStart == null)
		//				procCronStart = CronStartJob;
		//			queue.RegisterIdle(procCronStart);
		//		}
		//		// else Es bleibt aktiv --> setze die Liste neu

		//		// Lies die Liste mit den zuletzt gelaufenen Zeiten und errechne den nächsten Start
		//		lock (nextJobRunWriterLock)
		//			nextJobRun = LoadNextRuntime(cronItems);

		//		// Update der Liste
		//		cronItemCache = cronItems.ToArray();
		//	}
		//	else if (cronItemCache != null && cronItems.Count == 0) // Items komplett entfernt
		//	{
		//		IDEServerQueue queue = this.GetService<IDEServerQueue>(true);
		//		queue.UnregisterIdle(procCronStart);
		//		cronItemCache = null;
		//	}
		//} // proc RefreshCronServices

		//public void CancelJobs()
		//{
		//	lock (currentJobsLock)
		//		currentJobs.ForEach(c => c.Cancel());
		//} // proc CancelJobs

		//#endregion

		//#region -- Last Run Time ----------------------------------------------------------

		//private DateTime?[] LoadNextRuntime(List<ICronJobItem> items)
		//{
		//	// Array für die Daten erzeugen
		//	DateTime?[] nextRun = new DateTime?[items.Count];
		//	string sLine;

		//	// Lese erstmal die Datenaus dem Speicher
		//	using (LogMessage log = new LogMessage(LogMsgType.Information, Log))
		//		try
		//		{
		//			log.WriteLine("Lese Jobstarts ein.");

		//			using (StreamReader sr = new StreamReader(NextRuntimeFile))
		//			{
		//				while ((sLine = sr.ReadLine()) != null)
		//				{
		//					// # Displayname
		//					// id: zeit

		//					// Komentar überspringen
		//					sLine = sLine.Trim();
		//					if (sLine.Length > 0 && sLine[0] == '#')
		//						continue;

		//					int iPos = sLine.IndexOf(' ');
		//					if (iPos == -1)
		//						continue;

		//					// Lese die Daten aus der Zeile
		//					string sUniqueName = sLine.Substring(0, iPos).Trim();
		//					string sTime = sLine.Substring(iPos + 1).Trim();
		//					try
		//					{
		//						DateTime dtNext = DateTime.Parse(sTime);
		//						int iIndex = items.FindIndex(c => String.Compare(c.UniqueName, sUniqueName, true) == 0);
		//						if (iIndex >= 0)
		//							nextRun[iIndex] = dtNext;
		//						else
		//							log.WriteLine("{0}: Nicht mehr gefunden.", sUniqueName);
		//					}
		//					catch (Exception e)
		//					{
		//						log.Typ = LogMsgType.Warning;
		//						log.WriteLine("[{0}] {1} bei Zeile: {2}", e.GetType().Name, e.Message, sLine);
		//					}

		//				}
		//			}
		//		}
		//		catch (Exception ex)
		//		{
		//			log.WriteException(ex);
		//			log.Typ = LogMsgType.Warning;
		//		}

		//	// Danach ergänze die Zeiten für die neuen Aufgaben
		//	for (int i = 0; i < items.Count; i++)
		//	{
		//		if (nextRun[i].HasValue)
		//			continue;

		//		if (!items[i].Bound.IsEmpty)
		//		{
		//			DateTime dtNext = items[i].Bound.GetNext(DateTime.Now);
		//			items[i].NotifyNextRun(dtNext);
		//			nextRun[i] = dtNext;
		//		}
		//	}

		//	return nextRun;
		//} // proc LoadNextRuntime

		//private void SaveNextRuntime()
		//{
		//	using (StreamWriter sw = new StreamWriter(NextRuntimeFile))
		//	{
		//		for (int i = 0; i < nextJobRun.Length; i++)
		//			if (nextJobRun[i].HasValue)
		//			{
		//				sw.WriteLine("# {0}", cronItemCache[i].DisplayName);
		//				sw.WriteLine("{0}: {1}", cronItemCache[i].UniqueName, nextJobRun[i].Value);
		//			}
		//	}
		//} // proc SaveNextRuntime

		//private string NextRuntimeFile { get { return Path.ChangeExtension(this.LogFileName, "next"); } }

		//#endregion

		//#region -- Job-Verwaltung ---------------------------------------------------------

		//private void CronStartJob()
		//{
		//	// Prüfe nicht so häufig
		//	if (Math.Abs(Environment.TickCount - iLastIdleCheck) < 1000)
		//		return;

		//	// Prüfe, gibt es etwas zu starten
		//	using (EnterReadLock())
		//	{
		//		if (nextJobRun != null)
		//		{
		//			DateTime dtNow = DateTime.Now;
		//			for (int i = 0; i < nextJobRun.Length; i++)
		//			{
		//				if (nextJobRun[i].Value < dtNow)
		//					StartJob(cronItemCache[i]);
		//			}
		//		}
		//		lock (currentJobsLock)
		//			currentJobs.ForEach(c => c.CheckAutoCancel());
		//	}
		//	iLastIdleCheck = Environment.TickCount;
		//} // proc CronStartJob

		//public bool StartJob(ICronJobExecute job, CronJobFinishedDelegate jobFinished = null)
		//{
		//	lock (currentJobsLock)
		//	{
		//		// Prüfe, ob der Job schon läuft
		//		if (IsJobRunning(job))
		//			return false; // wenn ja erweitere die Laufzeit um 60sec

		//		// Starte ihn
		//		Log.Info("jobstart: {0}", job.DisplayName);
		//		currentJobs.Add(new CurrentRunningJob(this, job));
		//	}
		//	return true;
		//} // proc StartJob

		//private void FinishJob(CurrentRunningJob jobRunning, Exception jobException)
		//{
		//	ICronJobItem jobBound = jobRunning.Job as ICronJobItem;

		//	using (EnterReadLock())
		//		lock (currentJobsLock)
		//		{
		//			// Lösche den Job aus den laufenden Jobs
		//			currentJobs.Remove(jobRunning);
		//			string sName = jobBound == null ? "<unnamed>" : jobBound.DisplayName;
		//			if (jobBound != null)
		//				Log.Except("jobfinish: {0}" + Environment.NewLine + "{1}", sName, jobException.GetMessageString());
		//			else
		//				Log.Info("jobfinish: {0}", sName);

		//			// Rechne den nächsten Startpunkt aus
		//			if (jobBound != null && !jobBound.Bound.IsEmpty && cronItemCache != null)
		//			{
		//				int iIndex = Array.IndexOf(cronItemCache, jobBound);
		//				if (iIndex >= 0)
		//				{
		//					DateTime dtNext = jobBound.Bound.GetNext(DateTime.Now);
		//					nextJobRun[iIndex] = dtNext;
		//					jobBound.NotifyNextRun(dtNext);
		//					SaveNextRuntime();
		//				}
		//			}
		//		}
		//} // proc FinishJob

		//private bool IsJobRunning(ICronJobExecute job)
		//{
		//	lock (currentJobsLock)
		//		return currentJobs.Exists(c => job.CompareTo(c.Job) == 0);
		//} // func ExistsJob

		//#endregion

		//public override string Icon { get { return "/images/clock.png"; } }
	//} // class CronEngine
}
