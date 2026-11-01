using System.IO;
using System.Windows.Media.Imaging;
using Windows.Data.Pdf;
using Windows.Storage;
using Windows.Storage.Streams;

namespace VFRNav.App;

/// <summary>Dessine les pages d'un PDF (carte VAC) avec le moteur PDF de Windows.</summary>
public sealed class PdfPageRenderer
{
    private string? _path;
    private PdfDocument? _document;

    public uint PageCount => _document?.PageCount ?? 0;

    public async Task<bool> OpenAsync(string path)
    {
        if (_path == path && _document is not null) return true;
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            _document = await PdfDocument.LoadFromFileAsync(file);
            _path = path;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Runtime.InteropServices.COMException)
        {
            _document = null;
            _path = null;
            return false;
        }
    }

    public async Task<BitmapImage?> RenderAsync(uint pageIndex, uint width)
    {
        if (_document is null || pageIndex >= _document.PageCount) return null;
        using var page = _document.GetPage(pageIndex);
        using var stream = new InMemoryRandomAccessStream();
        await page.RenderToStreamAsync(stream, new PdfPageRenderOptions { DestinationWidth = width });
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream.AsStreamForRead();
        image.EndInit();
        image.Freeze();
        return image;
    }
}
