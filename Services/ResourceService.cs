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

                    // Resolve %RESOURCES% to a book-local folder if present
                    if (path.Contains("%25RESOURCES%25") || path.Contains("%RESOURCES%"))
                    {
                        path = path.Replace("%25RESOURCES%25/", "resource/")
                                   .Replace("%25RESOURCES%25", "resource")
                                   .Replace("%RESOURCES%/", "resource/")
                                   .Replace("%RESOURCES%", "resource");
                        var localPath = Path.Combine(bookRoot, path);
                        path = File.Exists(localPath) ? localPath : path;
                    }

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
    }
}
