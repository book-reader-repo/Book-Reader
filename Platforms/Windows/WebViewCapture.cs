#if WINDOWS
using Microsoft.Maui.Controls;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using System;
using System.IO;
using System.Threading.Tasks;
using Windows.Storage.Streams;
using Windows.Graphics.Imaging;
using Windows.Graphics.Display;

namespace BookViewer.Platforms.Windows
{
    public static class WebViewCapture
    {
        public static async Task<byte[]> CaptureHtmlAsync(string html, int width, int height)
        {
            try
            {
                // Create a hidden WebView2
                var webView = new WebView2();

                var tcs = new TaskCompletionSource<bool>();
                webView.NavigationCompleted += (s, e) => tcs.TrySetResult(e.IsSuccess);

                // Prepare the environment
                var userDataFolder = Path.Combine(Path.GetTempPath(), "BookViewerWebView2");
                var env = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
                await webView.EnsureCoreWebView2Async(env);

                // Load the HTML
                webView.NavigationCompleted += (s, e) => tcs.TrySetResult(true);
                webView.NavigateToString(html);

                await tcs.Task;
                await Task.Delay(800);   // let fonts/images settle

                // Capture to stream
                using var ms = new MemoryStream();
                var stream = new InMemoryRandomAccessStream();
                await webView.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream);

                stream.Seek(0);
                var reader = new DataReader(stream.GetInputStreamAt(0));
                await reader.LoadAsync((uint)stream.Size);
                var bytes = new byte[stream.Size];
                reader.ReadBytes(bytes);

                webView.Close();
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
