#if WINDOWS
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using System;
using System.IO;
using System.Threading.Tasks;
using Windows.Storage.Streams;

namespace BookViewer.Platforms.Windows
{
    public static class WebViewCapture
    {
        public static async Task<byte[]> CaptureHtmlAsync(string html, int width, int height)
        {
            try
            {
                // Create a hidden host window
                var hostControl = new Grid { Width = width, Height = height };
                var webView = new WebView2 { Width = width, Height = height };
                hostControl.Children.Add(webView);
        
                var userDataFolder = Path.Combine(Path.GetTempPath(), "BookViewerWebView2");
                Directory.CreateDirectory(userDataFolder);
        
                var env = await CoreWebView2Environment.CreateAsync(null, userDataFolder, null);
                await webView.EnsureCoreWebView2Async(env);
        
                // Force layout
                hostControl.Measure(new Windows.Foundation.Size(width, height));
                hostControl.Arrange(new Windows.Foundation.Rect(0, 0, width, height));
                hostControl.UpdateLayout();
        
                var tcs = new TaskCompletionSource<bool>();
                webView.NavigationCompleted += (s, e) => tcs.TrySetResult(true);
                webView.NavigateToString(html);
                await tcs.Task;
                await Task.Delay(800);
        
                using var ms = new MemoryStream();
                var stream = new InMemoryRandomAccessStream();
                await webView.CoreWebView2.CapturePreviewAsync(
                    CoreWebView2CapturePreviewImageFormat.Png, stream);
        
                stream.Seek(0);
                var reader = new DataReader(stream.GetInputStreamAt(0));
                await reader.LoadAsync((uint)stream.Size);
                var bytes = new byte[stream.Size];
                reader.ReadBytes(bytes);
        
                return bytes;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Windows capture error: {ex.Message}");
                return null;
            }
        }
    }
}
#endif
