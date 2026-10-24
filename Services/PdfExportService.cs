using iText.Html2pdf;
using iText.Html2pdf.Resolver.Font;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Utils;
using iText.Layout.Font;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Path = System.IO.Path;

namespace BookViewer.Services
{
    public class PdfExportService
    {
        private readonly BookService _bookService;

        public event EventHandler<int>? OnProgress;
        public event EventHandler<string>? OnComplete;
        public event EventHandler<string>? OnError;

        private string? _extractedFontPath;

        public PdfExportService(BookService bookService)
        {
            _bookService = bookService;
        }

        public async Task ExportBookAsPdfAsync(
            string outputPath,
            bool includeAnswers,
            bool includeTeacherNotes,
            bool includeStudentAnswers)
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "BookViewerPdfExport");

            try
            {
                var totalPages = _bookService.PageFiles.Count;
                if (totalPages == 0)
                {
                    OnError?.Invoke(this, "No pages to export");
                    return;
                }

                if (Directory.Exists(tempDir))
                {
                    try { Directory.Delete(tempDir, true); } catch { }
                }
                Directory.CreateDirectory(tempDir);

                var pageSize = new PageSize(768f, 1008f);

                // We will store each page's PDF in memory to avoid file locking issues
                var pdfStreams = new List<byte[]>();

                for (int i = 0; i < totalPages; i++)
                {
                    OnProgress?.Invoke(this, (int)((i / (double)totalPages) * 100));

                    var filePath = _bookService.PageFiles[i];

                    string html = await _bookService.BuildPageHtmlForRenderAsync(
                        filePath, includeAnswers, includeTeacherNotes, includeStudentAnswers);

                    html = InjectPdfCss(html);
                    html = await ExtractBase64ImagesAsync(html, tempDir, i);

                    var fontProvider = await BuildFontProviderAsync(html, tempDir);
                    var converterProperties = new ConverterProperties();
                    converterProperties.SetFontProvider(fontProvider);
                    converterProperties.SetCreateAcroForm(false);

                    // Convert to PDF in memory
                    using (var ms = new MemoryStream())
                    {
                        using (var writer = new PdfWriter(ms))
                        using (var pdf = new PdfDocument(writer))
                        {
                            pdf.SetDefaultPageSize(pageSize);
                            HtmlConverter.ConvertToPdf(html, pdf, converterProperties);
                        }
                        
                        // Save the generated PDF bytes
                        pdfStreams.Add(ms.ToArray());
                    }
                }

                OnProgress?.Invoke(this, 100);

                // Merge all in-memory PDFs into the final output file
                using (var writer = new PdfWriter(outputPath))
                using (var mergedPdf = new PdfDocument(writer))
                {
                    var merger = new PdfMerger(mergedPdf);
                    foreach (var pdfBytes in pdfStreams)
                    {
                        using var ms = new MemoryStream(pdfBytes);
                        using var reader = new PdfReader(ms);
                        using var sourcePdf = new PdfDocument(reader);
                        merger.Merge(sourcePdf, 1, sourcePdf.GetNumberOfPages());
                    }
                }

                OnComplete?.Invoke(this, outputPath);
            }
            catch (Exception ex)
            {
                OnError?.Invoke(this, ex.Message);
            }
            finally
            {
                // Cleanup temporary images and fonts
                try
                {
                    if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
                }
                catch { }
            }
        }

        private async Task<FontProvider> BuildFontProviderAsync(string html, string tempDir)
        {
            var provider = new DefaultFontProvider(false, false, false);
            bool anyRegistered = false;

            foreach (Match fontFace in Regex.Matches(html, @"@font-face\s*\{[^}]*\}", RegexOptions.IgnoreCase))
            {
                var base64Match = Regex.Match(fontFace.Value, @"base64,([^""']+)\)", RegexOptions.IgnoreCase);
                if (!base64Match.Success) continue;

                try
                {
                    var fontBytes = Convert.FromBase64String(base64Match.Groups[1].Value);

                    if (_extractedFontPath == null || !File.Exists(_extractedFontPath))
                    {
                        _extractedFontPath = Path.Combine(tempDir, "book_font.ttf");
                        await File.WriteAllBytesAsync(_extractedFontPath, fontBytes);
                    }

                    provider.AddFont(_extractedFontPath);
                    anyRegistered = true;
                }
                catch { }

                if (anyRegistered) break;
            }

            if (!anyRegistered)
            {
                provider = new DefaultFontProvider(true, true, true);
            }

            return provider;
        }

        private string InjectPdfCss(string html)
        {
            var css = @"
<style>
    @page { size: 768pt 1008pt; margin: 0 !important; }
    html, body { margin: 0 !important; padding: 0 !important; width: 768pt !important; height: 1008pt !important; overflow: visible !important; background: #fff !important; }
    .page-container { position: relative !important; width: 768pt !important; height: 1008pt !important; transform: scale(1) !important; transform-origin: top left !important; margin: 0 !important; }
    img { max-width: 100% !important; height: auto !important; }
</style>
";
            if (html.Contains("<head>"))
                return html.Replace("<head>", "<head>" + css);
            return css + html;
        }

        private async Task<string> ExtractBase64ImagesAsync(string html, string tempDir, int pageIndex)
        {
            var matches = Regex.Matches(html,
                @"src=""(data:image/([^;]+);base64,([^""]+))""",
                RegexOptions.IgnoreCase);

            foreach (Match match in matches)
            {
                var dataUri = match.Groups[1].Value;
                var ext = match.Groups[2].Value;
                var base64 = match.Groups[3].Value;

                var fileName = $"img_{pageIndex}_{Guid.NewGuid():N}.{ext}";
                var filePath = Path.Combine(tempDir, fileName);

                try
                {
                    await File.WriteAllBytesAsync(filePath, Convert.FromBase64String(base64));
                    html = html.Replace(dataUri, filePath.Replace('\\', '/'));
                }
                catch { }
            }
            return html;
        }
    }
}
