#if ANDROID
using Android.Content;
using Microsoft.Maui.ApplicationModel;
using System;
using System.IO;

namespace BookViewer.Platforms.Android
{
    public static class FileOpener
    {
        public static bool OpenFile(string filePath)
        {
            try
            {
                var context = global::Android.App.Application.Context;
                var file = new Java.IO.File(filePath);
                if (!file.Exists()) return false;

                var uri = AndroidX.Core.Content.FileProvider.GetUriForFile(
                    context,
                    context.PackageName + ".fileprovider",
                    file);

                var mime = GetMimeType(filePath);
                var intent = new Intent(Intent.ActionView);
                intent.SetDataAndType(uri, mime);
                intent.AddFlags(ActivityFlags.GrantReadUriPermission);
                intent.AddFlags(ActivityFlags.NewTask);

                context.StartActivity(intent);
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Android file open failed: {ex.Message}");
                return false;
            }
        }

        private static string GetMimeType(string path)
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            return ext switch
            {
                ".pdf" => "application/pdf",
                ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
                ".doc" => "application/msword",
                ".pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
                ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                ".html" => "text/html",
                ".htm" => "text/html",
                ".mp3" => "audio/mpeg",
                ".m4a" => "audio/mp4",
                ".mp4" => "video/mp4",
                ".txt" => "text/plain",
                _ => "*/*"
            };
        }
    }
}
#endif
