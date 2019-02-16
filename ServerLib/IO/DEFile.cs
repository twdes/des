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
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Neo.IronLua;

namespace TecWare.DE.Server.IO
{
	#region -- enum DEFileTargetExists ------------------------------------------------

	/// <summary>Action if the target file exists.</summary>
	public enum DEFileTargetExists
	{
		/// <summary>Do not execute the operation.</summary>
		Ignore,
		/// <summary>Raise an exception</summary>
		Error,
		/// <summary>Always overwrite the file.</summary>
		OverwriteAlways,
		/// <summary>Overwrite if the source is newer.</summary>
		OverwriteNewer,
		/// <summary>Create a new file name for the source and keep target.</summary>
		KeepTarget
	} // enum DEFileTargetExists

	#endregion

	#region -- class DEFile -----------------------------------------------------------

	/// <summary></summary>
	public static class DEFile
	{
		#region -- class FileDeleteTransaction ----------------------------------------

		private sealed class FileDeleteTransaction
		{
			private readonly string fullName;

			public FileDeleteTransaction(string fullName)
				=> this.fullName = fullName ?? throw new ArgumentNullException(nameof(fullName));

			public Task RollbackAsync()
				=> DeleteFileSilentAsync(fullName);

			public Task CommitAsync()
				=> DeleteFileSilentAsync(fullName);
		} // class FileDeleteTransaction

		#endregion

		#region -- class FileMoveTransaction ------------------------------------------

		private sealed class FileMoveTransaction
		{
			private readonly string sourceFileName;
			private readonly string targetFileName;
			private readonly bool forceOverwrite;

			public FileMoveTransaction(string sourceFileName, string targetFileName, bool forceOverwrite)
			{
				this.sourceFileName = sourceFileName ?? throw new ArgumentNullException(nameof(sourceFileName));
				this.targetFileName = targetFileName ?? throw new ArgumentNullException(nameof(targetFileName));
				this.forceOverwrite = forceOverwrite;
			} // ctor

			private void Move()
			{
				if (forceOverwrite && File.Exists(sourceFileName))
					File.Delete(sourceFileName);
				File.Move(targetFileName, sourceFileName);
			} // proc Move

			public Task RollbackAsync()
				=> Task.Run(new Action(Move));
		} // class FileMoveTransaction

		#endregion

		#region -- Helper -------------------------------------------------------------

		private static async Task<FileSystemInfo> GetDestinationInfoAsync(string destinationName)
		{
			if (String.IsNullOrEmpty(destinationName))
				return null;

			var c = destinationName[destinationName.Length - 1];
			if (c == Path.DirectorySeparatorChar || c == Path.AltDirectorySeparatorChar 
				|| await Task.Run(() => Directory.Exists(destinationName)))
				return new DirectoryInfo(destinationName);

			return new FileInfo(destinationName);
		} // func GetDestinationInfoAsync

		private static string GetFullNameContinue(Task<FileInfo> t)
			=> t.Result?.FullName;

		private static async Task SecureAsync(Action action, int retryCount)
		{
			if (retryCount < 0)
				retryCount = 1;

			while (retryCount > 0)
			{
				try
				{
					await Task.Run(action);
					return;
				}
				catch (IOException)
				{
					await Task.Delay(1500);
					retryCount--;
				}
			}
		} // proc SecureAsync

		#endregion

		#region -- GetUniqueFileName, CreateDestination -------------------------------

		/// <summary>Create a unique file name, if the target file exists.</summary>
		/// <param name="fileName">Target file name</param>
		/// <returns>Unique file name.</returns>
		public static string GetUniqueFileName(string fileName)
			=> GetUniqueFileName(new FileInfo(fileName)).FullName;

		/// <summary>Create a unique file name, if the target file exists.</summary>
		/// <param name="fi">Target file information.</param>
		/// <returns></returns>
		public static FileInfo GetUniqueFileName(this FileInfo fi)
		{
			if (fi == null)
				throw new ArgumentNullException(nameof(fi));

			if (fi.Exists)
			{
				var name = Path.GetFileNameWithoutExtension(fi.Name);
				var ext = fi.Extension;
				var i = 1;

				do
				{
					fi = new FileInfo(Path.Combine(fi.Directory.FullName, name + "." + i.ToString() + ext));
					i++;
				}
				while (fi.Exists);
			}
			return fi;
		} // func GetUniqueFileName

		/// <summary>Create the destination file info.</summary>
		/// <param name="sourceFileInfo"></param>
		/// <param name="destinationInfo"></param>
		/// <param name="onExists"></param>
		/// <param name="makeDestinationUnique"></param>
		/// <returns></returns>
		public static async Task<FileInfo> CreateDestinationFileInfoAsync(FileInfo sourceFileInfo, FileSystemInfo destinationInfo, DEFileTargetExists onExists, Func<FileInfo, FileInfo> makeDestinationUnique = null)
		{
			if (sourceFileInfo == null)
				throw new ArgumentNullException(nameof(sourceFileInfo));
			if (destinationInfo == null)
				throw new ArgumentNullException(nameof(destinationInfo));

			// get destination
			var destinationFileInfo = destinationInfo is FileInfo fi
				? fi
				: new FileInfo(Path.Combine(destinationInfo.FullName, sourceFileInfo.Name));

			// check destination
			if (destinationFileInfo.Exists)
			{
				switch (onExists)
				{
					case DEFileTargetExists.Ignore:
						return null;

					case DEFileTargetExists.Error:
						throw new IOException(String.Format("Destination file '{0}' already exists.", destinationFileInfo.FullName));

					case DEFileTargetExists.OverwriteNewer:
						if(sourceFileInfo.LastWriteTime <= destinationFileInfo.LastWriteTime)
							return null;
						break;
					case DEFileTargetExists.KeepTarget:
						if (makeDestinationUnique == null)
							destinationFileInfo = await Task.Run(() => GetUniqueFileName(destinationFileInfo));
						else
							destinationFileInfo = await Task.Run(() => makeDestinationUnique.Invoke(destinationFileInfo));
						break;
					case DEFileTargetExists.OverwriteAlways:
					default:
						break;

				}
			}

			// return destination file info
			if (!destinationFileInfo.Directory.Exists)
				await Task.Run(new Action(destinationFileInfo.Directory.Create));
			return destinationFileInfo;
		} // func CreateDestinationFileInfo

		/// <summary>Creates a temporary file name for a file.</summary>
		/// <param name="fileInfo"></param>
		/// <returns></returns>
		public static async Task<FileInfo> CreateTempFileInfoAsync(FileInfo fileInfo)
		{
			if (fileInfo == null)
				throw new ArgumentNullException(nameof(fileInfo));

			var name = fileInfo.Name;
			var directory = fileInfo.Directory.FullName;
			var i = 0;

			FileInfo GetInfo()
			{
				var fi = new FileInfo(Path.Combine(directory, (i == 0 ? "~" : "~" + i.ToString() + "~") + name));
				return fi.Exists ? null : fi;
			}

			FileInfo r;
			while ((r = await Task.Run(new Func<FileInfo>(GetInfo))) == null)
				i++;

			return r;
		} // func CreateTempFileInfoAsync

		#endregion

		#region -- DeleteAsync --------------------------------------------------------

		private static async Task<bool> DeleteFileSilentAsync(string fullName)
		{
			try
			{
				if (File.Exists(fullName))
					await SecureAsync(new Action(() => File.Delete(fullName)), 3);
				return true;
			}
			catch (IOException)
			{
				return false;
			}
		} // func DeleteFileSilentAsync

		private static async Task DeleteAsync(IDECommonScope scope, FileInfo fileInfo)
		{
			var oldDestinationFileInfo = await CreateTempFileInfoAsync(fileInfo); // create temp file name
			File.Move(fileInfo.FullName, oldDestinationFileInfo.FullName); // rename file

			// on commit delete moved file
			scope.RegisterCommitAction(new FileDeleteTransaction(oldDestinationFileInfo.FullName).CommitAsync);
			// on rollback move file back
			scope.RegisterRollbackAction(new FileMoveTransaction(fileInfo.FullName, oldDestinationFileInfo.FullName, true).RollbackAsync);
		} // func DeleteAsync

		/// <summary>Delete the file</summary>
		/// <param name="fileInfo">File info to delete.</param>
		/// <returns></returns>
		public static Task DeleteAsync(this FileInfo fileInfo)
			=> DeleteAsync(DEScope.GetScopeService<IDECommonScope>(true), fileInfo);

		/// <summary>Delete the file</summary>
		/// <param name="fileName">File name to delete.</param>
		/// <returns></returns>
		public static Task DeleteAsync(string fileName)
			=> DeleteAsync(new FileInfo(fileName));

		#endregion

		#region -- CopyAsync ----------------------------------------------------------

		/// <summary>Copy file with in an transaction.</summary>
		/// <param name="sourceFileInfo">Source file info.</param>
		/// <param name="destinationInfo">Destination file or directory info.</param>
		/// <param name="onExists">Action if the target exists.</param>
		/// <param name="makeDestinationUnique">Function to make the target unique.</param>
		/// <returns>FileInfo if the file was copied, or <c>null</c>.</returns>
		public async static Task<FileInfo> CopyAsync(this FileInfo sourceFileInfo, FileSystemInfo destinationInfo, DEFileTargetExists onExists = DEFileTargetExists.Error, Func<FileInfo, FileInfo> makeDestinationUnique = null)
		{
			var scope = DEScope.GetScopeService<IDECommonScope>(true);

			var destinationFileInfo = await CreateDestinationFileInfoAsync(sourceFileInfo, destinationInfo, onExists, makeDestinationUnique);
			if (destinationFileInfo == null) // abort copy opertation
				return null;

			// build copy transaction
			if (destinationFileInfo.Exists)
			{
				// delete file that will be overwritten
				await DeleteAsync(scope, destinationFileInfo);

				// copy file, 
				//   delete file transaction will move the old file back on rollback
				//   commit needs no actions, because copy does not touch the source
				File.Copy(sourceFileInfo.FullName, destinationFileInfo.FullName);
				
				// set destination attributes
				destinationFileInfo.CreationTimeUtc = sourceFileInfo.CreationTimeUtc;
				destinationFileInfo.LastWriteTimeUtc = sourceFileInfo.LastWriteTimeUtc;
				destinationFileInfo.Attributes = sourceFileInfo.Attributes;
			}
			else
			{
				// add rollback delete for destination
				scope.RegisterRollbackAction(new FileDeleteTransaction(destinationFileInfo.FullName).RollbackAsync);
				// copy file
				await Task.Run(() => File.Copy(sourceFileInfo.FullName, destinationFileInfo.FullName));
			}

			return destinationFileInfo;
		} // func CopyAsnyc

		/// <summary>Copy file with in an transaction.</summary>
		/// <param name="sourceFileName">Source file name.</param>
		/// <param name="destinationName">Destination file or directory name.</param>
		/// <param name="onExists">Action if the target exists.</param>
		/// <param name="makeDestinationUnique">Function to make the target unique.</param>
		/// <returns>FileName if the file was copied, or <c>null</c>.</returns>
		public static async Task<string> CopyAsync(string sourceFileName, string destinationName, DEFileTargetExists onExists = DEFileTargetExists.Error, Func<FileInfo, FileInfo> makeDestinationUnique = null)
			=> (await CopyAsync(new FileInfo(sourceFileName), await GetDestinationInfoAsync(destinationName), onExists, makeDestinationUnique))?.FullName;

		#endregion

		#region -- MoveAsync ----------------------------------------------------------

		/// <summary>Move file within an transaction.</summary>
		/// <param name="sourceFileInfo">Source file info.</param>
		/// <param name="destinationInfo">Destination file or directory info.</param>
		/// <param name="onExists">Action if the target exists.</param>
		/// <param name="makeDestinationUnique">Function to make the target unique.</param>
		/// <returns>FileInfo if the file was moved, or <c>null</c>.</returns>
		public async static Task<FileInfo> MoveAsync(this FileInfo sourceFileInfo, FileSystemInfo destinationInfo, DEFileTargetExists onExists = DEFileTargetExists.Error, Func<FileInfo, FileInfo> makeDestinationUnique = null)
		{
			var scope = DEScope.GetScopeService<IDECommonScope>(true);

			var destinationFileInfo = await CreateDestinationFileInfoAsync(sourceFileInfo, destinationInfo, onExists, makeDestinationUnique);
			if (destinationFileInfo == null) // abort copy opertation
				return null;

			// build copy transaction
			if (destinationFileInfo.Exists)
			{
				// delete file that will be overwritten
				await DeleteAsync(scope, destinationFileInfo);
			}

			// check root, if we move or copy the file
			if (String.Compare(destinationFileInfo.Directory.Root.FullName, sourceFileInfo.Directory.Root.FullName, StringComparison.OrdinalIgnoreCase) == 0)
			{
				await Task.Run(() => File.Move(sourceFileInfo.FullName, destinationFileInfo.FullName));
				scope.RegisterRollbackAction(new FileMoveTransaction(sourceFileInfo.FullName, destinationFileInfo.FullName, true).RollbackAsync);
			}
			else // root is different, copy file
			{
				// on rollback delete destination file
				scope.RegisterRollbackAction(new FileDeleteTransaction(destinationFileInfo.FullName).RollbackAsync);
				// copy file
				await Task.Run(() => File.Copy(sourceFileInfo.FullName, destinationFileInfo.FullName));
				// move source file to an tmp file
				var deletedFileInfo = await CreateTempFileInfoAsync(sourceFileInfo);
				await Task.Run(() => File.Move(sourceFileInfo.FullName, deletedFileInfo.FullName));

				// on commit delete tmp file
				scope.RegisterCommitAction(new FileDeleteTransaction(deletedFileInfo.FullName).CommitAsync);
				scope.RegisterRollbackAction(new FileMoveTransaction(sourceFileInfo.FullName, deletedFileInfo.FullName, true).RollbackAsync);
			}

			return destinationFileInfo;
		} // func MoveAsync

		/// <summary>Move file within an transaction.</summary>
		/// <param name="sourceFileName">Source file name.</param>
		/// <param name="destinationName">Destination file or directory name.</param>
		/// <param name="onExists">Action if the target exists.</param>
		/// <param name="makeDestinationUnique">Function to make the target unique.</param>
		/// <returns>FileName if the file was moved, or <c>null</c>.</returns>
		public static async Task<string> MoveAsync(string sourceFileName, string destinationName, DEFileTargetExists onExists = DEFileTargetExists.Error, Func<FileInfo, FileInfo> makeDestinationUnique = null)
			=> (await MoveAsync(new FileInfo(sourceFileName), await GetDestinationInfoAsync(destinationName), onExists, makeDestinationUnique))?.FullName;

		#endregion

		#region -- OpenAsync ----------------------------------------------------------

		#region -- class LuaMemoryTransaction -----------------------------------------

		private sealed class LuaMemoryTransaction : LuaFile
		{
			public LuaMemoryTransaction(TextReader tr, TextWriter tw) 
				: base(tr, tw)
			{
			} // ctor

			public override long Length => base.Length;

			public override void flush() => base.flush();
			public override LuaResult seek(string whence, long offset = 0) => base.seek(whence, offset);
			protected override void Dispose(bool disposing) => base.Dispose(disposing);
		} // class LuaMemoryTransaction

		#endregion

		/// <summary>Open a file in with write access. All write operations will persist
		/// in memory and write to disk on commit.</summary>
		/// <param name="fileName"></param>
		/// <param name="encoding"></param>
		/// <returns></returns>
		public static Task<LuaFile> OpenInMemoryAsync(string fileName, Encoding encoding = null)
		{
			throw new NotImplementedException("todo");
		} // func OpenInMemoryAsync

		/// <summary>Open a file, all operation will be done in a copy of the file. On 
		/// commit the source will be overwritten (needs more disk space).</summary>
		/// <param name="fileName"></param>
		/// <param name="encoding"></param>
		/// <returns></returns>
		public static Task<LuaFile> OpenCopyAsync(string fileName, Encoding encoding = null)
		{
			throw new NotImplementedException("todo");
		} // func OpenCopyAsync

		#endregion
	} // class DEFile

	#endregion
}