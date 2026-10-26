// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.App.Services.Search;
using Files.App.Services.SizeProvider;

namespace Files.App.Services
{
	public sealed partial class UserSizeProvider : ISizeProvider
	{
		private readonly IFoldersSettingsService folderPreferences
			= Ioc.Default.GetRequiredService<IFoldersSettingsService>();

		private ISizeProvider provider;

		public event EventHandler<SizeChangedEventArgs>? SizeChanged;

		public UserSizeProvider()
		{
			provider = GetProvider();
			provider.SizeChanged += Provider_SizeChanged;

			folderPreferences.PropertyChanged += FolderPreferences_PropertyChanged;
		}

		public Task CleanAsync()
			=> provider.CleanAsync();

		public async Task ClearAsync()
			=> await provider.ClearAsync();

		public Task UpdateAsync(string path, CancellationToken cancellationToken)
			=> provider.UpdateAsync(path, cancellationToken);

		public bool TryGetSize(string path, out ulong size)
			=> provider.TryGetSize(path, out size);

		public void Dispose()
		{
			provider.Dispose();
			folderPreferences.PropertyChanged -= FolderPreferences_PropertyChanged;
		}

		private ISizeProvider GetProvider()
		{
			if (!folderPreferences.UseEverything)
				return new DrivesSizeProvider();

			try
			{
				var everything = Ioc.Default.GetRequiredService<IEverythingService>();
				if (everything.IsAvailableAsync().GetAwaiter().GetResult())
					return new EverythingSizeProvider(everything);
			}
			catch (Exception)
			{
				// The probe failed. Keep the filesystem size provider.
			}

			return new DrivesSizeProvider();
		}

		private async void FolderPreferences_PropertyChanged(object? sender, PropertyChangedEventArgs e)
		{
			if (e.PropertyName is nameof(IFoldersSettingsService.CalculateFolderSizes) or nameof(IFoldersSettingsService.UseEverything))
			{
				await provider.ClearAsync();
				provider.SizeChanged -= Provider_SizeChanged;
				provider.Dispose();
				provider = GetProvider();
				provider.SizeChanged += Provider_SizeChanged;
			}
		}

		private void Provider_SizeChanged(object? sender, SizeChangedEventArgs e)
			=> SizeChanged?.Invoke(this, e);
	}
}
