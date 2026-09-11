#if ANDROID
using Android.Graphics;
using Android.Views;
using Android.Webkit;
using Microsoft.Maui.ApplicationModel;
using System;
using System.IO;
using System.Threading.Tasks;

namespace BookViewer.Platforms.Android
{
    public static class WebViewCapture
    {
        public static async Task<byte[]> CaptureHtmlAsync(string html, int width, int height)
        {
            var tcs = new TaskCompletionSource<byte[]>();

            await MainThread.InvokeOnMainThreadAsync(async () =>
            {
                global::Android.Webkit.WebView webView = null;
                try
                {
                    var context = global::Android.App.Application.Context;
                    webView = new global::Android.Webkit.WebView(context);

                    var settings = webView.Settings;
                    settings.JavaScriptEnabled = true;
                    settings.LoadsImagesAutomatically = true;
                    settings.AllowFileAccess = true;
                    settings.AllowFileAccessFromFileURLs = true;
                    settings.AllowUniversalAccessFromFileURLs = true;
                    settings.BlockNetworkLoads = false;
                    settings.SetRenderPriority(WebSettings.RenderPriority.High);

                    webView.SetBackgroundColor(Color.White);
                    webView.Layout(0, 0, width, height);
                    webView.Measure(
                        View.MeasureSpec.MakeMeasureSpec(width, MeasureSpecMode.Exactly),
                        View.MeasureSpec.MakeMeasureSpec(height, MeasureSpecMode.Exactly));
                    webView.Layout(0, 0, width, height);

                    var finishedTcs = new TaskCompletionSource<bool>();
                    var client = new CaptureWebViewClient(() => finishedTcs.TrySetResult(true));
                    webView.SetWebViewClient(client);

                    webView.LoadDataWithBaseURL("file:///android_asset/", html, "text/html", "UTF-8", null);

                    // Wait for load with timeout
                    var completed = await Task.WhenAny(finishedTcs.Task, Task.Delay(15000));
                    if (completed == finishedTcs.Task)
                    {
                        await finishedTcs.Task;
                    }

                    // Extra delay for fonts/images/SVG to render
                    await Task.Delay(800);

                    var bitmap = Bitmap.CreateBitmap(width, height, Bitmap.Config.Argb8888);
                    try
                    {
                        var canvas = new Canvas(bitmap);
                        canvas.DrawColor(Color.White);
                        webView.Draw(canvas);

                        using var ms = new MemoryStream();
                        bitmap.Compress(Bitmap.CompressFormat.Png, 100, ms);
                        var bytes = ms.ToArray();
                        tcs.TrySetResult(bytes);
                    }
                    finally
                    {
                        bitmap.Recycle();
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Capture error: {ex.Message}");
                    tcs.TrySetResult(null);
                }
                finally
                {
                    try { webView?.Destroy(); } catch { }
                }
            });

            return await tcs.Task;
        }

        private class CaptureWebViewClient : WebViewClient
        {
            private readonly Action _onFinished;
            public CaptureWebViewClient(Action onFinished) { _onFinished = onFinished; }

            public override void OnPageFinished(global::Android.Webkit.WebView view, string url)
            {
                base.OnPageFinished(view, url);
                _onFinished?.Invoke();
            }
        }
    }
}
#endif
