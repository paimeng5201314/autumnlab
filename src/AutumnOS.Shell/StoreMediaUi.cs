using AutumnOS.Store;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace AutumnOS.Shell;

public sealed partial class MainWindow
{
    private readonly Dictionary<long, int> storeVersionPages = [];
    private void AddStoreScreenshots(CatalogDetails details)
    {
        if (details.Store is not { Screenshots.Length: > 0 } store || storeCatalog is null) return;
        var strip = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        storeBody.Children.Add(new ScrollViewer { Content = strip, HorizontalScrollMode = ScrollMode.Enabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollMode = ScrollMode.Disabled, MaxHeight = 210 });
        var revision = storeRevision;
        var token = storeQuery?.Token ?? lifetime.Token;
        foreach (string url in store.Screenshots)
        {
            var status = Paragraph("正在读取截图…");
            var frame = new Border { Width = 280, Height = 175, CornerRadius = new CornerRadius(16), Child = status };
            strip.Children.Add(frame);
            _ = LoadAsync(url, frame, status);
        }
        async Task LoadAsync(string url, Border frame, TextBlock status)
        {
            try
            {
                var image = await storeCatalog.GetScreenshotAsync(details.Repository, url, token);
                if (token.IsCancellationRequested || revision != storeRevision) return;
                using var bytes = new MemoryStream(image.Bytes);
                var bitmap = new BitmapImage { DecodePixelWidth = 560 };
                await bitmap.SetSourceAsync(bytes.AsRandomAccessStream());
                if (token.IsCancellationRequested || revision != storeRevision) return;
                frame.Child = new Image { Source = bitmap, Stretch = Stretch.UniformToFill };
            }
            catch (OperationCanceledException) { }
            catch (Exception error) when (error is not OutOfMemoryException) { status.Text = "截图未能加载。"; }
        }
    }
    private async Task LoadMoreStoreVersionsAsync(CatalogDetails details)
    {
        if (storeCatalog is null || storeBusy) return;
        long revision = storeRevision;
        var token = storeQuery?.Token ?? lifetime.Token;
        storeBusy = true; storeStatus.Text = "正在读取更早的版本…";
        try
        {
            var page = await storeCatalog.GetVersionsAsync(details.Repository, details.Store,
                storeVersionPages.GetValueOrDefault(details.Repository.RepositoryId, 1) + 1, token);
            if (token.IsCancellationRequested || revision != storeRevision) return;
            storeVersionPages[details.Repository.RepositoryId] = page.Page;
            var merged = details with { Versions = details.Versions.Concat(page.Versions).DistinctBy(v => v.ReleaseId).ToArray(), HasMoreVersions = page.HasMore };
            storeDetails[details.Repository.RepositoryId] = merged;
            RenderStoreDetails(merged);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is not OutOfMemoryException) { storeStatus.Text = StoreError(SafeStoreCode(error)); }
        finally { if (revision == storeRevision) storeBusy = false; }
    }
}
