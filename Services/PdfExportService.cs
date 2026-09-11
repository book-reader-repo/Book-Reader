using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace BookViewer.Services
{
    public class PdfExportService
    {
        private readonly BookService _bookService;

        public event EventHandler<int> OnProgress;
        public event EventHandler<string> OnComplete;
        public event EventHandler<string> OnError;

        public PdfExportService(BookService bookService)
        {
            _bookService = bookService;
        }

        public async Task ExportBookAsPdfAsync(string outputPath, bool includeAnswers, bool includeTeacherNotes, bool includeStudentAnswers)
        {
            try
            {
                var totalPages = _bookService.PageFiles.Count;
                if (totalPages == 0)
                {
                    OnError?.Invoke(this, "No pages to export");
                    return;
                }

                // Create HTML document with all pages
                var htmlBuilder = new StringBuilder();
                htmlBuilder.AppendLine("<!DOCTYPE html>");
                htmlBuilder.AppendLine("<html><head>");
                htmlBuilder.AppendLine("<meta charset='UTF-8'>");
                htmlBuilder.AppendLine("<title>Book Export</title>");
                htmlBuilder.AppendLine("<style>");
                htmlBuilder.AppendLine("@page { size: 1024px 1344px; margin: 0; }");
                htmlBuilder.AppendLine("body { margin: 0; padding: 0; background: #fff; }");
                htmlBuilder.AppendLine(".page { width: 1024px; height: 1344px; page-break-after: always; position: relative; overflow: hidden; }");
                htmlBuilder.AppendLine(".page:last-child { page-break-after: auto; }");
                htmlBuilder.AppendLine("</style>");
                htmlBuilder.AppendLine("</head><body>");

                for (int i = 0; i < totalPages; i++)
                {
                    OnProgress?.Invoke(this, (int)((i / (double)totalPages) * 100));

                    var filePath = _bookService.PageFiles[i];
                    
                    // Get the base content
                    var baseContent = await GetBaseContentAsync(filePath);
                    var bgImage = _bookService.GetStepBackgroundImage(filePath);
                    var directory = Path.GetDirectoryName(filePath) ?? "";
                    var fontCss = await _bookService.GetFontCssWithEmbeddedFonts(directory);

                    // Get answers if needed
                    string teacherContent = "";
                    string studentContent = "";
                    
                    if (includeAnswers)
                    {
                        if (includeTeacherNotes)
                        {
                            teacherContent = await _bookService.GetRedAnswerContentAsync(filePath, "teacherNotes");
                        }
                        if (includeStudentAnswers)
                        {
                            studentContent = await _bookService.GetRedAnswerContentAsync(filePath, "studentAnswers");
                        }
                    }

                    htmlBuilder.AppendLine($"<div class='page'>");
                    htmlBuilder.AppendLine($"<style>{fontCss}</style>");
                    
                    if (!string.IsNullOrEmpty(bgImage))
                    {
                        htmlBuilder.AppendLine($"<img src='{bgImage}' style='position:absolute;top:0;left:0;width:100%;height:100%;object-fit:contain;z-index:1;' />");
                    }
                    
                    htmlBuilder.AppendLine($"<div style='position:absolute;top:0;left:0;width:100%;height:100%;z-index:5;'>");
                    htmlBuilder.AppendLine(baseContent);
                    htmlBuilder.AppendLine("</div>");

                    if (!string.IsNullOrEmpty(teacherContent))
                    {
                        htmlBuilder.AppendLine($"<div style='position:absolute;top:0;left:0;width:100%;height:100%;z-index:10;'>");
                        htmlBuilder.AppendLine(teacherContent);
                        htmlBuilder.AppendLine("</div>");
                    }

                    if (!string.IsNullOrEmpty(studentContent))
                    {
                        htmlBuilder.AppendLine($"<div style='position:absolute;top:0;left:0;width:100%;height:100%;z-index:15;'>");
                        htmlBuilder.AppendLine(studentContent);
                        htmlBuilder.AppendLine("</div>");
                    }

                    htmlBuilder.AppendLine("</div>");
                }

                htmlBuilder.AppendLine("</body></html>");

                OnProgress?.Invoke(this, 90);

                // Save HTML file (user can print to PDF from browser)
                var htmlPath = outputPath.Replace(".pdf", ".html");
                await File.WriteAllTextAsync(htmlPath, htmlBuilder.ToString());

                OnProgress?.Invoke(this, 100);
                OnComplete?.Invoke(this, htmlPath);
            }
            catch (Exception ex)
            {
                OnError?.Invoke(this, ex.Message);
            }
        }

        private async Task<string> GetBaseContentAsync(string filePath)
        {
            try
            {
                var directory = Path.GetDirectoryName(filePath) ?? "";
                var fileName = Path.GetFileName(filePath) ?? "";
                var nameWithoutExt = Path.GetFileNameWithoutExtension(fileName);

                // Try _ori.html first
                string oriHtmlPath = Path.Combine(directory, nameWithoutExt + "_ori.html");
                if (File.Exists(oriHtmlPath))
                {
                    var content = await File.ReadAllTextAsync(oriHtmlPath);
                    var bodyMatch = Regex.Match(content, @"<body[^>]*>([\s\S]*?)</body>", RegexOptions.IgnoreCase);
                    return bodyMatch.Success ? bodyMatch.Groups[1].Value : content;
                }

                // Try _base.html
                string baseHtmlPath = Path.Combine(directory, nameWithoutExt + "_base.html");
                if (File.Exists(baseHtmlPath))
                {
                    return await File.ReadAllTextAsync(baseHtmlPath);
                }

                // Try _para.xml
                string paraXmlPath = Path.Combine(directory, nameWithoutExt + "_para.xml");
                if (File.Exists(paraXmlPath))
                {
                    var paraContent = await File.ReadAllTextAsync(paraXmlPath);
                    var doc = new System.Xml.XmlDocument();
                    doc.LoadXml(paraContent);
                    var parasNode = doc.SelectSingleNode("//paras");
                    if (parasNode != null)
                    {
                        var result = "";
                        foreach (System.Xml.XmlNode child in parasNode.ChildNodes)
                        {
                            if (child.Name == "para")
                            {
                                var text = child.InnerText;
                                var style = child.Attributes?["style"]?.Value ?? "";
                                result += $"<div class='para' style='{style}'>{text}</div>";
                            }
                        }
                        return result;
                    }
                }

                return "<div style='padding:20px;'>Content not available</div>";
            }
            catch
            {
                return "<div style='padding:20px;'>Content not available</div>";
            }
        }
    }
}
