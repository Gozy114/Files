// Copyright (c) Files Community
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using System.IO;
using System.Runtime.InteropServices;

namespace Files.App.Services.Search
{
	/// <summary>
	/// Loads the Everything SDK2 client for this process architecture and talks to it on one gate.
	/// SDK2 keeps a single process-global query, so overlapping calls mix results and can overflow.
	/// Everything 1.5 folder size is used only when an Everything3 DLL is already installed.
	/// </summary>
	public sealed unsafe partial class EverythingService : IEverythingService, IDisposable
	{
		private const uint EverythingErrorIpc = 2;

		private const uint RequestFileName = 0x00000001;
		private const uint RequestPath = 0x00000002;
		private const uint RequestSize = 0x00000010;
		private const uint RequestDateCreated = 0x00000020;
		private const uint RequestDateModified = 0x00000040;
		private const uint RequestAttributes = 0x00000100;

		private const uint FileAttributeHidden = 0x2;
		private const uint FileAttributeDirectory = 0x10;
		private const uint FileAttributeReparsePoint = 0x400;

		private const uint SearchFlags = RequestFileName | RequestPath | RequestSize | RequestDateCreated | RequestDateModified | RequestAttributes;
		private const uint FolderSizeFlags = RequestFileName | RequestPath | RequestSize;

		private const int SearchResultCap = 1000;
		private const uint FolderSizePageSize = 8192;
		private const uint FolderSizeFileCap = 250_000;

		private static readonly TimeSpan AvailabilityCacheDuration = TimeSpan.FromSeconds(30);

		private readonly ILogger<EverythingService> _logger;
		private readonly SemaphoreSlim _gate = new(1, 1);

		private bool _sdk2Ready;
		private bool _loadFailed;
		private bool _loggedLoadFailure;
		private bool _loggedUnavailable;
		private bool _loggedAvailable;
		private bool _available;
		private DateTime _availableUntil;

		private IntPtr _sdk3Module;
		private IntPtr _sdk3Client;
		private bool _sdk3Missing;
		private bool _loggedSdk3;
		private DateTime _sdk3RetryAfter;
		private bool _disposed;

		private unsafe delegate* unmanaged[Stdcall]<char*, void> _setSearchW;
		private unsafe delegate* unmanaged[Stdcall]<int, void> _setMatchCase;
		private unsafe delegate* unmanaged[Stdcall]<int, void> _setMatchPath;
		private unsafe delegate* unmanaged[Stdcall]<int, void> _setMatchWholeWord;
		private unsafe delegate* unmanaged[Stdcall]<int, void> _setRegex;
		private unsafe delegate* unmanaged[Stdcall]<uint, void> _setMax;
		private unsafe delegate* unmanaged[Stdcall]<uint, void> _setOffset;
		private unsafe delegate* unmanaged[Stdcall]<uint, void> _setRequestFlags;
		private unsafe delegate* unmanaged[Stdcall]<int, int> _queryW;
		private unsafe delegate* unmanaged[Stdcall]<uint> _getNumResults;
		private unsafe delegate* unmanaged[Stdcall]<uint> _getLastError;
		private unsafe delegate* unmanaged[Stdcall]<int> _isDbLoaded;
		private unsafe delegate* unmanaged[Stdcall]<uint, int> _isFolderResult;
		private unsafe delegate* unmanaged[Stdcall]<uint, char*> _getResultFileNameW;
		private unsafe delegate* unmanaged[Stdcall]<uint, char*> _getResultPathW;
		private unsafe delegate* unmanaged[Stdcall]<uint, long*, int> _getResultSize;
		private unsafe delegate* unmanaged[Stdcall]<uint, long*, int> _getResultDateModified;
		private unsafe delegate* unmanaged[Stdcall]<uint, long*, int> _getResultDateCreated;
		private unsafe delegate* unmanaged[Stdcall]<uint, uint> _getResultAttributes;
		private unsafe delegate* unmanaged[Stdcall]<void> _reset;

		private unsafe delegate* unmanaged[Stdcall]<char*, IntPtr> _sdk3ConnectW;
		private unsafe delegate* unmanaged[Stdcall]<IntPtr, char*, ulong> _sdk3GetFolderSize;
		private unsafe delegate* unmanaged[Stdcall]<IntPtr, int> _sdk3Destroy;

		public EverythingService(ILogger<EverythingService> logger)
		{
			_logger = logger;
		}

		public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
		{
			return Task.Run(() =>
			{
				_gate.Wait(cancellationToken);
				try
				{
					return IsAvailableLocked();
				}
				finally
				{
					_gate.Release();
				}
			}, cancellationToken);
		}

		public Task<IReadOnlyList<EverythingSearchHit>> SearchAsync(string? query, string? folder, int maxResults, CancellationToken cancellationToken = default)
		{
			return Task.Run(() => SearchLocked(query, folder, maxResults, cancellationToken), cancellationToken);
		}

		public Task<EverythingFolderSize> GetFolderSizeAsync(string path, CancellationToken cancellationToken = default)
		{
			return Task.Run(() => FolderSizeLocked(path, cancellationToken), cancellationToken);
		}

		public void Dispose()
		{
			_gate.Wait();
			try
			{
				_disposed = true;
				DestroySdk3();
			}
			finally
			{
				_gate.Release();
			}
		}

		private bool IsAvailableLocked()
		{
			if (_disposed || _loadFailed)
				return false;

			if (DateTime.UtcNow < _availableUntil)
				return _available;

			if (!EnsureSdk2Loaded())
				return false;

			var available = Query("files-everything-probe", 1, 0, RequestFileName) == QueryStatus.Ok;
			RememberAvailability(available);
			return available;
		}

		private IReadOnlyList<EverythingSearchHit> SearchLocked(string? query, string? folder, int maxResults, CancellationToken cancellationToken)
		{
			_gate.Wait(cancellationToken);
			try
			{
				if (_disposed || !IsAvailableLocked())
					return Array.Empty<EverythingSearchHit>();

				var limit = maxResults <= 0 ? SearchResultCap : Math.Min(maxResults, SearchResultCap);
				var search = BuildSearchQuery(query, folder);
				if (search.Length == 0)
					return Array.Empty<EverythingSearchHit>();

				cancellationToken.ThrowIfCancellationRequested();
				var status = Query(search, (uint)limit, 0, SearchFlags);
				if (status == QueryStatus.IpcFailure)
				{
					RememberAvailability(false);
					return Array.Empty<EverythingSearchHit>();
				}

				if (status != QueryStatus.Ok)
					return Array.Empty<EverythingSearchHit>();

				RememberAvailability(true);
				var count = _getNumResults();
				if (count > limit)
					count = (uint)limit;

				var results = new List<EverythingSearchHit>((int)count);
				for (uint index = 0; index < count; index++)
				{
					cancellationToken.ThrowIfCancellationRequested();
					if (TryReadHit(index, out var hit))
						results.Add(hit);
				}

				return results;
			}
			finally
			{
				_gate.Release();
			}
		}

		private EverythingFolderSize FolderSizeLocked(string path, CancellationToken cancellationToken)
		{
			if (string.IsNullOrWhiteSpace(path))
				return new EverythingFolderSize(false, 0);

			_gate.Wait(cancellationToken);
			try
			{
				if (_disposed)
					return new EverythingFolderSize(false, 0);

				var folder = NormalizeFolder(path);
				if (TrySdk3FolderSize(folder, out var sdk3Size))
					return new EverythingFolderSize(true, sdk3Size);

				if (!EnsureSdk2Loaded() || _isDbLoaded() == 0)
					return new EverythingFolderSize(false, 0);

				var sawFile = false;
				var sawOutside = false;
				ulong total = 0;
				uint offset = 0;
				var query = BuildFolderSizeQuery(folder);

				while (true)
				{
					cancellationToken.ThrowIfCancellationRequested();
					var window = Math.Min(FolderSizePageSize, FolderSizeFileCap - offset);
					if (window == 0)
						return new EverythingFolderSize(false, 0);

					var status = Query(query, window, offset, FolderSizeFlags);
					if (status == QueryStatus.IpcFailure)
					{
						RememberAvailability(false);
						return new EverythingFolderSize(false, 0);
					}

					if (status != QueryStatus.Ok)
						return new EverythingFolderSize(false, 0);

					var count = _getNumResults();
					if (count == 0)
						break;

					for (uint index = 0; index < count; index++)
					{
						cancellationToken.ThrowIfCancellationRequested();
						if (_isFolderResult(index) != 0)
							continue;

						var attributes = _getResultAttributes(index);
						if ((attributes & FileAttributeReparsePoint) != 0)
							continue;

						var fullPath = ReadFullPath(index);
						if (fullPath is null || !IsUnderFolder(folder, fullPath))
						{
							sawOutside = true;
							continue;
						}

						var size = ReadSize(index);
						if (size > 0)
							total += (ulong)size;
						sawFile = true;
					}

					if (count < window)
						break;

					offset += count;
					if (offset >= FolderSizeFileCap)
						return new EverythingFolderSize(false, 0);
				}

				if (sawOutside && !sawFile)
					return new EverythingFolderSize(false, 0);

				return new EverythingFolderSize(true, total);
			}
			finally
			{
				_gate.Release();
			}
		}

		private unsafe bool TryReadHit(uint index, out EverythingSearchHit hit)
		{
			hit = default;
			var name = ReadText(_getResultFileNameW, index);
			var directory = ReadText(_getResultPathW, index);
			if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(directory))
				return false;

			var fullPath = Path.Combine(directory, name);
			var attributes = _getResultAttributes(index);
			var isFolder = _isFolderResult(index) != 0 || (attributes & FileAttributeDirectory) != 0;
			hit = new EverythingSearchHit(
				fullPath,
				name,
				isFolder,
				isFolder ? 0 : ReadSize(index),
				ReadFileTime(_getResultDateModified, index),
				ReadFileTime(_getResultDateCreated, index),
				(attributes & FileAttributeHidden) != 0);
			return true;
		}

		private unsafe string? ReadFullPath(uint index)
		{
			var name = ReadText(_getResultFileNameW, index);
			var directory = ReadText(_getResultPathW, index);
			if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(directory))
				return null;

			return Path.Combine(directory, name);
		}

		private unsafe long ReadSize(uint index)
		{
			long size;
			if (_getResultSize(index, &size) == 0 || size <= 0)
				return 0;

			return size;
		}

		private unsafe DateTime? ReadFileTime(delegate* unmanaged[Stdcall]<uint, long*, int> getter, uint index)
		{
			long fileTime;
			if (getter(index, &fileTime) == 0 || fileTime <= 0)
				return null;

			try
			{
				return DateTime.FromFileTimeUtc(fileTime);
			}
			catch (ArgumentOutOfRangeException)
			{
				return null;
			}
		}

		private unsafe string? ReadText(delegate* unmanaged[Stdcall]<uint, char*> getter, uint index)
		{
			var text = getter(index);
			return text is null ? null : new string(text);
		}

		private unsafe QueryStatus Query(string search, uint max, uint offset, uint flags)
		{
			_reset();
			SetSearch(search);
			_setMatchCase(0);
			_setMatchPath(0);
			_setMatchWholeWord(0);
			_setRegex(0);
			_setRequestFlags(flags);
			_setMax(max);
			_setOffset(offset);

			if (_queryW(1) != 0)
				return QueryStatus.Ok;

			return _getLastError() == EverythingErrorIpc ? QueryStatus.IpcFailure : QueryStatus.Failed;
		}

		private unsafe void SetSearch(string search)
		{
			fixed (char* text = search)
				_setSearchW(text);
		}

		private bool EnsureSdk2Loaded()
		{
			if (_sdk2Ready)
				return true;

			if (_loadFailed)
				return false;

			var fileName = Sdk2FileName;
			foreach (var path in CandidatePaths(fileName, includeInstallDirectories: false))
			{
				if (!File.Exists(path))
					continue;

				try
				{
					var module = NativeLibrary.Load(path);
					BindSdk2(module);
					_sdk2Ready = true;
					_logger.LogInformation("Loaded Everything SDK from {SdkPath}.", path);
					return true;
				}
				catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException)
				{
					LogLoadFailure(ex, path);
				}
			}

			LogLoadFailure(null, fileName);
			_loadFailed = true;
			return false;
		}

		private unsafe void BindSdk2(IntPtr module)
		{
			_setSearchW = (delegate* unmanaged[Stdcall]<char*, void>)NativeLibrary.GetExport(module, "Everything_SetSearchW");
			_setMatchCase = (delegate* unmanaged[Stdcall]<int, void>)NativeLibrary.GetExport(module, "Everything_SetMatchCase");
			_setMatchPath = (delegate* unmanaged[Stdcall]<int, void>)NativeLibrary.GetExport(module, "Everything_SetMatchPath");
			_setMatchWholeWord = (delegate* unmanaged[Stdcall]<int, void>)NativeLibrary.GetExport(module, "Everything_SetMatchWholeWord");
			_setRegex = (delegate* unmanaged[Stdcall]<int, void>)NativeLibrary.GetExport(module, "Everything_SetRegex");
			_setMax = (delegate* unmanaged[Stdcall]<uint, void>)NativeLibrary.GetExport(module, "Everything_SetMax");
			_setOffset = (delegate* unmanaged[Stdcall]<uint, void>)NativeLibrary.GetExport(module, "Everything_SetOffset");
			_setRequestFlags = (delegate* unmanaged[Stdcall]<uint, void>)NativeLibrary.GetExport(module, "Everything_SetRequestFlags");
			_queryW = (delegate* unmanaged[Stdcall]<int, int>)NativeLibrary.GetExport(module, "Everything_QueryW");
			_getNumResults = (delegate* unmanaged[Stdcall]<uint>)NativeLibrary.GetExport(module, "Everything_GetNumResults");
			_getLastError = (delegate* unmanaged[Stdcall]<uint>)NativeLibrary.GetExport(module, "Everything_GetLastError");
			_isDbLoaded = (delegate* unmanaged[Stdcall]<int>)NativeLibrary.GetExport(module, "Everything_IsDBLoaded");
			_isFolderResult = (delegate* unmanaged[Stdcall]<uint, int>)NativeLibrary.GetExport(module, "Everything_IsFolderResult");
			_getResultFileNameW = (delegate* unmanaged[Stdcall]<uint, char*>)NativeLibrary.GetExport(module, "Everything_GetResultFileNameW");
			_getResultPathW = (delegate* unmanaged[Stdcall]<uint, char*>)NativeLibrary.GetExport(module, "Everything_GetResultPathW");
			_getResultSize = (delegate* unmanaged[Stdcall]<uint, long*, int>)NativeLibrary.GetExport(module, "Everything_GetResultSize");
			_getResultDateModified = (delegate* unmanaged[Stdcall]<uint, long*, int>)NativeLibrary.GetExport(module, "Everything_GetResultDateModified");
			_getResultDateCreated = (delegate* unmanaged[Stdcall]<uint, long*, int>)NativeLibrary.GetExport(module, "Everything_GetResultDateCreated");
			_getResultAttributes = (delegate* unmanaged[Stdcall]<uint, uint>)NativeLibrary.GetExport(module, "Everything_GetResultAttributes");
			_reset = (delegate* unmanaged[Stdcall]<void>)NativeLibrary.GetExport(module, "Everything_Reset");
		}

		private unsafe bool TrySdk3FolderSize(string folder, out ulong size)
		{
			size = 0;
			if (!EnsureSdk3Client())
				return false;

			ulong value;
			fixed (char* path = folder)
				value = _sdk3GetFolderSize(_sdk3Client, path);

			if (value == ulong.MaxValue)
				return false;

			size = value;
			return true;
		}

		private unsafe bool EnsureSdk3Client()
		{
			if (_sdk3Client != IntPtr.Zero)
				return true;

			if (_sdk3Missing || DateTime.UtcNow < _sdk3RetryAfter)
				return false;

			var fileName = Sdk3FileName;
			string? found = null;
			foreach (var path in CandidatePaths(fileName, includeInstallDirectories: true))
			{
				if (File.Exists(path))
				{
					found = path;
					break;
				}
			}

			if (found is null)
			{
				_sdk3Missing = true;
				LogSdk3Once("Everything 1.5 SDK was not found. Folder sizes use the SDK2 file sum.");
				return false;
			}

			try
			{
				if (_sdk3Module == IntPtr.Zero)
				{
					_sdk3Module = NativeLibrary.Load(found);
					_sdk3ConnectW = (delegate* unmanaged[Stdcall]<char*, IntPtr>)NativeLibrary.GetExport(_sdk3Module, "Everything3_ConnectW");
					_sdk3GetFolderSize = (delegate* unmanaged[Stdcall]<IntPtr, char*, ulong>)NativeLibrary.GetExport(_sdk3Module, "Everything3_GetFolderSizeFromFilenameW");
					_sdk3Destroy = (delegate* unmanaged[Stdcall]<IntPtr, int>)NativeLibrary.GetExport(_sdk3Module, "Everything3_DestroyClient");
				}

				var client = _sdk3ConnectW(null);
				if (client == IntPtr.Zero)
				{
					fixed (char* instanceName = "1.5a")
						client = _sdk3ConnectW(instanceName);
				}

				if (client == IntPtr.Zero)
				{
					_sdk3RetryAfter = DateTime.UtcNow.Add(AvailabilityCacheDuration);
					LogSdk3Once("Everything 1.5 SDK is present but did not connect.");
					return false;
				}

				_sdk3Client = client;
				_logger.LogInformation("Connected to Everything 1.5 for folder sizes.");
				return true;
			}
			catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException)
			{
				_sdk3Missing = true;
				if (!_loggedSdk3)
				{
					_loggedSdk3 = true;
					_logger.LogInformation(ex, "Everything 1.5 SDK could not be loaded.");
				}

				return false;
			}
		}

		private unsafe void DestroySdk3()
		{
			if (_sdk3Client == IntPtr.Zero)
				return;

			try
			{
				_sdk3Destroy(_sdk3Client);
			}
			catch (Exception ex)
			{
				_logger.LogDebug(ex, "Everything 1.5 client cleanup failed.");
			}

			_sdk3Client = IntPtr.Zero;
		}

		private void RememberAvailability(bool available)
		{
			_available = available;
			_availableUntil = DateTime.UtcNow.Add(AvailabilityCacheDuration);
			if (available)
			{
				if (_loggedAvailable)
					return;

				_loggedAvailable = true;
				_logger.LogInformation("Everything is available.");
				return;
			}

			if (_loggedUnavailable)
				return;

			_loggedUnavailable = true;
			_logger.LogInformation("Everything is not available. Windows search and folder sizes will be used.");
		}

		private void LogLoadFailure(Exception? exception, string path)
		{
			if (_loggedLoadFailure)
				return;

			_loggedLoadFailure = true;
			if (exception is null)
				_logger.LogWarning("Everything SDK DLL {SdkFile} was not found.", path);
			else
				_logger.LogWarning(exception, "Everything SDK at {SdkPath} could not be loaded.", path);
		}

		private void LogSdk3Once(string message)
		{
			if (_loggedSdk3)
				return;

			_loggedSdk3 = true;
			_logger.LogInformation("{Message}", message);
		}

		private static string Sdk2FileName => RuntimeInformation.ProcessArchitecture switch
		{
			Architecture.Arm64 => "EverythingARM64.dll",
			Architecture.X86 => "Everything32.dll",
			_ => Environment.Is64BitProcess ? "Everything64.dll" : "Everything32.dll",
		};

		private static string Sdk3FileName => RuntimeInformation.ProcessArchitecture switch
		{
			Architecture.Arm64 => "Everything3_ARM64.dll",
			Architecture.X86 => "Everything3_x86.dll",
			_ => "Everything3_x64.dll",
		};

		private static IEnumerable<string> CandidatePaths(string fileName, bool includeInstallDirectories)
		{
			var baseDirectory = AppContext.BaseDirectory;
			yield return Path.Combine(baseDirectory, "Libraries", fileName);
			yield return Path.Combine(baseDirectory, fileName);

			if (!includeInstallDirectories)
				yield break;

			foreach (var directory in InstalledEverythingDirectories())
				yield return Path.Combine(directory, fileName);

			var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
			var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
			foreach (var root in new[] { programFiles, programFilesX86 })
			{
				if (string.IsNullOrEmpty(root))
					continue;

				yield return Path.Combine(root, "Everything 1.5a", fileName);
				yield return Path.Combine(root, "Everything", fileName);
			}
		}

		private static IEnumerable<string> InstalledEverythingDirectories()
		{
			Process[] processes;
			try
			{
				processes = Process.GetProcessesByName("Everything");
			}
			catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
			{
				yield break;
			}

			foreach (var process in processes)
			{
				string? directory = null;
				try
				{
					directory = Path.GetDirectoryName(process.MainModule?.FileName);
				}
				catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
				{
				}
				finally
				{
					process.Dispose();
				}

				if (!string.IsNullOrEmpty(directory))
					yield return directory;
			}
		}

		private static string NormalizeFolder(string path)
		{
			var full = Path.GetFullPath(path);
			if (full.EndsWith(':'))
				return full + Path.DirectorySeparatorChar;

			return full.Length > 3
				? full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
				: full;
		}

		private static string BuildSearchQuery(string? query, string? folder)
		{
			var text = query?.Trim() ?? string.Empty;
			if (string.IsNullOrEmpty(folder) || folder.Equals("Home", StringComparison.OrdinalIgnoreCase))
				return text;

			var scope = NormalizeFolder(folder);
			if (!scope.EndsWith(Path.DirectorySeparatorChar))
				scope += Path.DirectorySeparatorChar;

			var escaped = scope.Replace("\"", "\"\"", StringComparison.Ordinal);
			return text.Length == 0 ? $"\"{escaped}\"" : $"\"{escaped}\" {text}";
		}

		private static string BuildFolderSizeQuery(string folder)
		{
			var scope = folder;
			if (!scope.EndsWith(Path.DirectorySeparatorChar))
				scope += Path.DirectorySeparatorChar;

			return $"file:\"{scope.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
		}

		private static bool IsUnderFolder(string folder, string fullPath)
		{
			string root;
			string candidate;
			try
			{
				root = Path.GetFullPath(folder);
				candidate = Path.GetFullPath(fullPath);
			}
			catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
			{
				return false;
			}

			if (root.EndsWith(':'))
				root += Path.DirectorySeparatorChar;
			else
				root = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

			var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
			return candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
		}

		private enum QueryStatus
		{
			Ok,
			IpcFailure,
			Failed,
		}
	}
}
