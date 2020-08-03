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
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using Neo.IronLua;
using TecWare.DE.Stuff;

namespace TecWare.DE.Server
{
	#region -- class DirectoryListenerItem --------------------------------------------

	internal class DirectoryListenerItem : DEConfigLogItem
	{
		public const string DirectoryListenerCategory = "Directory";

		#region -- enum NotifyMethod --------------------------------------------------

		private enum NotifyMethod
		{
			/// <summary>No method selected.</summary>
			None,
			/// <summary>Compare the timestamp.</summary>
			TimeStamp,
			/// <summary>Archive-Bit is set after process.</summary>
			ArchiveBit
		} // enum NotifyMethod

		#endregion

		#region -- class FileNotifyEvent ----------------------------------------------

		private sealed class FileNotifyEvent
		{
			private readonly string name;
			private readonly string fullPath;
			private DateTime eventCreated;
			private int errorCounter = 0;

			public FileNotifyEvent(FileSystemEventArgs e)
				: this(e.Name, e.FullPath, DateTime.Now)
			{
			} // ctor

			public FileNotifyEvent(string name, string fullPath, DateTime eventCreated)
			{
				this.name = name ?? throw new ArgumentNullException(nameof(name));
				this.fullPath = fullPath ?? throw new ArgumentNullException(nameof(fullPath));
				this.eventCreated = eventCreated;
			} // ctor

			public void IncError()
			{
				errorCounter++;
				eventCreated = DateTime.Now.AddSeconds(errorCounter * 5);
			} // proc IncError

			public bool IsSamePath(string otherFullPath)
				=> String.Compare(fullPath, otherFullPath, StringComparison.OrdinalIgnoreCase) == 0;

			public string Name => name;
			public DateTime Stamp => eventCreated;
			public FileInfo File => new FileInfo(fullPath);

			public int ErrorCounter => errorCounter;
		} // class FileNotifyEvent

		#endregion

		private readonly FileSystemWatcher fileSystemWatcher = new FileSystemWatcher();
		private readonly List<FileNotifyEvent> notifyQueue = new List<FileNotifyEvent>();
		private NotifyMethod notifyMethod = NotifyMethod.None;
		private TimeSpan notifyDelay = TimeSpan.FromSeconds(5);
		private TimeSpan? rescanDelay = null;
		
		private readonly Action notifyCheck;
		private DateTime lastTimeStamp = DateTime.MinValue;
		private DateTime lastFullScan = DateTime.MinValue;

		private long fileCount = 0;
		private long fileErrCount = 0;
		private long fileCountRefresh = 0;

		#region -- Ctor/Dtor --------------------------------------------------------------

		public DirectoryListenerItem(IServiceProvider sp, string name)
			: base(sp, name)
		{
			fileSystemWatcher.Created += FileSystemWatcher_Changed;
			fileSystemWatcher.Changed += FileSystemWatcher_Changed;
			fileSystemWatcher.Deleted += FileSystemWatcher_Changed;
			fileSystemWatcher.Renamed += FileSystemWatcher_Changed;
			fileSystemWatcher.Error += FileSystemWatcher_Error;

			notifyCheck = NotifyCheckIdle;

			PublishDebugInterface();
		} // ctor

		protected override void Dispose(bool disposing)
		{
			try
			{
				fileSystemWatcher.Dispose();
				Server.Queue.CancelCommand(notifyCheck);
			}
			finally
			{
				base.Dispose(disposing);
			}
		} // proc Dispose

		protected override void OnBeginReadConfiguration(IDEConfigLoading config)
		{
			base.OnBeginReadConfiguration(config);

			// validate path
			ValidateDirectory(config.ConfigNew, "path");

			// stop
			fileSystemWatcher.EnableRaisingEvents = false;
			Server.Queue.CancelCommand(notifyCheck);
		} // proc OnBeginReadConfiguration

		protected override void OnEndReadConfiguration(IDEConfigLoading config)
		{
			base.OnEndReadConfiguration(config);

			ReadLastTimeStamp();

			// reset the parameters
			fileSystemWatcher.Path = Config.GetAttribute("path", null);
			fileSystemWatcher.Filter = Config.GetAttribute("filter", "*.*");
			fileSystemWatcher.IncludeSubdirectories = Config.GetAttribute("recursive", false);

			notifyMethod = Config.GetAttribute("method", NotifyMethod.None);
			notifyDelay = Config.GetAttribute("delay", notifyDelay);
			rescanDelay = Config.GetAttribute("rescan", rescanDelay ?? TimeSpan.Zero);
			if (rescanDelay <= TimeSpan.Zero)
				rescanDelay = null;

			switch (notifyMethod)
			{
				case NotifyMethod.ArchiveBit:
					fileSystemWatcher.NotifyFilter = NotifyFilters.Attributes | NotifyFilters.FileName;
					break;
				case NotifyMethod.TimeStamp:
					fileSystemWatcher.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName;
					break;
				case NotifyMethod.None:
				default:
					fileSystemWatcher.NotifyFilter = NotifyFilters.Attributes | NotifyFilters.LastWrite | NotifyFilters.FileName;
					break;
			}

			// run
			Server.Queue.RegisterIdle(notifyCheck);
			fileSystemWatcher.EnableRaisingEvents = true;
		} // proc OnEndReadConfiguration

		#endregion

		#region -- Handle Events ----------------------------------------------------------

		private void FileSystemWatcher_Changed(object sender, FileSystemEventArgs e)
		{
			WatcherChangeTypes type;
			var e2 = e as RenamedEventArgs;

			// write debug message
			if (IsDebug)
			{
				if (e2 != null)
					Log.Debug("FileSystemWatcher event: Type={0}, Name={1} > {2}, FullPath={3} > {4}", e.ChangeType, e2.OldName, e.Name, e2.OldFullPath, e.FullPath);
				else
					Log.Debug("FileSystemWatcher event: Type={0}, Name={1}, FullPath={2}", e.ChangeType, e.Name, e.FullPath);
			}

			// we priorisize the events, and ignore lower level events, if the occure
			if ((e.ChangeType & WatcherChangeTypes.Created) != 0) // file is created
			{
				type = WatcherChangeTypes.Created;

				lock (notifyQueue)
				{
					RemoveNotifyEvent(e.FullPath);
					notifyQueue.Add(new FileNotifyEvent(e));
				}
			}
			else if ((e.ChangeType & WatcherChangeTypes.Deleted) != 0) // files is deleted
			{
				type = WatcherChangeTypes.Deleted;
				lock (notifyQueue)
					RemoveNotifyEvent(e.FullPath);
			}
			else if ((e.ChangeType & WatcherChangeTypes.Renamed) != 0) // files is renamed
			{
				type = WatcherChangeTypes.Renamed;

				lock (notifyQueue)
				{
					RemoveNotifyEvent(e2.OldFullPath);

					// check if the new name requires the filter criteria
					if (Regex.IsMatch(e.Name, Procs.FileFilterToRegex(fileSystemWatcher.Filter)))
						notifyQueue.Add(new FileNotifyEvent(e));
				}
			}
			else if ((e.ChangeType & WatcherChangeTypes.Changed) != 0) // attributes or lastwrite changed
			{
				type = WatcherChangeTypes.Changed;

				lock (notifyQueue)
				{
					RemoveNotifyEvent(e.FullPath);
					notifyQueue.Add(new FileNotifyEvent(e));
				}
			}
			else
				return;

			// call a lua extension
			var member = this["NotifyFileChangeCore"];
			if (Lua.RtInvokeable(member))
			{
				try
				{
					((dynamic)this).NotifyFileChange(type, e);
				}
				catch (Exception ex)
				{
					Log.Except(String.Format("NotifyFileChangeCore failed for {0}.", e.Name), ex);
				}
			}
		} // event FileSystemWatcher_Changed

		private int IndexOfNotifyEvent(string fullPath)
		{
			lock (notifyQueue)
				return notifyQueue.FindIndex(c => c.IsSamePath(fullPath));
		} // func IndexOfNotifyEvent

		private void RemoveNotifyEvent(string fullPath)
		{
			lock (notifyQueue)
			{
				var idx = IndexOfNotifyEvent(fullPath);
				if (idx >= 0)
					notifyQueue.RemoveAt(idx);
			}
		} // proc RemoveNotifyEvent

		#endregion

		#region  -- RefreshFiles ------------------------------------------------------

		[LuaMember("StartRefreshFiles")]
		private void StartRefreshFiles(int wait = 500)
		{
			// wait because the initialization process is not finished.
			var refreshFiles = new Action<int>(RefreshFiles);
			refreshFiles.BeginInvoke(wait, EndRefreshFiles, refreshFiles);
		} // proc StartRefreshFiles

		private void EndRefreshFiles(IAsyncResult ar)
		{
			try
			{
				((Action<int>)ar.AsyncState).EndInvoke(ar);
			}
			catch (Exception e)
			{
				Log.Except(e);
			}
		} // proc EndRefreshlFiles

		private void RefreshFiles(int wait)
		{
			using var scope = IsDebug ? Log.CreateScope(LogMsgType.Information, true, true) : null;

			scope?.WriteLine("RefreshFiles");

			lastFullScan = DateTime.Now;

			if (wait > 0)
			{
				Thread.Sleep(wait);
				scope?.WriteLine("Wait for {0:N0}ms", wait);
			}

			Func<FileInfo, bool> isFileProcessed;

			if (notifyMethod == NotifyMethod.ArchiveBit)
			{
				isFileProcessed = fi => (fi.Attributes & FileAttributes.Archive) != 0; // check the archive flag
			}
			else if (notifyMethod == NotifyMethod.TimeStamp)
			{
				isFileProcessed = fi => fi.LastWriteTime > lastTimeStamp;
			}
			else
			{
				var needProcessLua = this["IsFileProcessed"];
				if (Lua.RtInvokeable(needProcessLua))
					isFileProcessed = fi => ((LuaResult)Lua.RtInvoke(needProcessLua, new object[] { fi })).ToBoolean();
				else
					isFileProcessed = fi => false;
			}

			var di = new DirectoryInfo(fileSystemWatcher.Path);
			var fileCountChanged = false;

			// check file
			foreach (var fi in di.EnumerateFiles(fileSystemWatcher.Filter, fileSystemWatcher.IncludeSubdirectories ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly))
			{
				if (!isFileProcessed(fi)) // check for enqueue
				{
					lock (notifyQueue)
					{
						if (IndexOfNotifyEvent(fi.FullName) < 0)
						{
							var stamp = fi.LastWriteTime;
							if (stamp > DateTime.Now) // do not allow timestamp's they are in the future
								stamp = DateTime.Now;

							scope?.WriteLine("Add: {0}", fi.FullName);
							notifyQueue.Add(new FileNotifyEvent(fi.Name, fi.FullName, stamp));
							fileCountRefresh++;
							fileCountChanged |= true;
						}
						else
							scope?.WriteLine("Already queued: {0}", fi.FullName);
					}
				}
				else
					scope?.WriteLine("Skip file: {0}", fi.FullName);
			}

			if (fileCountChanged)
				OnPropertyChanged(nameof(FileCountRefresh));
		} // proc RefreshFiles

		private void FileSystemWatcher_Error(object sender, ErrorEventArgs e)
		{
			Log.Warn($"FileSystemWatcher failed (state: {fileSystemWatcher.EnableRaisingEvents}).", e.GetException());
			// start a scan
			fileSystemWatcher.EnableRaisingEvents = true;
			StartRefreshFiles(200);
		} // event FileSystemWatcher_Error

		#endregion

		#region -- Handle File Notify -----------------------------------------------------

		private void UpdateLastTimeStamp(DateTime newLastTimeStamp)
		{
			lastTimeStamp = newLastTimeStamp;

			// update file
			var fileName = Path.ChangeExtension(LogFileName, "timestmap");
			if (lastTimeStamp > DateTime.MinValue)
				File.WriteAllText(fileName, lastTimeStamp.ToString(CultureInfo.InvariantCulture));
		} // proc UpdateLastTimeStamp

		private void ReadLastTimeStamp()
		{
			try
			{
				var fileName = Path.ChangeExtension(LogFileName, "timestmap");
				if (File.Exists(fileName))
				{
					using var sr = new StreamReader(fileName);
					lastTimeStamp = DateTime.Parse(sr.ReadLine(), CultureInfo.InvariantCulture);
				}
				else
					lastTimeStamp = DateTime.MinValue;
			}
			catch (Exception e)
			{
				Log.Warn("Timestamp could not readed.", e);
			}
		} // proc ReadLastTimeStamp

		private void NotifyCheckIdle()
		{
			var newLastTimeStamp = lastTimeStamp;

			// rescan needed
			if (rescanDelay.HasValue && (DateTime.Now - lastFullScan) > rescanDelay.Value)
				RefreshFiles(0);

			// notify files
			lock (notifyQueue)
			{
				while (true)
				{
					var item = notifyQueue.FirstOrDefault();
					if (item != null && (DateTime.Now - item.Stamp) > notifyDelay)
					{
						// update time stamp
						if (item.Stamp > newLastTimeStamp)
							newLastTimeStamp = item.Stamp;

						try
						{
							notifyQueue.RemoveAt(0); // remove

							// execute
							NotifyFile(item.File);

							// add archive flag
							if (notifyMethod == NotifyMethod.ArchiveBit)
							{
								item.File.Refresh();
								if (item.File.Exists)
									item.File.Attributes = item.File.Attributes | FileAttributes.Archive;
							}

							fileCount++;
							OnPropertyChanged(nameof(FileCount));
						}
						catch (Exception e)
						{
							// re add
							item.IncError();
							notifyQueue.Add(item);
							Log.Except(String.Format("Failed {0}.", item.File?.FullName), e);

							fileErrCount++;
							OnPropertyChanged(nameof(FileErrCount));
						}
					}
					else
						break;
				}
			}

			// update stamp
			if (notifyMethod == NotifyMethod.TimeStamp && newLastTimeStamp != lastTimeStamp)
				UpdateLastTimeStamp(newLastTimeStamp);
		} // proc NotifyCheckIdle

		private void NotifyFile(FileInfo file)
			=> CallMember("NotifyFile", file);

		#endregion

		#region -- Properties ---------------------------------------------------------

		[
		PropertyName("tw_dirlsn_filecount"),
		DisplayName("FileCount"),
		Description("Processed files."),
		Category(DirectoryListenerCategory),
		Format("N0")
		]
		public long FileCount => fileCount;

		[
		PropertyName("tw_dirlsn_filecountr"),
		DisplayName("FileCountRefresh"),
		Description("File count, that collected through the refresh."),
		Category(DirectoryListenerCategory),
		Format("N0")
		]
		public long FileCountRefresh => fileCountRefresh;
		[
		PropertyName("tw_dirlsn_fileerrcount"),
		DisplayName("FileErrCount"),
		Description("Processing errors."),
		Category(DirectoryListenerCategory),
		Format("N0")
		]
		public long FileErrCount => fileErrCount;

		#endregion
	} // class DirectoryListenerItem

	#endregion
}
