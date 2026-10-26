// Copyright (c) Files Community
// Licensed under the MIT License.

namespace Files.App.Services.Search
{
	/// <summary>
	/// Client for the voidtools Everything SDK. The service is safe to resolve when Everything is not installed.
	/// </summary>
	public interface IEverythingService
	{
		/// <summary>
		/// Gets whether an Everything instance answered a recent probe. The result is cached briefly.
		/// A missing SDK DLL is remembered for the process and logged once.
		/// </summary>
		Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default);

		/// <summary>
		/// Searches Everything. An empty list means no hits or that Everything did not answer.
		/// </summary>
		/// <param name="folder"><c>null</c> or <c>Home</c> searches without a path scope.</param>
		/// <param name="maxResults">Clamped to 1..1000.</param>
		Task<IReadOnlyList<EverythingSearchHit>> SearchAsync(string? query, string? folder, int maxResults, CancellationToken cancellationToken = default);

		/// <summary>
		/// Sums indexed file sizes under <paramref name="path"/>, or reads an Everything 1.5 folder size when that SDK is installed.
		/// <see cref="EverythingFolderSize.Succeeded"/> is false when the size is unknown. Zero with success is an empty folder.
		/// </summary>
		Task<EverythingFolderSize> GetFolderSizeAsync(string path, CancellationToken cancellationToken = default);
	}

	/// <summary>
	/// One Everything search hit. Dates are UTC file times.
	/// </summary>
	/// <param name="FullPath">Full path of the file or folder.</param>
	/// <param name="Name">File or folder name.</param>
	/// <param name="IsFolder">True when the hit is a folder.</param>
	/// <param name="Size">File size in bytes. Zero for folders.</param>
	/// <param name="DateModified">UTC last-write time, when Everything returned one.</param>
	/// <param name="DateCreated">UTC creation time, when Everything returned one.</param>
	/// <param name="IsHidden">True when the hidden attribute is set.</param>
	public readonly record struct EverythingSearchHit(
		string FullPath,
		string Name,
		bool IsFolder,
		long Size,
		DateTime? DateModified,
		DateTime? DateCreated,
		bool IsHidden);

	/// <summary>
	/// Folder size from Everything.
	/// </summary>
	/// <param name="Succeeded">False when the size is unknown and must not be shown as zero.</param>
	/// <param name="Size">Byte count. Meaningful only when <paramref name="Succeeded"/> is true.</param>
	public readonly record struct EverythingFolderSize(bool Succeeded, ulong Size);
}
