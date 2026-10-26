// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.App.Services.Search;
using System.Collections.Concurrent;
using System.IO;
using Windows.Win32;
using Windows.Win32.Storage.FileSystem;

namespace Files.App.Services.SizeProvider
{
	/// <summary>
	/// Folder sizes from Everything, with an iterative filesystem walk when Everything cannot answer.
	/// </summary>
	public sealed partial class EverythingSizeProvider : ISizeProvider
	{
		private readonly IEverythingService everything;
		private readonly ConcurrentDictionary<string, ulong> sizes = new(StringComparer.OrdinalIgnoreCase);

		public event EventHandler<SizeChangedEventArgs>? SizeChanged;

		public EverythingSizeProvider(IEverythingService everything)
		{
			this.everything = everything;
		}

		public Task CleanAsync() => Task.CompletedTask;

		public Task ClearAsync()
		{
			sizes.Clear();
			return Task.CompletedTask;
		}

		public async Task UpdateAsync(string path, CancellationToken cancellationToken)
		{
			if (string.IsNullOrEmpty(path))
				return;

			if (sizes.TryGetValue(path, out var cached))
			{
				RaiseSizeChanged(path, cached, SizeChangedValueState.Final);
				return;
			}

			RaiseSizeChanged(path, 0, SizeChangedValueState.None);

			try
			{
				var measured = await everything.GetFolderSizeAsync(path, cancellationToken);
				var size = measured.Succeeded
					? measured.Size
					: await Task.Run(() => MeasureOnDisk(path, cancellationToken), cancellationToken);

				sizes[path] = size;
				RaiseSizeChanged(path, size, SizeChangedValueState.Final);
			}
			catch (OperationCanceledException)
			{
			}
		}

		public bool TryGetSize(string path, out ulong size)
			=> sizes.TryGetValue(path, out size);

		public void Dispose()
		{
			sizes.Clear();
		}

		private ulong MeasureOnDisk(string root, CancellationToken cancellationToken)
		{
			var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			var completed = new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase);
			var stack = new Stack<Frame>();
			var rootFrame = new Frame(root) { Key = Normalize(root) };
			stack.Push(rootFrame);

			var walkedBytes = 0UL;
			var stopwatch = Stopwatch.StartNew();

			while (stack.Count > 0)
			{
				cancellationToken.ThrowIfCancellationRequested();
				var frame = stack.Peek();
				if (!frame.Expanded)
				{
					frame.Expanded = true;
					if (!seen.Add(frame.Key))
					{
						stack.Pop();
						continue;
					}

					Expand(frame);
					walkedBytes += frame.FileBytes;
					for (var index = frame.Children.Count - 1; index >= 0; index--)
					{
						var child = frame.Children[index];
						child.Key = Normalize(child.Path);
						if (!completed.TryGetValue(child.Key, out var known))
							sizes.TryGetValue(child.Path, out known);

						if (known > 0 || completed.ContainsKey(child.Key) || sizes.ContainsKey(child.Path))
						{
							frame.FileBytes += known;
							seen.Add(child.Key);
							frame.Children.RemoveAt(index);
						}
						else if (seen.Contains(child.Key))
						{
							frame.Children.RemoveAt(index);
						}
						else
						{
							stack.Push(child);
						}
					}

					if (stopwatch.ElapsedMilliseconds > 500)
					{
						stopwatch.Restart();
						RaiseSizeChanged(root, walkedBytes, SizeChangedValueState.Intermediate);
					}
				}
				else
				{
					stack.Pop();
					var total = frame.FileBytes;
					foreach (var child in frame.Children)
						total += child.Total;

					frame.Total = total;
					completed[frame.Key] = total;
					sizes[frame.Path] = total;
				}
			}

			return completed.TryGetValue(rootFrame.Key, out var rootTotal) ? rootTotal : 0;
		}

		private unsafe void Expand(Frame frame)
		{
			WIN32_FIND_DATAW initialFindData = default;
			var handle = PInvoke.FindFirstFileEx(
				$"{frame.Path}{Path.DirectorySeparatorChar}*.*",
				FINDEX_INFO_LEVELS.FindExInfoBasic,
				&initialFindData,
				FINDEX_SEARCH_OPS.FindExSearchNameMatch,
				FIND_FIRST_EX_FLAGS.FIND_FIRST_EX_LARGE_FETCH);
			using var handleScope = handle;
			if (handle.IsInvalid)
				return;

			var findData = initialFindData;
			do
			{
				if (((FileAttributes)findData.dwFileAttributes & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint)
					continue;

				var name = findData.cFileName.ToString() ?? string.Empty;
				var isDirectory = ((FileAttributes)findData.dwFileAttributes & FileAttributes.Directory) == FileAttributes.Directory;
				if (isDirectory)
				{
					if (name is "." or "..")
						continue;

					frame.Children.Add(new Frame(Path.Combine(frame.Path, name)));
				}
				else
				{
					var length = findData.GetSize();
					if (length > 0)
						frame.FileBytes += (ulong)length;
				}
			} while (PInvoke.FindNextFile(handle, out findData));
		}

		private static string Normalize(string path)
		{
			try
			{
				var full = Path.GetFullPath(path);
				if (full.Length > 3)
					full = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

				return full;
			}
			catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
			{
				return path;
			}
		}

		private void RaiseSizeChanged(string path, ulong newSize, SizeChangedValueState valueState)
			=> SizeChanged?.Invoke(this, new SizeChangedEventArgs(path, newSize, valueState));

		private sealed class Frame
		{
			public Frame(string path) => Path = path;

			public string Path { get; }

			public string Key { get; set; } = string.Empty;

			public bool Expanded { get; set; }

			public ulong FileBytes { get; set; }

			public ulong Total { get; set; }

			public List<Frame> Children { get; } = new();
		}
	}
}
