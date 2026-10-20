using BookViewer.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml;

namespace BookViewer.Services
{
    public class ResourceService
    {
        /// <summary>
        /// Parses steps_N_data.xml to extract resources (audio/video/docs) for a given page.
        /// Resolves %RESOURCES% (and its URL-encoded form %25RESOURCES%25) against the
        /// shared Books/resources folder first, then per-book fallbacks.
        /// </summary>
        public List<ResourceData> LoadResourcesForStepFile(string stepFilePath, string bookRoot)
        {
            var result = new List<ResourceData>();

            try
            {
                var dir = Path.GetDirectoryName(stepFilePath) ?? "";
                var name = Path.GetFileNameWithoutExtension(stepFilePath);
                var dataXmlPath = Path.Combine(dir, name + "_data.xml");

                if (!File.Exists(dataXmlPath))
                    return result;

                var doc = new XmlDocument();
                doc.LoadXml(File.ReadAllText(dataXmlPath));

                var items = doc.SelectNodes("//links/item");
                if (items == null) return result;

                // Shared resources root: <Documents>/BookViewer/Books/resources
                var booksRoot = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    "BookViewer", "Books");
                var sharedResources = Path.Combine(booksRoot, "resources");

                foreach (XmlNode item in items)
                {
                    var path = item.SelectSingleNode("path")?.InnerText ?? "";
                    var desc = item.SelectSingleNode("desc")?.InnerText ?? "";
                    var icon = item.SelectSingleNode("icon")?.InnerText ?? "";
                    var x = item.SelectSingleNode("x")?.InnerText ?? "";
                    var y = item.SelectSingleNode("y")?.InnerText ?? "";

                    // URL decode (%20 -> space, etc.)
                    path = Uri.UnescapeDataString(path);
                    desc = Uri.UnescapeDataString(desc);

                    // Resolve %RESOURCES% / %25RESOURCES%25 to a real path.
                    path = ResolveResourcesPlaceholder(path, bookRoot, sharedResources);

                    string type = "link";
                    var lowerPath = path.ToLower();
                    if (lowerPath.EndsWith(".mp3") || lowerPath.EndsWith(".m4a") || lowerPath.EndsWith(".wav"))
                        type = "audio";
                    else if (lowerPath.EndsWith(".mp4") || lowerPath.EndsWith(".mov") || lowerPath.EndsWith(".mkv"))
                        type = "video";
                    else if (lowerPath.EndsWith(".pdf"))
                        type = "pdf";
                    else if (lowerPath.EndsWith(".docx") || lowerPath.EndsWith(".doc") || lowerPath.EndsWith(".pptx") || lowerPath.EndsWith(".xlsx"))
                        type = "doc";
                    else if (lowerPath.StartsWith("http"))
                        type = "web";

                    result.Add(new ResourceData
                    {
                        Type = type,
                        Description = desc,
                        Path = path,
                        Icon = icon,
                        PageNumber = "" // computed later from step index
                    });
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"ResourceService error: {ex.Message}");
            }

            return result;
        }

        /// <summary>
        /// Resolves a %RESOURCES% (or %25RESOURCES%25) placeholder to a real disk path.
        /// Order of candidates:
        ///   1. Shared Books/resources/<rel>
        ///   2. <bookRoot>/resources/<rel>
        ///   3. <bookRoot>/resource/<rel>
        ///   4. <bookRoot>/<rel>
        /// If nothing exists on disk, the ORIGINAL string is returned unchanged so
        /// downstream code (FallbackUrl / remote URL handling) can still attempt it.
        /// </summary>
        private static string ResolveResourcesPlaceholder(string url, string bookRoot, string sharedResources)
        {
            if (string.IsNullOrEmpty(url)) return url;

            // Normalize the double-encoded form first.
            var decoded = url;
            if (decoded.Contains("%25RESOURCES%25"))
                decoded = decoded.Replace("%25RESOURCES%25", "%RESOURCES%");

            if (!decoded.Contains("%RESOURCES%"))
                return url; // nothing to resolve

            var rel = decoded
                .Replace("%RESOURCES%/", "")
                .Replace("%RESOURCES%", "")
                .TrimStart('/');

            if (string.IsNullOrEmpty(rel))
                return url;

            var relNative = rel.Replace('/', Path.DirectorySeparatorChar);

            var candidates = new[]
            {
                Path.Combine(sharedResources, relNative),
                Path.Combine(bookRoot, "resources", relNative),
                Path.Combine(bookRoot, "resource", relNative),
                Path.Combine(bookRoot, relNative),
            };

            foreach (var c in candidates)
            {
                try
                {
                    var disk = Path.GetFullPath(c);
                    if (File.Exists(disk))
                        return disk;
                }
                catch { }
            }

            // Not found anywhere — return the original (untouched) URL so the
            // caller can still surface a fallback URL or report it.
            return url;
        }
    }
}
