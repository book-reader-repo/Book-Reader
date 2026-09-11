#if IOS
using Foundation;
using UIKit;
using WebKit;
using System;
using System.Threading.Tasks;
using CoreGraphics;

namespace BookViewer.Platforms.iOS
{
    public static class WebViewCapture
    {
        public static async Task<byte[]> CaptureHtmlAsync(string html, int width, int height)
        {
            var tcs = new TaskCompletionSource<byte[]>();

            var config = new WKWebViewConfiguration();
            var webView = new WKWebView(new CGRect(0, 0, width, height), config);

            var navDelegate = new CaptureNavigationDelegate(async () =>
            {
                try
                {
                    // Give rendering a moment to settle (fonts, images, SVG)
                    await Task.Delay(800);

                    var snapshotConfig = new WKSnapshotConfiguration
                    {
                        Rect = new CGRect(0, 0, width, height),
                        SnapshotWidth = NSNumber.FromDouble(width)
                    };

                    webView.TakeSnapshot(snapshotConfig, (image, error) =>
                    {
                        try
                        {
                            if (error != null || image == null)
                            {
                                tcs.TrySetResult(null);
                                return;
                            }

                            using var pngData = image.AsPNG();
                            if (pngData == null)
                            {
                                tcs.TrySetResult(null);
                                return;
                            }

                            var bytes = pngData.ToArray();
                            tcs.TrySetResult(bytes);
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"Snapshot error: {ex.Message}");
                            tcs.TrySetResult(null);
                        }
                    });
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Capture error: {ex.Message}");
                    tcs.TrySetResult(null);
                }
            });

            webView.NavigationDelegate = navDelegate;
            webView.LoadHtmlString(html, new NSUrl("file:///"));

            return await tcs.Task;
        }

        private class CaptureNavigationDelegate : WKNavigationDelegate
        {
            private readonly Func<Task> _onFinished;
            private bool _hasRun;

            public CaptureNavigationDelegate(Func<Task> onFinished)
            {
                _onFinished = onFinished;
            }

            public override async void DidFinishNavigation(WKWebView webView, WKNavigation navigation)
            {
                if (_hasRun) return;
                _hasRun = true;
                await _onFinished();
            }
        }
    }
}
#endif
