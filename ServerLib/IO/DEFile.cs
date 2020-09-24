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
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Win32;
using TecWare.DE.Stuff;

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

	#region -- class DETransactionStream ----------------------------------------------

	/// <summary>Interface for extented stream methods.</summary>
	public abstract class DETransactionStream : Stream
	{
		/// <summary>Commit the file changes.</summary>
		/// <returns></returns>
		public abstract Task CommitAsync();

		/// <summary>Rollback the file changes.</summary>
		/// <returns></returns>
		public abstract Task RollbackAsync();

		/// <summary>Commit the file changes.</summary>
		public void Commit()
			=> CommitAsync().AwaitTask();

		/// <summary>Rollback the file changes.</summary>
		public void Rollback()
			=> RollbackAsync().AwaitTask();

		/// <summary>Create a text access for read.</summary>
		/// <param name="encoding"></param>
		/// <returns></returns>
		public TextReader CreateTextReader(Encoding encoding = null)
			=> new StreamReader(this, encoding ?? Encoding.UTF8, false, 4096, true);

		/// <summary>Create a text access for write.</summary>
		/// <param name="encoding"></param>
		/// <returns></returns>
		public TextWriter CreateTextWriter(Encoding encoding = null)
			=> new StreamWriter(this, encoding ?? Encoding.UTF8, 4096, true);
	} // class DETransactionStream

	#endregion

	#region -- class ShareInfo --------------------------------------------------------

	/// <summary>Simple structure to represent a share.</summary>
	public sealed class ShareInfo
	{
		/// <summary>Initialize share info</summary>
		/// <param name="name">Name of the share.</param>
		/// <param name="path">Local path of the share.</param>
		/// <param name="remark"></param>
		/// <param name="maxUses"></param> 
		public ShareInfo(string name, string path, string remark, uint maxUses)
		{
			Name = name ?? throw new ArgumentNullException(nameof(name));
			Path = path ?? throw new ArgumentNullException(nameof(path));

			Remark = remark;
			MaxUses = maxUses;
		} // ctor

		/// <summary>Name of the share.</summary>
		public string Name { get; }
		/// <summary>Local path of the share.</summary>
		public string Path { get; }
		/// <summary>Remark of the share.</summary>
		public string Remark { get; }
		/// <summary>Number of users.</summary>
		public uint MaxUses { get; }

		// -- Static ----------------------------------------------------------

		private static readonly CachedProperty<Dictionary<string, ShareInfo>> shares = new CachedProperty<Dictionary<string, ShareInfo>>(new Dictionary<string, ShareInfo>(StringComparer.OrdinalIgnoreCase), ReadShares, 10000);

		private static bool TryParseShareInfo(string[] values, string name, out ShareInfo share)
		{
			string path = null;
			string remark = null;
			var maxUses = 0u;

			foreach (var c in values)
			{
				if (c.StartsWith("ShareName=", StringComparison.OrdinalIgnoreCase))
					name = c.Substring(10).Trim();
				else if (c.StartsWith("Path=", StringComparison.OrdinalIgnoreCase))
					path = c.Substring(5).Trim();
				else if (c.StartsWith("Remark=", StringComparison.OrdinalIgnoreCase))
					remark = c.Substring(7).Trim();
				else if (c.StartsWith("MaxUses=", StringComparison.OrdinalIgnoreCase) && UInt32.TryParse(c.Substring(8), out var tmp))
					maxUses = tmp;
			}

			if (String.IsNullOrEmpty(path))
			{
				share = null;
				return false;
			}
			else
			{
				share = new ShareInfo(name, Procs.IncludeTrailingBackslash(path), remark, maxUses);
				return true;
			}
		} // func TryParseShareInfo

		private static void ReadShares(Dictionary<string, ShareInfo> value)
		{
			value.Clear();

			using (var r = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\LanmanServer\Shares", false))
			{
				foreach (var n in r.GetValueNames())
				{
					if (r.GetValue(n) is string[] values && TryParseShareInfo(values, n, out var share))
						value[share.Name] = share;
				}
			}
		} // proc ReadShares

		/// <summary>Translate a local path to an remotePath.</summary>
		/// <param name="localPath"></param>
		/// <param name="uncPath"></param>
		/// <returns></returns>
		public static bool TryLocalToUnc(string localPath, out string uncPath)
		{
			localPath = System.IO.Path.GetFullPath(localPath);

			lock (shares)
			{
				var shareName = String.Empty;
				var sharePathLength = -1;

				foreach (var v in shares.Value)
				{
					if (localPath.StartsWith(v.Value.Path, StringComparison.OrdinalIgnoreCase) && sharePathLength < v.Value.Path.Length)
					{
						shareName = v.Key;
						sharePathLength = v.Value.Path.Length;
					}

				}

				if (sharePathLength <= 0)
				{
					uncPath = localPath;
					return false;
				}

				uncPath = "\\\\" + Environment.MachineName + "\\" + shareName + "\\" + localPath.Substring(sharePathLength);
				return true;
			}
		} // func TryLocalToUnc

		/// <summary>Translate a remote path to an unc path.</summary>
		/// <param name="uncPath"></param>
		/// <param name="localPath"></param>
		/// <returns></returns>
		public static bool TryUncToLocal(string uncPath, out string localPath)
		{
			lock (shares)
			{
				if (Procs.TrySplitUncPath(uncPath, out var serverName, out var shareName, out var sharePath)
					&& String.Compare(serverName, Environment.MachineName, StringComparison.OrdinalIgnoreCase) == 0
					&& shares.Value.TryGetValue(shareName, out var share))
				{
					localPath = share.Path + sharePath;
					return true;
				}
				else
				{
					localPath = uncPath;
					return false;
				}
			}
		} // func TryUncToLocal

		/// <summary>Return all shares.</summary>
		public static IReadOnlyList<ShareInfo> Shares
		{
			get
			{
				lock (shares)
					return shares.Value.Values.ToArray();
			}
		} // func GetShares
	} // class ShareInfo

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

		#region -- class TransactionStream --------------------------------------------

		private abstract class TransactionStream : DETransactionStream, IDETransactionAsync
		{
			#region -- struct BlockInfo -----------------------------------------------

			private struct BlockInfo
			{
				public int Index;
				public int Ofs;
				public int Len;
			} // struct BlockInfo

			#endregion

			protected const int blockBits = 16;
			protected const int blockSize = 1 << blockBits; // 64k
			protected const int blockMask = blockSize - 1;

			private readonly bool isNewStream;
			private readonly Stream stream;
			private long position = 0;
			private long length = 0;

			private bool? commited = null;

			#region -- Ctor/Dtor ------------------------------------------------------

			public TransactionStream(Stream stream, bool isNewStream)
			{
				this.stream = stream ?? throw new ArgumentNullException(nameof(stream));
				this.isNewStream = isNewStream;
				position = stream.Position;
				length = stream.Length;

				if (!stream.CanSeek
					|| !stream.CanRead
					|| !stream.CanWrite)
					throw new ArgumentException("Stream must be seek-, read- and writable.", nameof(stream));
			} // ctor

			protected override void Dispose(bool disposing)
			{
				if (!commited.HasValue)
					RollbackAsync().AwaitTask();
			} // proc Dispose

			public sealed override async Task CommitAsync()
			{
				if (commited.HasValue)
					throw new InvalidOperationException();

				await CommitCoreAsync(stream);
				stream.Dispose();

				commited = true;
			} // func CommitAsync

			protected virtual async Task CommitCoreAsync(Stream stream)
			{
				var setOffset = true;
				var blockIndex = 0;
				var pos = 0L;
				while (pos < Length)
				{
					var buf = ReadBlock(blockIndex);
					if (buf == null)
						setOffset = true;
					else
					{
						if (setOffset)
						{
							stream.Seek(pos, SeekOrigin.Begin);
							setOffset = false;
						}

						var r = Length - pos;
						await stream.WriteAsync(buf, 0, blockSize < r ? blockSize : unchecked((int)r));
					}

					pos += blockSize;
					blockIndex++;
				}
			} // func CommitCoreAsync

			public sealed override async Task RollbackAsync()
			{
				if (commited.HasValue)
					throw new InvalidOperationException();

				await RollbackCoreAsync();
				stream.Dispose();

				if (isNewStream && stream is FileStream fs)
					await DeleteFileSilentAsync(fs.Name);

				commited = false;
			} // proc Rollback

			protected virtual Task RollbackCoreAsync()
				=> Task.CompletedTask;

			#endregion

			protected abstract byte[] ReadBlock(int index);

			protected abstract void WriteBlock(int index, byte[] block);

			public sealed override void Flush() { }

			private IEnumerable<BlockInfo> GetBlockOffset(long position, int count)
			{
				// first block
				var blockIndex = (int)(position >> blockBits);
				var blockOfs = (int)(position & blockMask);
				var blockLen = blockSize - blockOfs;
				if (blockLen >= count)
					yield return new BlockInfo() { Index = blockIndex, Ofs = blockOfs, Len = count };
				else
				{
					yield return new BlockInfo() { Index = blockIndex, Ofs = blockOfs, Len = blockLen };

					count -= blockLen;
					blockIndex++;

					// next blocks
					while (count > 0)
					{
						blockLen = Math.Min(blockSize, count);
						yield return new BlockInfo() { Index = blockIndex, Ofs = 0, Len = blockLen };

						count -= blockLen;
						blockIndex++;
					}
				}
			} // func GetBlockOffset

			public sealed override int Read(byte[] buffer, int offset, int count)
			{
				var readed = 0;

				// check count, align it to the total length
				if (position + count > length)
				{
					count = checked((int)(length - position));
					if (count < 0)
						throw new ArgumentOutOfRangeException(nameof(count));
					else if (count == 0)
						return 0;
				}

				// enumerate the blocks to read
				foreach (var b in GetBlockOffset(position, count))
				{
					var block = ReadBlock(b.Index);
					if (block != null)
						Array.Copy(block, b.Ofs, buffer, offset, b.Len);
					else
					{
						stream.Position = position;
						if (stream.Read(buffer, offset, b.Len) != b.Len)
							throw new InvalidOperationException();
					}

					offset += b.Len;
					readed += b.Len;
					position += b.Len;
				}

				return readed;
			} // func Read

			public sealed override void Write(byte[] buffer, int offset, int count)
			{
				foreach (var b in GetBlockOffset(position, count))
				{
					var block = ReadBlock(b.Index);

					if (block == null)
					{
						block = new byte[blockSize];
						// read current content
						if (position < stream.Length)
						{
							stream.Position = position;
							stream.Read(block, b.Ofs, b.Len);
						}
					}

					// copy content
					var c = Math.Min(b.Len, count);
					Array.Copy(buffer, offset, block, b.Ofs, c);
					// write block change
					WriteBlock(b.Index, block);

					count -= c;
					offset += c;
					position += c;
					if (position > length)
						length = position;
				}
			} // proc Write

			public sealed override long Seek(long offset, SeekOrigin origin)
			{
				long GetNewPosition()
				{
					switch (origin)
					{
						case SeekOrigin.Begin:
							return offset;
						case SeekOrigin.Current:
							return position + offset;
						case SeekOrigin.End:
							return Length - offset;
						default:
							throw new ArgumentOutOfRangeException(nameof(origin));
					}
				} // GetNewPosition

				var newPosition = GetNewPosition();
				if (newPosition > Length)
					throw new ArgumentOutOfRangeException(nameof(offset));

				return position = newPosition;
			} // func Seek

			public override void SetLength(long value)
				=> length = value;

			public override bool CanSeek => true;
			public override bool CanWrite => true;
			public override bool CanRead => true;

			public override long Position { get => position; set => Seek(value, SeekOrigin.Begin); }
			public override long Length => length;
		} // class TransactionStream

		#endregion

		#region -- class MemoryTransactionStream --------------------------------------

		private sealed class MemoryTransactionStream : TransactionStream
		{
			private readonly List<byte[]> blocks = new List<byte[]>();

			public MemoryTransactionStream(FileStream stream, bool isNew)
				: base(stream, isNew)
			{
			} // ctor

			protected override async Task CommitCoreAsync(Stream stream)
			{
				var setOffset = true;
				var blockIndex = 0;
				var pos = 0L;
				while(pos < Length)
				{
					var buf = ReadBlock(blockIndex);
					if (buf == null)
						setOffset = true;
					else
					{
						if (setOffset)
						{
							stream.Seek(pos, SeekOrigin.Begin);
							setOffset = false;
						}

						var r = Length - pos;
						await stream.WriteAsync(buf, 0, blockSize < r ? blockSize : unchecked((int)r));
					}

					pos += blockSize;
					blockIndex++;
				}
			} // proc CommitCoreAsync

			protected override Task RollbackCoreAsync()
			{
				blocks.Clear();
				return base.RollbackCoreAsync();
			} // proc RollbackCoreAsync

			protected override byte[] ReadBlock(int index)
				=> index >= 0 && index < blocks.Count ? blocks[index] : null;

			protected override void WriteBlock(int index, byte[] block)
			{
				if (index < 0)
					throw new ArgumentOutOfRangeException(nameof(index), index, "Index is negative.");

				// reserve blocks
				while (blocks.Count <= index)
					blocks.Add(null);

				// set block data
				blocks[index] = block;
			} // proc WriteBlock
		} // class MemoryTransactionStream

		#endregion

		#region -- class DiskTransactionStream ----------------------------------------

		private sealed class DiskTransactionStream : TransactionStream, IDETransactionAsync
		{
			private readonly FileInfo fileInfo;
			private readonly FileInfo transactionFileInfo;
			private readonly FileStream transactionStream;
			private readonly List<long> blockOffsets = new List<long>();
			
			private int currentBlockIndex = -1;
			private readonly byte[] currentBlock;

			public DiskTransactionStream(FileStream stream, bool isNew, FileInfo fileInfo, FileInfo transactionFileInfo)
				: base(stream, isNew)
			{
				this.fileInfo = fileInfo;
				this.transactionFileInfo = transactionFileInfo;

				transactionStream = transactionFileInfo.Open(FileMode.CreateNew);

				currentBlock = new byte[blockSize];
			} // ctor

			protected override async Task CommitCoreAsync(Stream stream)
			{
				// is the temp file the target file?
				if (IsSequence()) // move whole file
				{
					// close target
					stream.Dispose();

					// truncate file to correct size
					transactionStream.SetLength(Length);
					transactionStream.Dispose();

					// move transaction file as file
					if (File.Exists(fileInfo.FullName))
						File.Delete(fileInfo.FullName);
					File.Move(transactionFileInfo.FullName, fileInfo.FullName);
				}
				else // copy parts back
				{
					await base.CommitCoreAsync(stream);
					transactionStream.Dispose();

					// delete transaction
					File.Delete(transactionFileInfo.FullName);
				}
			} // proc CommitCoreAsync

			private bool IsSequence()
			{
				if (transactionStream.Length < Length) // not all blocks in file
					return false;

				// check block order
				var pos = 0L;
				for (var i = 0; i < blockOffsets.Count; i++)
				{
					if (blockOffsets[i] != pos)
						return false;
					pos += blockSize;
				}

				return true;
			} // func IsSequence

			protected override async Task RollbackCoreAsync()
			{
				try
				{
					transactionStream.Dispose();

					await DeleteFileSilentAsync(transactionFileInfo.FullName);
				}
				catch { }
			} // proc RollbackCoreAsync

			private long GetBlockOffset(int index)
				=> index >= 0 && index < blockOffsets.Count ? blockOffsets[index] : -1L;

			protected override byte[] ReadBlock(int index)
			{
				if (index == currentBlockIndex)
					return currentBlock;

				currentBlockIndex = index;
				var ofs = GetBlockOffset(currentBlockIndex);
				if (ofs < 0)
					return null;

				// get block from file
				transactionStream.Seek((long)currentBlockIndex << blockBits, SeekOrigin.Begin);
				transactionStream.Read(currentBlock, 0, blockSize);
				return currentBlock;
			} // func ReadBlock

			protected override void WriteBlock(int index, byte[] block)
			{
				if (index < 0)
					throw new ArgumentOutOfRangeException(nameof(index), index, "Index is negative.");

				// reserve blocks
				while (blockOffsets.Count <= index)
					blockOffsets.Add(-1L);

				// set block data
				var ofs = GetBlockOffset(index);
				if (ofs < 0) // write new block
				{
					ofs = transactionStream.Length;
					blockOffsets[index] = ofs;
				}

				transactionStream.Seek(ofs, SeekOrigin.Begin);
				transactionStream.Write(block, 0, blockSize);
			} // proc WriteBlock
		} // class MemoryTransactionStream

		#endregion

		private static (FileStream stream, bool isNew) CreateOrOpenFile(string fileName)
		{
			var fileMode = File.Exists(fileName) ? FileMode.Open : FileMode.Create;
			return (new FileStream(fileName, fileMode, FileAccess.ReadWrite, FileShare.None), fileMode != FileMode.Open);
		} // func CreateOrOpenFile

		/// <summary>Open a file in with write access. All write operations will persist
		/// in memory and write to disk on commit.</summary>
		/// <param name="fileName"></param>
		/// <returns></returns>
		public static Task<DETransactionStream> OpenInMemoryAsync(string fileName)
		{
			return Task.Run<DETransactionStream>(() =>
				{
					var (s, n) = CreateOrOpenFile(fileName);
					return new MemoryTransactionStream(s, n);
				}
			);
		} // func OpenInMemoryAsync

		/// <summary>Open a file, all operation will be done in a copy of the file. On 
		/// commit the source will be overwritten (needs more disk space).</summary>
		/// <param name="fileName"></param>
		/// <returns></returns>
		public static async Task<DETransactionStream> OpenCopyAsync(string fileName)
		{
			var fileInfo = new FileInfo(fileName);
			var transactionFileInfo = await CreateTempFileInfoAsync(fileInfo);
			return await Task.Run<DETransactionStream>(() =>
				{
					var (stream, isNew) = CreateOrOpenFile(fileInfo.FullName);
					return new DiskTransactionStream(stream, isNew, fileInfo, transactionFileInfo);
				}
			);
		} // func OpenCopyAsync

		#endregion
	} // class DEFile

	#endregion
}